using System.Collections.Concurrent;
using SaveTether.Core.Operations;

namespace SaveTether.Gui.Presentation;

/// <summary>
/// The Apps page's sizes: each row's folder and the sync folder, as "12 files, 3.4 MiB", the words Add app uses. Read off the UI
/// thread; the last size read is kept, so a check shows it at once while the folder is read again.
/// </summary>
public sealed class FolderSizes
{
    /// <summary>What a size says while it is read for the first time.</summary>
    public const string MeasuringText = "measuring…";

    public const string NotKnownText = "size not known";

    private readonly ConcurrentDictionary<string, string> _known = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<string>> _running = new(StringComparer.Ordinal);

    /// <summary>The size last read for the folder, or null when it was not read yet.</summary>
    public string? Known(string folder) => _known.TryGetValue(folder, out var line) ? line : null;

    /// <summary>Reads the folder's size on a worker thread. A folder already being read is not read twice: both wait for the one read.</summary>
    public Task<string> MeasureAsync(string folder)
    {
        lock (_running)
        {
            if (_running.TryGetValue(folder, out var running))
            {
                return running;
            }

            // The lock is held until the task is recorded, so its own removal below cannot come first.
            var task = Task.Run(() =>
            {
                try
                {
                    var (exists, _, bytes, files) = Workspace.Measure(folder);
                    var line = AddAppFlow.SizeOf(exists, bytes, files);
                    _known[folder] = line;
                    return line;
                }
                finally
                {
                    lock (_running)
                    {
                        _running.Remove(folder);
                    }
                }
            });
            _running[folder] = task;
            return task;
        }
    }

    /// <summary>
    /// Shows a folder's size as it is read, for the Apps page and Add app: the size last read at once, else "measuring…" for at
    /// least <see cref="Pacing.BusyMinimum"/>, so a quick read does not flash it for a frame (asked for by the owner). A read that
    /// fails keeps the last size, or says it is not known, and goes to <paramref name="failed"/>, since nobody awaits this.
    /// </summary>
    public async Task ShowAsync(string folder, Action<string> show, Action<Exception> failed)
    {
        ArgumentNullException.ThrowIfNull(show);
        ArgumentNullException.ThrowIfNull(failed);
        var known = Known(folder);
        show(known ?? MeasuringText);
        try
        {
            var measure = MeasureAsync(folder);
            await Task.WhenAll(measure, known is null ? Task.Delay(Pacing.BusyMinimum) : Task.CompletedTask);
            show(await measure);
        }
        catch (Exception e)
        {
            failed(e);
            show(known ?? NotKnownText);
        }
    }
}
