using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace SaveTether.Core.Startup;

/// <summary>
/// The local socket a running desktop app listens on, one per local folder (desktop-app-spec.md, section 2), and
/// the messages it answers. The app listens (SingleInstance, in the app); a second copy and <c>savetether
/// uninstall</c> send.
/// </summary>
public static class AppChannel
{
    public const string ShowMessage = "show\n";
    public const string QuitMessage = "quit\n";
    private const int MaxSocketPath = 100;

    /// <summary>Asks the copy running for this local folder to show its window. False when none answered.</summary>
    public static bool RequestShow(string localFolder) => Send(localFolder, ShowMessage);

    /// <summary>Asks the copy running for this local folder to quit, as it would from its tray menu. False when none answered.</summary>
    public static bool RequestQuit(string localFolder) => Send(localFolder, QuitMessage);

    /// <summary>
    /// The socket's path: in the local folder, or, when that path is too long for a socket, in the user's runtime
    /// folder (Linux) or temporary folder under a name made from the local folder's path.
    /// </summary>
    public static string SocketPath(string localFolder)
    {
        var inFolder = Path.Combine(Path.GetFullPath(localFolder), "gui.sock");
        if (inFolder.Length <= MaxSocketPath)
        {
            return inFolder;
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(localFolder))))[..16];
        var runtime = OperatingSystem.IsWindows() ? null : Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        var folder = runtime is { Length: > 0 } && Path.IsPathFullyQualified(runtime) ? runtime : Path.GetTempPath();
        return Path.Combine(folder, $"savetether-{hash}.sock");
    }

    private static bool Send(string localFolder, string message)
    {
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(SocketPath(localFolder)));
            socket.Send(Encoding.ASCII.GetBytes(message));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}

/// <summary>Which processes run a given program. Tests supply their own.</summary>
public interface IRunningPrograms
{
    /// <summary>The process ids running exactly this program file.</summary>
    IReadOnlyList<int> Running(string program);
}

/// <summary>This machine's processes, matched by the file each one runs.</summary>
public sealed class SystemRunningPrograms : IRunningPrograms
{
    public IReadOnlyList<int> Running(string program)
    {
        var found = new List<int>();
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(program)))
        {
            using (process)
            {
                try
                {
                    if (string.Equals(process.MainModule?.FileName, program, StringComparison.Ordinal))
                    {
                        found.Add(process.Id);
                    }
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // Ended meanwhile, or another user's: not this user's installed copy.
                }
            }
        }

        return found;
    }
}
