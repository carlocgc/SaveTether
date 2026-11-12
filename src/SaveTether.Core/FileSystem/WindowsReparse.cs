using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SaveTether.Core.FileSystem;

/// <summary>
/// Reads reparse points and creates junctions on Windows. .NET has no API to create a
/// junction, and its link APIs do not say whether a link is a symlink or a junction.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class WindowsReparse
{
    internal const uint TagMountPoint = 0xA0000003;
    internal const uint TagSymlink = 0xA000000C;

    private const uint FsctlSetReparsePoint = 0x000900A4;
    private const uint FsctlGetReparsePoint = 0x000900A8;
    private const uint GenericWrite = 0x40000000;
    private const uint ShareReadWriteDelete = 0x7;
    private const uint OpenExisting = 3;
    private const uint FlagBackupSemantics = 0x02000000;
    private const uint FlagOpenReparsePoint = 0x00200000;
    private const int MaxReparseBuffer = 16 * 1024;
    private const string NtPrefix = @"\??\";

    /// <summary>What a reparse point says about itself.</summary>
    /// <param name="Tag">The reparse tag.</param>
    /// <param name="Target">The stored target for symlinks and mount points, without the NT prefix; null for other tags.</param>
    /// <param name="IsRelative">True for a symlink whose target is relative to the link's folder.</param>
    internal readonly record struct ReparseInfo(uint Tag, string? Target, bool IsRelative);

    /// <summary>Reads the reparse point at <paramref name="path"/> itself, never its target.</summary>
    internal static ReparseInfo Read(string path)
    {
        using var handle = Open(path, access: 0);
        var buffer = new byte[MaxReparseBuffer];
        fixed (byte* p = buffer)
        {
            if (!DeviceIoControl(handle, FsctlGetReparsePoint, null, 0, p, MaxReparseBuffer, out _, IntPtr.Zero))
            {
                throw Failure("read the link at", path);
            }
        }

        var tag = BitConverter.ToUInt32(buffer, 0);
        if (tag != TagMountPoint && tag != TagSymlink)
        {
            return new ReparseInfo(tag, null, false);
        }

        // Both layouts start: tag(4) dataLength(2) reserved(2) substituteOffset(2) substituteLength(2)
        // printOffset(2) printLength(2). A symlink then has flags(4) before the path buffer.
        int substituteOffset = BitConverter.ToUInt16(buffer, 8);
        int substituteLength = BitConverter.ToUInt16(buffer, 10);
        var isSymlink = tag == TagSymlink;
        var pathBuffer = isSymlink ? 20 : 16;
        var isRelative = isSymlink && (BitConverter.ToUInt32(buffer, 16) & 1) != 0;

        var target = Encoding.Unicode.GetString(buffer, pathBuffer + substituteOffset, substituteLength);
        if (target.StartsWith(NtPrefix, StringComparison.Ordinal))
        {
            target = target[NtPrefix.Length..];
        }

        return new ReparseInfo(tag, target, isRelative);
    }

    /// <summary>
    /// Turns the existing, empty directory <paramref name="directory"/> into a junction to
    /// <paramref name="absoluteTarget"/>. Needs no privilege.
    /// </summary>
    internal static void SetJunction(string directory, string absoluteTarget)
    {
        var substitute = Encoding.Unicode.GetBytes(NtPrefix + absoluteTarget);
        var print = Encoding.Unicode.GetBytes(absoluteTarget);

        // Mount point data: four 16-bit fields, then both names, each followed by a 16-bit null.
        var dataLength = 8 + substitute.Length + 2 + print.Length + 2;
        if (dataLength + 8 > MaxReparseBuffer)
        {
            throw new SafeFsException($"The link target is too long for a junction: {absoluteTarget}");
        }

        var buffer = new byte[8 + dataLength];
        BitConverter.TryWriteBytes(buffer.AsSpan(0), TagMountPoint);
        BitConverter.TryWriteBytes(buffer.AsSpan(4), (ushort)dataLength);
        BitConverter.TryWriteBytes(buffer.AsSpan(8), (ushort)0);
        BitConverter.TryWriteBytes(buffer.AsSpan(10), (ushort)substitute.Length);
        BitConverter.TryWriteBytes(buffer.AsSpan(12), (ushort)(substitute.Length + 2));
        BitConverter.TryWriteBytes(buffer.AsSpan(14), (ushort)print.Length);
        substitute.CopyTo(buffer, 16);
        print.CopyTo(buffer, 16 + substitute.Length + 2);

        using var handle = Open(directory, GenericWrite);
        fixed (byte* p = buffer)
        {
            if (!DeviceIoControl(handle, FsctlSetReparsePoint, p, (uint)buffer.Length, null, 0, out _, IntPtr.Zero))
            {
                throw Failure("create a junction at", directory);
            }
        }
    }

    private static SafeFileHandle Open(string path, uint access)
    {
        var handle = CreateFile(
            ToExtendedPath(path), access, ShareReadWriteDelete, IntPtr.Zero, OpenExisting,
            FlagBackupSemantics | FlagOpenReparsePoint, IntPtr.Zero);

        if (handle.IsInvalid)
        {
            var failure = Failure("open", path);
            handle.Dispose();
            throw failure;
        }

        return handle;
    }

    /// <summary>The \\?\ form, so paths over 260 characters work without a manifest.</summary>
    private static string ToExtendedPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return full;
        }

        return full.StartsWith(@"\\", StringComparison.Ordinal)
            ? string.Concat(@"\\?\UNC\", full.AsSpan(2))
            : @"\\?\" + full;
    }

    private static SafeFsException Failure(string action, string path)
    {
        var error = Marshal.GetLastPInvokeError();
        return new SafeFsException($"Could not {action} '{path}': {new Win32Exception(error).Message} (Windows error {error}).");
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(
        SafeFileHandle device, uint controlCode, byte* inBuffer, uint inBufferSize,
        byte* outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped);
}
