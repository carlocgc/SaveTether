using System.Diagnostics;
using SaveTether.Cli;
using SaveTether.Core.FileSystem;
using SaveTether.Core.Storage;
using SaveTether.Tests.Cli;
using SaveTether.Tests.FileSystem;

namespace SaveTether.Tests.Storage;

/// <summary>
/// One operation at a time on this device, across processes (desktop-app-spec.md, section 9.1):
/// a second is refused and changes nothing, and a lock left by a killed process does not block.
/// </summary>
public class OperationLockTests
{
    [Fact]
    public void A_second_taker_is_refused_with_who_holds_it_until_the_first_lets_go()
    {
        using var sandbox = new Sandbox();
        var local = new LocalStore(sandbox.Dir("local"));

        using (var first = OperationLock.Acquire(local, "gui", "apply"))
        {
            Assert.Equal(Environment.ProcessId, first.Holder.ProcessId);

            var refused = Assert.Throws<StoreException>(() => OperationLock.Acquire(local, "cli", "revert"));
            Assert.Equal(StoreProblem.OperationRunning, refused.Problem);
            Assert.Contains($"gui apply, process {Environment.ProcessId}", refused.Message, StringComparison.Ordinal);
            Assert.Contains("Nothing was changed", refused.Message, StringComparison.Ordinal);
        }

        using var again = OperationLock.Acquire(local, "cli", "revert");
        Assert.Equal("revert", OperationLock.TryReadHolder(local)!.Command);

        // The lock file is never deleted: one deleted while held would let a second process lock a new one.
        Assert.Equal(EntryKind.File, SafeFs.Probe(Path.Combine(local.Folder, OperationLock.FileName)));
    }

    [Fact]
    public void Releasing_the_lock_removes_the_holder_file_and_a_second_dispose_leaves_the_next_one()
    {
        using var sandbox = new Sandbox();
        var local = new LocalStore(sandbox.Dir("local"));

        var first = OperationLock.Acquire(local, "gui", "apply");
        first.Dispose();
        Assert.Null(OperationLock.TryReadHolder(local));

        using var next = OperationLock.Acquire(local, "cli", "revert");
        first.Dispose();
        Assert.Equal("revert", OperationLock.TryReadHolder(local)!.Command);
    }

    [Fact]
    public void Status_says_when_another_process_is_running_an_operation()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");
        pc.SetUp(createRoot: true);
        bool Running() => pc.Ok("status", "--json").Json().GetProperty("status").GetProperty("operation_running").GetBoolean();
        const string Line = "Another SaveTether process is running an operation.";

        Assert.False(Running());
        Assert.DoesNotContain(Line, pc.Ok("status").Out, StringComparison.Ordinal);

        using (var other = HoldInAnotherProcess(pc.LocalFolder))
        {
            // The stand-in takes the lock only; a SaveTether process also names itself.
            File.WriteAllText(
                Path.Combine(pc.LocalFolder, OperationLock.HolderFileName),
                $$"""{"schema": 1, "process_id": {{other.ProcessId}}, "front_end": "gui", "command": "apply", "started_utc": "{{DateTimeOffset.UtcNow:O}}"}""");
            Assert.True(Running());
            Assert.Contains(Line, pc.Ok("status").Out, StringComparison.Ordinal);
        }

        // The file a killed process left behind.
        Assert.False(Running());
    }

    [Fact]
    public void Status_counts_an_operation_that_ends_while_it_is_reading()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");
        pc.SetUp(createRoot: true);
        using var other = HoldInAnotherProcess(pc.LocalFolder);
        var holder = Path.Combine(pc.LocalFolder, OperationLock.HolderFileName);
        File.WriteAllText(
            holder,
            $$"""{"schema": 1, "process_id": {{other.ProcessId}}, "front_end": "gui", "command": "apply", "started_utc": "{{DateTimeOffset.UtcNow:O}}"}""");

        // Status reads the autostart entry after the links: the operation lets go there.
        pc.Autostart = new EndsTheOperation(holder);

        Assert.True(pc.Ok("status", "--json").Json().GetProperty("status").GetProperty("operation_running").GetBoolean());
        Assert.False(File.Exists(holder));
    }

    [Fact]
    public void Releasing_waits_for_a_reader_to_close_the_holder_file()
    {
        using var sandbox = new Sandbox();
        var local = new LocalStore(sandbox.Dir("local"));
        var held = OperationLock.Acquire(local, "gui", "apply");

        // Open as File.ReadAllText opens it. Windows refuses the delete meanwhile; Linux does not.
        var reader = File.OpenRead(Path.Combine(local.Folder, OperationLock.HolderFileName));
        // A thread of its own: a busy thread pool could close the file after the retries have given up.
        var closing = new Thread(() =>
        {
            Thread.Sleep(100);
            reader.Dispose();
        });
        closing.Start();
        held.Dispose();
        closing.Join();

        // On Windows the release finishes on a thread of its own, the caller not waiting for the reader.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (OperationLock.TryReadHolder(local) is not null && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(10);
        }

        Assert.Null(OperationLock.TryReadHolder(local));
        using var next = OperationLock.Acquire(local, "cli", "revert");
    }

    [Fact]
    public void Something_other_than_a_file_at_the_holder_path_does_not_keep_the_lock()
    {
        using var sandbox = new Sandbox();
        var local = new LocalStore(sandbox.Dir("local"));
        var held = OperationLock.Acquire(local, "gui", "apply");
        var holder = Path.Combine(local.Folder, OperationLock.HolderFileName);
        File.Delete(holder);
        Directory.CreateDirectory(holder);

        held.Dispose();

        Assert.True(Directory.Exists(holder));
        using var free = new FileStream(Path.Combine(local.Folder, OperationLock.FileName), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void Status_counts_an_operation_that_starts_and_ends_while_it_is_reading()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");
        pc.SetUp(createRoot: true);
        pc.Autostart = new RunsAnOperation(new LocalStore(pc.LocalFolder));

        Assert.True(pc.Ok("status", "--json").Json().GetProperty("status").GetProperty("operation_running").GetBoolean());
    }

    private sealed class RunsAnOperation(LocalStore local) : SaveTether.Core.Startup.IAutostartStore
    {
        public string Location => "test entry";

        public string? ReadProgram()
        {
            using (OperationLock.Acquire(local, "gui", "apply"))
            {
            }

            return null;
        }

        public void Write(string program) => throw new InvalidOperationException("Status must not write the entry.");

        public void Remove() => throw new InvalidOperationException("Status must not remove the entry.");
    }

    [Fact]
    public void A_holder_file_that_may_not_be_read_names_no_holder()
    {
        // Windows gives the same refusal for a file being deleted, which cannot be staged.
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess)
        {
            return;
        }

        using var sandbox = new Sandbox();
        var local = new LocalStore(sandbox.Dir("local"));
        var path = Path.Combine(local.Folder, OperationLock.HolderFileName);
        File.WriteAllText(path, "{}");
        File.SetUnixFileMode(path, UnixFileMode.None);

        Assert.Null(OperationLock.TryReadHolder(local));
    }

    private sealed class EndsTheOperation(string holderFile) : SaveTether.Core.Startup.IAutostartStore
    {
        public string Location => "test entry";

        public string? ReadProgram()
        {
            File.Delete(holderFile);
            return null;
        }

        public void Write(string program) => throw new InvalidOperationException("Status must not write the entry.");

        public void Remove() => throw new InvalidOperationException("Status must not remove the entry.");
    }

    [Fact]
    public void Another_process_holding_the_lock_refuses_the_cli_which_changes_nothing()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");
        pc.SetUp(createRoot: true);
        pc.Save("GAME1/save.bin", "one");

        using (HoldInAnotherProcess(pc.LocalFolder))
        {
            var refused = pc.Expect(CliApp.ExitRefused, "apply", CliDevice.MappingId, "--copy", "--json").Json();
            Assert.Equal("operation-running", refused.GetProperty("problem").GetString());
            Assert.Contains("Another SaveTether operation is running", refused.GetProperty("error").GetString(), StringComparison.Ordinal);

            // Looking still works while another process acts.
            pc.Ok("status");
            pc.Ok("apply", CliDevice.MappingId, "--copy", "--dry-run");
        }

        Assert.False(pc.IsLinked);
        Assert.Equal("one", pc.LocalFiles()["GAME1/save.bin"]);
        Assert.Empty(pc.SharedFiles());

        // The holder was killed, not closed cleanly: its lock went with it.
        pc.Ok("apply", CliDevice.MappingId, "--copy");
        Assert.True(pc.IsLinked);
        sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void A_lock_held_by_a_process_that_was_killed_does_not_block()
    {
        using var sandbox = new Sandbox();
        var local = new LocalStore(sandbox.Dir("local"));

        using (HoldInAnotherProcess(local.Folder))
        {
            var refused = Assert.Throws<StoreException>(() => OperationLock.Acquire(local, "cli", "apply"));
            Assert.Equal(StoreProblem.OperationRunning, refused.Problem);

            // A holder file left by a process that was killed.
            File.WriteAllText(
                Path.Combine(local.Folder, OperationLock.HolderFileName),
                $$"""{"schema": 1, "process_id": {{DeadProcessId()}}, "front_end": "cli", "command": "map add", "started_utc": "{{DateTimeOffset.UtcNow:O}}"}""");
            var unnamed = Assert.Throws<StoreException>(() => OperationLock.Acquire(local, "cli", "apply"));
            Assert.DoesNotContain("map add", unnamed.Message, StringComparison.Ordinal);
            Assert.Contains("Another SaveTether operation is running.", unnamed.Message, StringComparison.Ordinal);
        }

        using var taken = OperationLock.Acquire(local, "cli", "apply");
        Assert.Equal("apply", taken.Holder.Command);
    }

    [Fact]
    public void A_process_started_while_the_lock_is_held_does_not_keep_it()
    {
        // SaveTether starts other programs while holding the lock, such as flatpak during apply. Had they
        // inherited the locked file, the lock would outlive the operation for as long as they ran.
        using var sandbox = new Sandbox();
        var local = new LocalStore(sandbox.Dir("local"));

        Process child;
        using (OperationLock.Acquire(local, "cli", "apply"))
        {
            var start = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("powershell.exe") { ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep 120" } }
                : new ProcessStartInfo("sleep") { ArgumentList = { "120" } };
            start.UseShellExecute = false;
            child = Process.Start(start) ?? throw new InvalidOperationException("The child did not start.");
        }

        using (new Holder(child))
        {
            Assert.False(child.HasExited);
            using var again = OperationLock.Acquire(local, "cli", "revert");
            Assert.Equal("revert", again.Holder.Command);
        }
    }

    /// <summary>
    /// Another process that takes the same operating-system lock on the lock file and waits, until
    /// disposed, which kills it. On Windows, PowerShell opens it with no sharing; on Linux, flock(1)
    /// takes the same exclusive flock(2) lock .NET takes. Its -o closes the locked file before running
    /// the command, so only flock itself holds the lock: without it the shell and its sleep inherit
    /// the file and keep the lock after flock is killed (found on CI and on the Mint box).
    /// </summary>
    private static Holder HoldInAnotherProcess(string localFolder)
    {
        Directory.CreateDirectory(localFolder);
        var path = Path.Combine(localFolder, OperationLock.FileName);
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("powershell.exe")
            {
                ArgumentList =
                {
                    "-NoProfile", "-NonInteractive", "-Command",
                    $"$f = [IO.File]::Open('{path.Replace("'", "''", StringComparison.Ordinal)}', 'OpenOrCreate', 'ReadWrite', 'None'); 'ready'; Start-Sleep 120",
                },
            }
            : new ProcessStartInfo("flock") { ArgumentList = { "-o", "-x", path, "-c", "echo ready; sleep 120" } };
        start.RedirectStandardOutput = true;
        start.UseShellExecute = false;

        var process = Process.Start(start) ?? throw new InvalidOperationException("The lock holder did not start.");
        var line = process.StandardOutput.ReadLine();
        Assert.Equal("ready", line?.Trim());
        return new Holder(process);
    }

    /// <summary>The ID of a process that has ended.</summary>
    private static int DeadProcessId()
    {
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", "exit" } }
            : new ProcessStartInfo("true");
        start.UseShellExecute = false;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The short process did not start.");
        process.WaitForExit();
        return process.Id;
    }

    private sealed class Holder(Process process) : IDisposable
    {
        public int ProcessId => process.Id;

        public void Dispose()
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            process.Dispose();
        }
    }
}
