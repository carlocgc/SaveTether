using SaveTether.Cli;
using SaveTether.Core.Backup;
using SaveTether.Core.Execution;
using SaveTether.Core.Operations;
using SaveTether.Core.Planning;
using SaveTether.Core.Storage;
using SaveTether.Tests.Execution;
using SaveTether.Tests.FileSystem;

namespace SaveTether.Tests.Cli;

/// <summary>"backup delete": one backup removed, only once the owner has confirmed, and never mid-operation.</summary>
public class BackupDeleteTests
{
    private const string Id = CliDevice.MappingId;

    /// <summary>A device with two backups of its mapping: the first before linking, then one before reverting.</summary>
    private static (CliDevice Pc, string First, string Second) WithTwoBackups(Sandbox sandbox)
    {
        var pc = new CliDevice(sandbox, "pc");
        pc.SetUp(createRoot: true);
        pc.Save("GAME1/save.bin", "one");
        pc.Ok("apply", Id, "--copy");
        pc.Ok("revert", Id);
        var ids = Ids(pc);
        Assert.True(ids.Count >= 2, string.Join(", ", ids));
        return (pc, ids[0], ids[^1]);
    }

    private static List<string> Ids(CliDevice pc) =>
        [.. pc.Ok("backup", "list", "--json").Json().GetProperty("backups").EnumerateArray().Select(b => b.GetProperty("id").GetString()!)];

    [Fact]
    public void Without_a_person_or_the_flag_nothing_is_deleted()
    {
        using var sandbox = new Sandbox();
        var (pc, first, _) = WithTwoBackups(sandbox);

        var refused = pc.Expect(CliApp.ExitRefused, "backup", "delete", first);

        Assert.Contains("--confirm-delete", refused.Err, StringComparison.Ordinal);
        Assert.Contains(first, Ids(pc));
    }

    [Fact]
    public void At_a_terminal_it_asks_warns_about_the_first_backup_and_deletes_only_on_yes()
    {
        using var sandbox = new Sandbox();
        var (pc, first, second) = WithTwoBackups(sandbox);

        pc.Typing = new StringReader("no\n");
        var cancelled = pc.Expect(CliApp.ExitRefused, "backup", "delete", first);
        Assert.Contains("This is the first backup of", cancelled.Out, StringComparison.Ordinal);
        Assert.Contains("only copy", cancelled.Out, StringComparison.Ordinal);
        Assert.Contains("Cancelled. Nothing was deleted.", cancelled.Err, StringComparison.Ordinal);
        Assert.Contains(first, Ids(pc));

        // A later backup is asked about without the first-backup warning.
        pc.Typing = new StringReader("yes\n");
        var deleted = pc.Ok("backup", "delete", second);
        Assert.DoesNotContain("first backup", deleted.Out, StringComparison.Ordinal);
        Assert.Contains($"Deleted backup {second}.", deleted.Out, StringComparison.Ordinal);
        Assert.DoesNotContain(second, Ids(pc));
        Assert.Contains(first, Ids(pc));
        sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void With_the_flag_it_deletes_and_says_what_in_json()
    {
        using var sandbox = new Sandbox();
        var (pc, first, _) = WithTwoBackups(sandbox);
        var before = Ids(pc).Count;
        var location = pc.Ok("backup", "list", "--json").Json().GetProperty("backups")[0].GetProperty("location").GetString()!;

        var json = pc.Ok("backup", "delete", first, "--confirm-delete", "--json").Json().GetProperty("backup_deleted");

        Assert.Equal(first, json.GetProperty("backup").GetProperty("id").GetString());
        Assert.True(json.GetProperty("was_first").GetBoolean());
        Assert.Equal(before - 1, json.GetProperty("remaining").GetInt32());
        Assert.False(Directory.Exists(location));
        Assert.Equal(new Dictionary<string, string> { ["GAME1/save.bin"] = "one" }, pc.LocalFiles());
        sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void An_unknown_backup_is_refused()
    {
        using var sandbox = new Sandbox();
        var (pc, _, _) = WithTwoBackups(sandbox);

        var refused = pc.Expect(CliApp.ExitRefused, "backup", "delete", $"{Id}/20200101T000000Z", "--confirm-delete");

        Assert.Contains("There is no backup", refused.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void No_backup_is_deleted_while_an_operation_is_unfinished()
    {
        using var sandbox = new Sandbox();
        var (pc, first, _) = WithTwoBackups(sandbox);
        pc.Faults = new FailAt(StepKind.WriteState, FaultPoint.BeforeStep);
        pc.Expect(CliApp.ExitRecoveryNeeded, "apply", Id, "--no-copy");
        pc.Faults = null;

        // The CLI stops first, as for every change; the core refuses on its own too, for the app.
        pc.Expect(CliApp.ExitRecoveryNeeded, "backup", "delete", first, "--confirm-delete");
        var workspace = Workspace.Open(new LocalStore(pc.LocalFolder), new NoProcesses());
        var refused = Assert.Throws<BackupException>(() => workspace.DeleteBackup(first));
        Assert.Contains("Recover first", refused.Message, StringComparison.Ordinal);
        Assert.Contains(first, workspace.ListBackups().Select(b => b.Id));
    }

    private sealed class NoProcesses : IProcessLister
    {
        public IReadOnlyList<string> RunningProcessNames() => [];
    }
}
