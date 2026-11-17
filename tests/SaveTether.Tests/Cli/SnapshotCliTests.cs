using SaveTether.Cli;
using SaveTether.Core.Execution;
using SaveTether.Core.Planning;
using SaveTether.Tests.Execution;
using SaveTether.Tests.FileSystem;

namespace SaveTether.Tests.Cli;

/// <summary>"snapshot" (plan P2.7): copies of the shared folders in the backup store, with their own retention.</summary>
public class SnapshotCliTests
{
    private const string Id = CliDevice.MappingId;

    /// <summary>A device with the standard mapping applied and one save in the shared folder.</summary>
    private static CliDevice Applied(Sandbox sandbox)
    {
        var pc = new CliDevice(sandbox, "pc");
        pc.SetUp(createRoot: true);
        pc.Save("GAME1/save.bin", "one");
        pc.Ok("apply", Id, "--copy");
        return pc;
    }

    private static List<(string Id, string Reason)> Backups(CliDevice pc) =>
        pc.Ok("backup", "list", "--json").Json().GetProperty("backups").EnumerateArray()
            .Select(b => (b.GetProperty("id").GetString()!, b.GetProperty("reason").GetString()!))
            .ToList();

    [Fact]
    public void A_snapshot_copies_the_shared_folder_and_reports_its_space()
    {
        using var sandbox = new Sandbox();
        var pc = Applied(sandbox);

        var mapping = pc.Ok("snapshot", "--json").Json().GetProperty("snapshots");

        var outcome = mapping.GetProperty("mappings")[0];
        Assert.Equal("taken", outcome.GetProperty("status").GetString());
        Assert.Equal(1, outcome.GetProperty("files").GetInt32());
        Assert.Equal(3, outcome.GetProperty("bytes").GetInt64());
        Assert.Equal(3, outcome.GetProperty("kept_bytes").GetInt64());
        Assert.Equal(1, outcome.GetProperty("kept_count").GetInt32());
        Assert.True(mapping.GetProperty("free_bytes").GetInt64() > 0);
        Assert.Equal(5, mapping.GetProperty("keep").GetInt32());

        Assert.Contains(Backups(pc), b => b.Reason == "snapshot");
        sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void An_unchanged_shared_folder_is_not_snapshotted_twice()
    {
        using var sandbox = new Sandbox();
        var pc = Applied(sandbox);
        var first = pc.Ok("snapshot", "--json").Json().GetProperty("snapshots").GetProperty("mappings")[0].GetProperty("backup_id").GetString();

        var again = pc.Ok("snapshot", "--json").Json().GetProperty("snapshots").GetProperty("mappings")[0];

        Assert.Equal("unchanged", again.GetProperty("status").GetString());
        Assert.Equal(first, again.GetProperty("backup_id").GetString());
        Assert.Single(Backups(pc), b => b.Reason == "snapshot");
    }

    [Fact]
    public void Retention_removes_only_older_snapshots_never_other_backups()
    {
        using var sandbox = new Sandbox();
        var pc = Applied(sandbox);
        var before = Backups(pc).Where(b => b.Reason != "snapshot").ToList();
        Assert.NotEmpty(before);

        var taken = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            pc.Save("GAME1/save.bin", $"version {i}");
            taken.Add(pc.Ok("snapshot", "--keep", "2", "--json").Json().GetProperty("snapshots").GetProperty("mappings")[0].GetProperty("backup_id").GetString()!);
        }

        var after = Backups(pc);
        Assert.Equal(taken[^2..], after.Where(b => b.Reason == "snapshot").Select(b => b.Id));
        Assert.Equal(before, after.Where(b => b.Reason != "snapshot"));
    }

    [Fact]
    public void A_mapping_not_applied_here_is_not_snapshotted_and_that_is_not_a_problem()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");
        pc.SetUp(createRoot: true);

        var outcome = pc.Ok("snapshot", "--json", "--notify").Json().GetProperty("snapshots").GetProperty("mappings")[0];

        Assert.Equal("not-applied", outcome.GetProperty("status").GetString());
        Assert.Empty(pc.Notifications);
        Assert.DoesNotContain(Backups(pc), b => b.Reason == "snapshot");
    }

    [Fact]
    public void A_dry_run_writes_nothing()
    {
        using var sandbox = new Sandbox();
        var pc = Applied(sandbox);

        var outcome = pc.Ok("snapshot", "--dry-run", "--json").Json().GetProperty("snapshots").GetProperty("mappings")[0];

        Assert.Equal("planned", outcome.GetProperty("status").GetString());
        Assert.DoesNotContain(Backups(pc), b => b.Reason == "snapshot");
    }

    [Fact]
    public void A_snapshot_never_runs_while_an_operation_is_unfinished()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");
        pc.SetUp(createRoot: true);
        pc.Save("GAME1/save.bin", "one");
        pc.Faults = new FailAt(StepKind.WriteState, FaultPoint.BeforeStep);
        pc.Expect(CliApp.ExitRecoveryNeeded, "apply", Id, "--copy");
        pc.Faults = null;

        var run = pc.Expect(CliApp.ExitRecoveryNeeded, "snapshot", "--notify", "--log");

        Assert.Contains("savetether recover", run.Err, StringComparison.Ordinal);
        Assert.DoesNotContain(Backups(pc), b => b.Reason == "snapshot");
        Assert.Single(pc.Notifications);
        Assert.Contains("did not finish", File.ReadAllText(Path.Combine(pc.LocalFolder, RunLog.FileName)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("101")]
    [InlineData("two")]
    public void Keep_must_be_a_sensible_number(string keep)
    {
        using var sandbox = new Sandbox();
        var pc = Applied(sandbox);

        pc.Expect(CliApp.ExitUsage, "snapshot", "--keep", keep);
    }

    // ---- The scheduled status check ----

    [Fact]
    public void Status_notifies_only_when_something_is_wrong_and_logs_every_run()
    {
        using var sandbox = new Sandbox();
        var pc = Applied(sandbox);

        pc.Ok("status", "--notify", "--log");
        Assert.Empty(pc.Notifications);

        // The shared folder vanishes, as when the sync root is not mounted: the link is broken.
        Directory.Move(pc.Target, pc.Target + "-gone");
        pc.Ok("status", "--notify", "--log");

        var (title, body) = Assert.Single(pc.Notifications);
        Assert.Equal("SaveTether: 1 problem", title);
        Assert.Contains($"{Id}: Broken", body, StringComparison.Ordinal);

        var log = File.ReadAllLines(Path.Combine(pc.LocalFolder, RunLog.FileName));
        Assert.Contains(log, line => line.EndsWith("status: all well", StringComparison.Ordinal));
        Assert.Contains(log, line => line.EndsWith("status: 1 problem(s)", StringComparison.Ordinal));
        Assert.Contains(log, line => line.Contains($"{Id}: Broken", StringComparison.Ordinal));
    }

    [Fact]
    public void Status_on_a_device_that_is_not_set_up_notifies_too()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");

        pc.Expect(CliApp.ExitRefused, "status", "--notify");

        Assert.Contains("not set up", Assert.Single(pc.Notifications).Body, StringComparison.Ordinal);
    }
}
