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

        // Neither file is ever deleted: a lock file deleted while held would let a second process lock a new one.
        Assert.Equal(EntryKind.File, SafeFs.Probe(Path.Combine(local.Folder, OperationLock.FileName)));
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

        // A holder file left by a process that has since ended, as after any earlier operation.
        using (OperationLock.Acquire(local, "cli", "map add"))
        {
        }

        using (HoldInAnotherProcess(local.Folder))
        {
            var refused = Assert.Throws<StoreException>(() => OperationLock.Acquire(local, "cli", "apply"));
            Assert.Equal(StoreProblem.OperationRunning, refused.Problem);

            // This process is alive, so its old record would be believed: name a dead process in it instead.
            var stale = OperationLock.TryReadHolder(local)! with { ProcessId = DeadProcessId() };
            File.WriteAllText(Path.Combine(local.Folder, OperationLock.HolderFileName), System.Text.Json.JsonSerializer.Serialize(new
            {
                schema = stale.Schema, process_id = stale.ProcessId, front_end = stale.FrontEnd, command = stale.Command, started_utc = stale.StartedUtc,
            }));
            var unnamed = Assert.Throws<StoreException>(() => OperationLock.Acquire(local, "cli", "apply"));
            Assert.DoesNotContain("map add", unnamed.Message, StringComparison.Ordinal);
            Assert.Contains("Another SaveTether operation is running.", unnamed.Message, StringComparison.Ordinal);
        }

        using var taken = OperationLock.Acquire(local, "cli", "apply");
        Assert.Equal("apply", taken.Holder.Command);
    }

    /// <summary>
    /// Another process that takes the same operating-system lock on the lock file and waits, until
    /// disposed, which kills it. On Windows, PowerShell opens it with no sharing; on Linux, flock(1)
    /// takes the same exclusive flock(2) lock .NET takes.
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
            : new ProcessStartInfo("flock") { ArgumentList = { "-x", path, "-c", "echo ready; sleep 120" } };
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
        public void Dispose()
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            process.Dispose();
        }
    }
}
