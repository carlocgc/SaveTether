using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace SaveTether.Core.FileSystem;

/// <summary>What kind of network location a path is on. Always <see cref="None"/> off Windows.</summary>
public enum NetworkKind
{
    /// <summary>A local path.</summary>
    None,

    /// <summary>A <c>\\server\share</c> path, or its <c>\\?\UNC\</c> long form.</summary>
    Unc,

    /// <summary>A drive letter mapped to a share (<c>net use Z: \\server\share</c>).</summary>
    MappedDrive,

    /// <summary>A drive letter made with <c>subst</c> from a share. A <c>subst</c> of a local folder is <see cref="None"/>.</summary>
    Subst,
}

/// <summary>Windows lookups behind <see cref="PathRules.NetworkKind"/>: mapped letters, <c>subst</c>, the symlink policy, a bounded probe.</summary>
internal static unsafe partial class NetworkPaths
{
    private const uint NoError = 0;
    private const uint ConnectionUnavailable = 1201;
    private const int BufferChars = 1024;
    private const string SubstUncPrefix = @"\??\UNC\";
    private const string SubstPrefix = @"\??\";

    internal static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The share a mapped drive letter ("Z:") points to, or null when it is not a mapped drive.</summary>
    internal static string? MappedShare(string driveLetter)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var buffer = stackalloc char[BufferChars];
        var length = (uint)BufferChars;
        // A remembered drive whose share is down answers ConnectionUnavailable and still names the share.
        return WNetGetConnection(driveLetter, buffer, ref length) is NoError or ConnectionUnavailable && new string(buffer) is { Length: > 0 } share ? share : null;
    }

    /// <summary>The <c>\\server\share</c> a <c>subst</c> letter ("Z:") stands for, or null when it is not a subst of a share.</summary>
    internal static string? SubstShare(string driveLetter)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var buffer = stackalloc char[BufferChars];
        var count = QueryDosDevice(driveLetter, buffer, BufferChars);
        var target = count == 0 ? "" : new string(buffer);
        return target.StartsWith(SubstUncPrefix, StringComparison.OrdinalIgnoreCase) ? @"\\" + target[SubstUncPrefix.Length..] : null;
    }

    /// <summary>The drive letter ("Z:") a path starts with, or null.</summary>
    internal static string? DriveLetterOf(string path) =>
        path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':' ? path[..2].ToUpperInvariant() : null;

    /// <summary>
    /// Decides the kind from the path's text and two lookups, which tests replace. A mapped letter is told from
    /// a <c>subst</c> one by asking the subst lookup first: <c>subst</c> drives can report as network drives.
    /// </summary>
    internal static NetworkKind Kind(string path, Func<string, string?> mappedShare, Func<string, string?> substShare)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!OperatingSystem.IsWindows())
        {
            return NetworkKind.None;
        }

        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return NetworkKind.Unc;
        }

        // \\?\C:\... and \\.\C:\... are the long forms of a local path; any other \\ path (DFS, WebDAV) is a share.
        var local = path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal) ? path[4..] : path;
        if (local.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return NetworkKind.Unc;
        }

        return DriveLetterOf(local) is { } letter
            ? substShare(letter) is not null ? NetworkKind.Subst : mappedShare(letter) is not null ? NetworkKind.MappedDrive : NetworkKind.None
            : NetworkKind.None;
    }

    /// <summary>
    /// The path with a mapped or <c>subst</c> drive letter replaced by its share, so it holds under any logon
    /// session. Other paths come back unchanged.
    /// </summary>
    internal static string ToUncIfMapped(string path, Func<string, string?> mappedShare, Func<string, string?> substShare)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!OperatingSystem.IsWindows() || DriveLetterOf(path) is not { } letter)
        {
            return path;
        }

        var share = substShare(letter) ?? mappedShare(letter);
        return share is null ? path : Path.TrimEndingDirectorySeparator(share) + path[2..];
    }

    /// <summary>
    /// False when <paramref name="path"/> does not answer within <paramref name="timeout"/>. A share that is not
    /// there can stall a plain existence check for a long time; this gives up and reports it unreachable instead.
    /// shortcut: the abandoned check keeps running on a pool thread until Windows gives up, replace with a cancellable call if probes become frequent.
    /// </summary>
    internal static bool IsReachable(string path, TimeSpan? timeout = null, Func<string, bool>? exists = null)
    {
        var check = exists ?? Directory.Exists;
        var task = Task.Run(() =>
        {
            try
            {
                return check(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        });
        return task.Wait(timeout ?? DefaultProbeTimeout) && task.Result;
    }

    [LibraryImport("mpr.dll", EntryPoint = "WNetGetConnectionW", StringMarshalling = StringMarshalling.Utf16)]
    [SupportedOSPlatform("windows")]
    private static partial uint WNetGetConnection(string localName, char* remoteName, ref uint length);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", StringMarshalling = StringMarshalling.Utf16)]
    [SupportedOSPlatform("windows")]
    private static partial uint QueryDosDevice(string deviceName, char* target, uint maxChars);
}

/// <summary>The Windows policy for following a symbolic link from this machine to a share.</summary>
public static class SymlinkPolicy
{
    private const string FileSystemKey = @"SYSTEM\CurrentControlSet\Control\FileSystem";
    private const string ValueName = "SymlinkLocalToRemoteEvaluation";

    /// <summary>
    /// True when Windows follows a symlink that lives here and points to a share (<c>fsutil behavior query
    /// SymlinkEvaluation</c>, "Local to remote"). On by default; policy can switch it off. Always true off
    /// Windows, where it does not apply.
    /// </summary>
    public static bool LocalToRemoteAllowed() => !OperatingSystem.IsWindows() || FromRegistry();

    /// <summary>The registry value to the answer: absent means the default, on; 0 is off.</summary>
    internal static bool Allowed(object? value) => value is not int enabled || enabled != 0;

    [SupportedOSPlatform("windows")]
    private static bool FromRegistry()
    {
        using var key = Registry.LocalMachine.OpenSubKey(FileSystemKey);
        return Allowed(key?.GetValue(ValueName));
    }
}
