using System.Globalization;
using System.Text.Json.Serialization;

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

    private OperationLock(FileStream handle, LockHolder holder)
    {
        _handle = handle;
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

        var holder = new LockHolder
        {
            Schema = LockHolder.CurrentSchema,
            ProcessId = Environment.ProcessId,
            FrontEnd = frontEnd,
            Command = command,
            StartedUtc = (time ?? TimeProvider.System).GetUtcNow(),
        };

        try
        {
            JsonFiles.Write(Path.Combine(local.Folder, HolderFileName), holder, LockJsonContext.Default.LockHolder, LockHolder.CurrentSchema);
        }
        catch
        {
            handle.Dispose();
            throw;
        }

        return new OperationLock(handle, holder);
    }

    /// <summary>
    /// Who took the lock last, or null when no one has or the file cannot be read. It is left in place
    /// after the lock is released, so it says who holds the lock only while the lock is held.
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
        catch (IOException)
        {
            return null;
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

    /// <summary>Releases the lock. The lock file and the holder file stay.</summary>
    public void Dispose() => _handle.Dispose();
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(LockHolder))]
internal sealed partial class LockJsonContext : JsonSerializerContext;
