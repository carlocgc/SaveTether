using System.Globalization;
using System.Text;

namespace SaveTether.Gui.Presentation;

/// <summary>
/// <c>gui.log</c> in the local folder (desktop-app-spec.md, section 2): one line per operation, refusal
/// and unexpected error, keeping about the last 1 MB. Writing it never fails the app: a line that cannot
/// be written is dropped.
/// </summary>
public sealed class GuiLog(string folder, TimeProvider? time = null)
{
    public const string FileName = "gui.log";
    public const long MaxBytes = 1024 * 1024;

    private static readonly UTF8Encoding NoByteOrderMark = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Lock _gate = new();

    public string Path => System.IO.Path.Combine(folder, FileName);

    public void Write(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var stamp = (time ?? TimeProvider.System).GetUtcNow().ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var line = $"{stamp} {message.ReplaceLineEndings(" ")}\n";
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(folder);
                // Encoding.UTF8 would start a new file with a byte-order mark: found on the Deck.
                File.AppendAllText(Path, line, NoByteOrderMark);
                if (new FileInfo(Path).Length > MaxBytes)
                {
                    Trim();
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Keeps the newest three quarters of the limit, from the start of a line.</summary>
    private void Trim()
    {
        var bytes = File.ReadAllBytes(Path);
        var start = bytes.Length - (int)(MaxBytes * 3 / 4);
        var newline = Array.IndexOf(bytes, (byte)'\n', start);
        var keep = newline < 0 ? [] : bytes[(newline + 1)..];
        File.WriteAllBytes(Path, keep);
    }
}
