using System.Net.Sockets;
using SaveTether.Core.FileSystem;
using SaveTether.Core.Startup;

namespace SaveTether.Gui.Presentation;

/// <summary>
/// One copy of the app per user and local folder (desktop-app-spec.md, section 2). The first copy holds
/// <c>gui.lock</c> with no sharing, as the operation lock does, so the operating system frees it however
/// the process ends. It listens on a local socket; a second copy finds the lock taken, asks the first to
/// show its window, and exits. A newer copy that is to update the installed app asks the first to quit instead.
/// </summary>
/// <remarks>
/// One mechanism on both systems, where the spec named a mutex and a named pipe for Windows: .NET's
/// Unix domain sockets work on Windows 10 and later, so the code and its tests are the same everywhere.
/// The socket is in the local folder, or, when that path is too long for a socket, in the user's runtime
/// folder (Linux) or temporary folder under a name made from the local folder's path.
/// </remarks>
public sealed class SingleInstance : IDisposable
{
    public const string LockName = "gui.lock";
    private const string ShowMessage = "show\n";
    private const string QuitMessage = "quit\n";
    private const int MaxSocketPath = 100;

    private readonly FileStream _lock;
    private readonly Socket _listener;
    private readonly string _socketPath;
    private readonly CancellationTokenSource _stop = new();

    private SingleInstance(FileStream lockHandle, Socket listener, string socketPath, Action onShow, Action? onQuit)
    {
        _lock = lockHandle;
        _listener = listener;
        _socketPath = socketPath;
        _ = Task.Run(() => ListenAsync(onShow, onQuit, _stop.Token));
    }

    /// <summary>
    /// Becomes the one copy, or returns null when another copy already is. <paramref name="onShow"/> runs on
    /// a background thread each time another copy asks for the window; <paramref name="onQuit"/> when one asks this
    /// copy to quit, to update it.
    /// </summary>
    public static SingleInstance? TryClaim(string localFolder, Action onShow, Action? onQuit = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localFolder);
        ArgumentNullException.ThrowIfNull(onShow);
        Directory.CreateDirectory(localFolder);

        FileStream handle;
        try
        {
            handle = new FileStream(Path.Combine(localFolder, LockName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException e) when (e is not (DirectoryNotFoundException or PathTooLongException))
        {
            return null;
        }

        var socketPath = SocketPath(localFolder);
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            // Left by a copy that was killed. Only the lock holder gets here, so no running copy owns it.
            new SafeFs().DeleteSocket(socketPath);
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            listener.Listen(4);
        }
        catch
        {
            listener.Dispose();
            handle.Dispose();
            throw;
        }

        return new SingleInstance(handle, listener, socketPath, onShow, onQuit);
    }

    /// <summary>Asks the copy that holds the lock to show its window. False when none answered.</summary>
    public static bool SignalExisting(string localFolder) => AppChannel.RequestShow(localFolder);

    /// <summary>Asks the copy that holds the lock to quit, as it would from its tray menu. False when none answered.</summary>
    public static bool RequestQuit(string localFolder) => AppChannel.RequestQuit(localFolder);

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Dispose();
        try
        {
            new SafeFs().DeleteSocket(_socketPath);
        }
        catch (IOException)
        {
            // Left for the next copy, which removes it before listening.
        }

        _lock.Dispose();
        _stop.Dispose();
    }

    internal static string SocketPath(string localFolder) => AppChannel.SocketPath(localFolder);

    private async Task ListenAsync(Action onShow, Action? onQuit, CancellationToken stop)
    {
        var buffer = new byte[16];
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var client = await _listener.AcceptAsync(stop).ConfigureAwait(false);
                var read = await client.ReceiveAsync(buffer, SocketFlags.None, stop).ConfigureAwait(false);
                var message = System.Text.Encoding.ASCII.GetString(buffer, 0, read);
                if (message == AppChannel.ShowMessage)
                {
                    onShow();
                }
                else if (message == AppChannel.QuitMessage)
                {
                    onQuit?.Invoke();
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                // One bad connection does not stop the listener.
            }
        }
    }
}
