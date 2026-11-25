using System.Globalization;
using System.Text.Json.Serialization;
using SaveTether.Core.FileSystem;

namespace SaveTether.Core.Storage;

/// <summary>Who holds the operation lock: written beside it, for the refusal another process gives.</summary>
public sealed record LockHolder
{
    public const int CurrentSchema = 1;

    public required int Schema { get; init; }

    public required int ProcessId { get; init; }

    /// <summary>Which front end took the lock: <c>cli</c> or <c>gui</c>.</summary>
    public required string FrontEnd { get; init; }

    /// <summary>The command or action it is running, such as <c>apply</c>.</summary>
    public required string Command { get; init; }

    public required DateTimeOffset StartedUtc { get; init; }
}

/// <summary>
/// One SaveTether operation at a time on this device, across processes: the CLI, the desktop app,
/// a script, the Decky plugin (desktop-app-spec.md, section 9.1). The journal records an unfinished
/// operation; this stops two from starting at once.
/// </summary>
/// <remarks>
/// <para>
/// The lock is <c>operation.lock</c> in the local folder, held open with no sharing for as long as
/// the operation runs. The operating system releases it when the process ends, however it ends, so
/// a killed process never leaves a lock that blocks. On Linux .NET takes an exclusive <c>flock</c> for
/// <see cref="FileShare.None"/>; any other share mode takes a shared one, which would let two
/// processes in, so the mode must stay <see cref="FileShare.None"/>. That lock is advisory: every
/// party that honours it is SaveTether.
/// </para>
/// <para>
/// The lock file is never deleted. Deleting it while it is held would let another process create a
/// new file and lock that, and two operations would run. Who holds the lock goes in a separate file,
/// <c>operation-holder.json</c>, because the lock file cannot be read while it is held.
/// </para>
/// </remarks>
public sealed class OperationLock : IDisposable
{
    public const string FileName = "operation.lock";
    public const string HolderFileName = "operation-holder.json";

    private readonly FileStream _handle;

    private readonly string _holderPath;

    private int _released;

    private OperationLock(FileStream handle, string holderPath, LockHolder holder)
    {
        _handle = handle;
        _holderPath = holderPath;
        Holder = holder;
    }

    /// <summary>Who this lock was taken for.</summary>
    public LockHolder Holder { get; }

    /// <summary>
    /// Takes the lock, or throws a <see cref="StoreException"/> with <see cref="StoreProblem.OperationRunning"/>
    /// when another process holds it. Nothing has been changed then.
    /// </summary>
    /// <param name="frontEnd"><c>cli</c> or <c>gui</c>.</param>
    /// <param name="command">What is about to run, for the other process to show.</param>
    public static OperationLock Acquire(LocalStore local, string frontEnd, string command, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentException.ThrowIfNullOrWhiteSpace(frontEnd);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        Directory.CreateDirectory(local.Folder);
        var path = Path.Combine(local.Folder, FileName);
        FileStream handle;
        try
        {
            handle = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException e) when (e is not (DirectoryNotFoundException or PathTooLongException))
        {
            // The holder file is from the last process to take the lock. It names who holds it now only if
            // that process is still running; otherwise the lock was taken a moment ago and not yet described.
            var current = TryReadHolder(local) is { } last && IsRunning(last.ProcessId) ? last : null;
            throw new StoreException(StoreProblem.OperationRunning, RunningMessage(current), e);
        }

        try
        {
            // For LastTaken: a time read without opening the file, which no one else may open while it is held.
            File.SetLastWriteTimeUtc(handle.SafeFileHandle, DateTime.UtcNow);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Only status's check for an operation that came and went while it read is lost.
        }

        var holder = new LockHolder
        {
            Schema = LockHolder.CurrentSchema,
            ProcessId = Environment.ProcessId,
            FrontEnd = frontEnd,
            Command = command,
            StartedUtc = (time ?? TimeProvider.System).GetUtcNow(),
        };

        var holderPath = Path.Combine(local.Folder, HolderFileName);
        try
        {
            JsonFiles.Write(holderPath, holder, LockJsonContext.Default.LockHolder, LockHolder.CurrentSchema);
        }
        catch
        {
            handle.Dispose();
            throw;
        }

        return new OperationLock(handle, holderPath, holder);
    }

    /// <summary>
    /// The holder file, or null when there is none or it cannot be read. A killed process leaves its file
    /// behind, so it says who holds the lock only while the lock is held.
    /// </summary>
    public static LockHolder? TryReadHolder(LocalStore local)
    {
        ArgumentNullException.ThrowIfNull(local);
        try
        {
            return JsonFiles.TryRead(Path.Combine(local.Folder, HolderFileName), LockJsonContext.Default.LockHolder, LockHolder.CurrentSchema);
        }
        catch (StoreException)
        {
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Windows denies access to a file while another process is deleting it.
            return null;
        }
    }

    /// <summary>
    /// When the lock was last taken, by any process: two reads that differ mean an operation started in between,
    /// even one that has ended since. Reads only, like <see cref="HeldByAnotherProcess"/>.
    /// </summary>
    public static DateTime LastTaken(LocalStore local)
    {
        ArgumentNullException.ThrowIfNull(local);
        try
        {
            return File.GetLastWriteTimeUtc(Path.Combine(local.Folder, FileName));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>
    /// Who holds the lock, when it is another process that is still running; otherwise null. Reads only:
    /// it does not try the lock, since even holding it for a moment could make another process's
    /// operation refuse. It goes by the holder file, so it is right while the holder runs. A process ID
    /// reused since then is not taken for the holder: that process started after the holder file was written.
    /// </summary>
    public static LockHolder? HeldByAnotherProcess(LocalStore local)
    {
        if (TryReadHolder(local) is not { } holder || holder.ProcessId == Environment.ProcessId)
        {
            return null;
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(holder.ProcessId);
            if (process.HasExited)
            {
                return null;
            }

            // A second's slack: the holder file is written just after its process starts at the earliest.
            return process.StartTime.ToUniversalTime() <= holder.StartedUtc.UtcDateTime.AddSeconds(1) ? holder : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Its start time cannot be read (an elevated or another user's process). A killed holder's ID may have
            // gone to any program since, so believe only a SaveTether one.
            return IsSaveTether(holder.ProcessId) ? holder : null;
        }
    }

    /// <summary>
    /// Whether the process is one of SaveTether's binaries, or true when its name cannot be read either, as before.
    /// shortcut: a build run through <c>dotnet</c> is not recognised; count the host too if that ever matters.
    /// </summary>
    private static bool IsSaveTether(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return process.ProcessName.StartsWith("savetether", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            // It has ended.
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return true;
        }
    }

    /// <summary>The refusal when another process holds the lock.</summary>
    public static string RunningMessage(LockHolder? holder) =>
        holder is null
            ? "Another SaveTether operation is running. Nothing was changed. Try again when it has finished."
            : $"Another SaveTether operation is running ({holder.FrontEnd} {holder.Command}, process {holder.ProcessId}, " +
              $"started {holder.StartedUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)}). " +
              "Nothing was changed. Try again when it has finished.";

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            // No process has that ID.
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Releases the lock, first deleting the holder file: left behind, it would name a process that runs on,
    /// such as the desktop app, as still holding the lock. The lock file stays. When a reader has the holder
    /// file open, which Windows refuses a delete for, the release finishes on a thread of its own within
    /// about half a second, so the caller (the app's UI thread among them) does not wait.
    /// </summary>
    public void Dispose()
    {
        // A second Dispose must not delete the next holder's file.
        if (Interlocked.Exchange(ref _released, 1) == 1)
        {
            return;
        }

        try
        {
            if (TryRemoveHolderFile())
            {
                _handle.Dispose();
                return;
            }
        }
        catch
        {
            _handle.Dispose();
            throw;
        }

        // Not a background thread: a process that is ending waits for it, as it waited for the release before.
        new Thread(() =>
        {
            try
            {
                for (var attempt = 1; attempt <= 20; attempt++)
                {
                    Thread.Sleep(25);
                    if (TryRemoveHolderFile())
                    {
                        return;
                    }
                }

                // shortcut: a file held longer stays until the next operation, and names this process until then.
            }
            finally
            {
                // Released only after the delete, which must never take the next holder's file.
                _handle.Dispose();
            }
        })
        {
            Name = "SaveTether lock release",
        }.Start();
    }

    /// <summary>
    /// False while a reader has the file open. Something other than a file at its path is left alone: it is
    /// not this lock's to delete, and it must not keep the lock held.
    /// </summary>
    private bool TryRemoveHolderFile()
    {
        try
        {
            new SafeFs().RemoveFile(_holderPath);
            return true;
        }
        catch (SafeFsException)
        {
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(LockHolder))]
internal sealed partial class LockJsonContext : JsonSerializerContext;
