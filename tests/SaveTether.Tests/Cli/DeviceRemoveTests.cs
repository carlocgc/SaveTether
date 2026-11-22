using SaveTether.Cli;
using SaveTether.Core.Backup;
using SaveTether.Core.Model;
using SaveTether.Core.Operations;
using SaveTether.Core.Storage;
using SaveTether.Core.Execution;
using SaveTether.Core.Planning;
using SaveTether.Tests.Execution;
using SaveTether.Tests.FileSystem;

namespace SaveTether.Tests.Cli;

/// <summary>"device remove" (phase 3.6, T5): the dry run, the run, the orphan choices, the refusals and the backups.</summary>
public sealed class DeviceRemoveTests : IDisposable
{
    private const string Id = CliDevice.MappingId;
    private const string Solo = "solo.saves";

    private readonly Sandbox _sandbox = new();

    public void Dispose()
    {
        _sandbox.AssertCanaryIntact();
        _sandbox.Dispose();
    }

    private static string Meta(CliDevice d) => Path.Combine(d.SyncRoot, "savetether", ".savetether");

    private static string SoloSource(CliDevice d) => Path.Combine(d.AppFolder, "solo");

    private static string SoloShared(CliDevice d) => Path.Combine(d.SyncRoot, "savetether", "solo", "saves");

    /// <summary>The PC has two applied mappings: one it shares with the deck, and one only it uses.</summary>
    private (CliDevice Pc, CliDevice Deck) Devices()
    {
        var pc = new CliDevice(_sandbox, "pc");
        pc.SetUp(createRoot: true);
        pc.Save("GAME1/save.bin", "mine");
        pc.Ok("apply", Id, "--copy");
        pc.Ok("map", "add", Solo, "--target", "solo/saves", "--source", SoloSource(pc));
        CliDevice.Write(SoloSource(pc), "slot.sav", "solo");
        pc.Ok("apply", Solo, "--copy");

        var deck = new CliDevice(_sandbox, "deck");
        pc.DeliverTo(deck);
        deck.Ok("init", "--sync-root", deck.SyncRoot, "--device", "deck");
        deck.Ok("map", "add", Id, "--source", deck.Source);
        deck.DeliverTo(pc);
        return (pc, deck);
    }

    [Fact]
    public void A_dry_run_lists_the_plan_and_changes_nothing()
    {
        var (pc, _) = Devices();
        var shared = CliDevice.FilesIn(pc.SyncRoot);
        var local = CliDevice.FilesIn(pc.LocalFolder).Keys.Where(k => !k.StartsWith("backups", StringComparison.Ordinal)).Order().ToList();

        var o = pc.Ok("device", "remove", "--delete-orphan", Solo + ":saves", "--json").Json().GetProperty("removal");

        Assert.True(o.GetProperty("dry_run").GetBoolean());
        Assert.Equal(2, o.GetProperty("given_back").GetArrayLength());
        Assert.Equal(Solo, o.GetProperty("orphans")[0].GetProperty("mapping_id").GetString());
        Assert.Equal("delete-with-saves", o.GetProperty("orphans")[0].GetProperty("choice").GetString());
        Assert.Equal(3, o.GetProperty("shared_files").GetArrayLength()); // two seed markers and the device file
        Assert.Equal(shared, CliDevice.FilesIn(pc.SyncRoot));
        Assert.Equal(local, CliDevice.FilesIn(pc.LocalFolder).Keys.Where(k => !k.StartsWith("backups", StringComparison.Ordinal)).Order().ToList());
        Assert.True(pc.IsLinked);
    }

    [Fact]
    public void The_run_gives_the_folders_back_deletes_this_devices_files_and_leaves_the_rest()
    {
        var (pc, deck) = Devices();
        var theirs = CliDevice.FilesIn(pc.Target);
        var backups = Directory.GetDirectories(pc.BackupRoot).Length;
        Assert.True(backups > 0);

        var o = pc.Ok("device", "remove", "--delete-orphan", Solo + ":saves", "--confirm-remove", "--json").Json().GetProperty("removal");

        Assert.False(o.GetProperty("dry_run").GetBoolean());
        Assert.False(pc.IsLinked);
        Assert.Equal("mine", File.ReadAllText(Path.Combine(pc.Source, "GAME1", "save.bin")));
        Assert.Equal("solo", File.ReadAllText(Path.Combine(SoloSource(pc), "slot.sav")));
        Assert.True(Directory.Exists(SoloSource(pc)) && !pc.IsLinked);

        Assert.False(File.Exists(Path.Combine(Meta(pc), "devices", "pc.json")));
        Assert.False(File.Exists(Path.Combine(Meta(pc), "seed", Id, "pc.json")));
        Assert.True(File.Exists(Path.Combine(Meta(pc), "devices", "deck.json")));
        Assert.Contains("\"removed_by\": \"pc\"", File.ReadAllText(Path.Combine(Meta(pc), "removed", "pc.json")), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(Meta(pc), "mappings", Solo + ".json")));
        Assert.False(Directory.Exists(SoloShared(pc)));
        Assert.Equal(theirs, CliDevice.FilesIn(pc.Target)); // the shared mapping and its saves are untouched
        Assert.True(File.Exists(Path.Combine(Meta(pc), "mappings", Id + ".json")));

        Assert.False(File.Exists(Path.Combine(pc.LocalFolder, "bootstrap.json")));
        Assert.False(File.Exists(Path.Combine(pc.LocalFolder, "state.json")));
        Assert.False(File.Exists(Path.Combine(pc.LocalFolder, "journal.json")));
        Assert.True(Directory.GetDirectories(pc.BackupRoot).Length >= backups); // kept

        // Not set up any more, and set up again with a new name.
        Assert.Equal(CliApp.ExitRefused, pc.Run("status").Exit);
        pc.Ok("init", "--sync-root", pc.SyncRoot, "--device", "pc2");
        deck.Ok("status"); // the other device is unaffected
    }

    [Fact]
    public void Deleting_only_the_mapping_keeps_its_shared_saves()
    {
        var (pc, _) = Devices();
        pc.Ok("device", "remove", "--delete-orphan", Solo, "--confirm-remove");

        Assert.False(File.Exists(Path.Combine(Meta(pc), "mappings", Solo + ".json")));
        Assert.True(File.Exists(Path.Combine(SoloShared(pc), "slot.sav"))); // the shared saves stay

    }

    [Fact]
    public void An_orphan_with_no_choice_is_kept()
    {
        var (pc, _) = Devices();
        pc.Ok("device", "remove", "--confirm-remove");
        Assert.True(File.Exists(Path.Combine(Meta(pc), "mappings", Solo + ".json")));
        Assert.True(File.Exists(Path.Combine(SoloShared(pc), "slot.sav")));
    }

    [Fact]
    public void A_mapping_another_device_uses_is_never_deleted_and_a_bad_choice_changes_nothing()
    {
        var (pc, _) = Devices();
        var shared = CliDevice.FilesIn(pc.SyncRoot);

        Assert.Equal(CliApp.ExitRefused, pc.Run("device", "remove", "--delete-orphan", Id + ":saves", "--confirm-remove").Exit);
        Assert.Equal(CliApp.ExitUsage, pc.Run("device", "remove", "--delete-orphan", "Not An Id", "--confirm-remove").Exit);
        Assert.Equal(shared, CliDevice.FilesIn(pc.SyncRoot));
        Assert.True(pc.IsLinked);
    }

    [Fact]
    public void A_running_app_or_an_open_journal_refuses_and_nothing_changes()
    {
        var (pc, _) = Devices();
        var shared = CliDevice.FilesIn(pc.SyncRoot);

        pc.Running.Add("PPSSPPSDL");
        var run = pc.Run("device", "remove", "--confirm-remove");
        Assert.Equal(CliApp.ExitRefused, run.Exit);
        Assert.Contains("PPSSPPSDL", run.Err + run.Out, StringComparison.Ordinal);
        pc.Running.Clear();
        Assert.Equal(shared, CliDevice.FilesIn(pc.SyncRoot));
        Assert.True(pc.IsLinked);

        File.WriteAllText(Path.Combine(pc.LocalFolder, "journal.json"), "{}");
        Assert.Equal(CliApp.ExitRecoveryNeeded, pc.Run("device", "remove", "--confirm-remove").Exit);
        File.Delete(Path.Combine(pc.LocalFolder, "journal.json"));
        Assert.Equal(shared, CliDevice.FilesIn(pc.SyncRoot));
    }

    [Fact]
    public void Backups_are_deleted_only_when_chosen()
    {
        var (pc, _) = Devices();
        pc.Ok("device", "remove", "--delete-backups", "--confirm-remove");
        Assert.Empty(Directory.GetFiles(pc.BackupRoot, "manifest.json", SearchOption.AllDirectories));
    }

    [Fact]
    public void A_mapping_id_that_does_not_exist_is_refused_as_a_typo_unless_an_earlier_run_got_that_far()
    {
        var (pc, _) = Devices();
        var shared = CliDevice.FilesIn(pc.SyncRoot);
        Assert.Equal(CliApp.ExitRefused, pc.Run("device", "remove", "--delete-orphan", "solo.savse:saves", "--confirm-remove").Exit);
        Assert.Equal(shared, CliDevice.FilesIn(pc.SyncRoot));
        Assert.True(pc.IsLinked);

        // This install's tombstone is there: an earlier run deleted the mapping and stopped. The same choices go through.
        var workspace = Open(pc);
        workspace.Shared.WriteTombstone(new TombstoneFile
        {
            Schema = TombstoneFile.CurrentSchema, DeviceName = "pc", InstallId = workspace.Bootstrap.InstallId,
            RemovedUtc = DateTimeOffset.UtcNow, RemovedBy = "pc",
        });
        pc.Ok("device", "remove", "--delete-orphan", "gone.saves:saves", "--confirm-remove");
        Assert.False(File.Exists(Path.Combine(pc.LocalFolder, "bootstrap.json")));
    }

    [Fact]
    public void A_backup_step_that_fails_leaves_the_device_set_up_and_a_run_again_finishes()
    {
        var pc = new CliDevice(_sandbox, "pc");
        pc.SetUp(createRoot: true); // a mapping that is not applied: no revert, so no backup is needed first
        var workspace = Open(pc);
        var plan = workspace.PlanDeviceRemoval(new RemovalChoices()) with { DeleteBackups = true };
        Directory.Delete(pc.BackupRoot, recursive: true);

        Assert.Throws<BackupException>(() => workspace.RemoveDevice(plan));
        Assert.True(File.Exists(Path.Combine(pc.LocalFolder, "bootstrap.json")));
        Assert.True(File.Exists(Path.Combine(Meta(pc), "devices", "pc.json")));
        Assert.False(Directory.Exists(Path.Combine(Meta(pc), "removed")));

        Directory.CreateDirectory(pc.BackupRoot);
        Open(pc).RemoveDevice(plan);
        Assert.False(File.Exists(Path.Combine(pc.LocalFolder, "bootstrap.json")));
        Assert.False(File.Exists(Path.Combine(Meta(pc), "devices", "pc.json")));
    }

    [Fact]
    public void An_unreadable_tombstone_does_not_stop_device_list()
    {
        var (pc, _) = Devices();
        var removed = Path.Combine(Meta(pc), "removed");
        Directory.CreateDirectory(removed);
        File.WriteAllText(Path.Combine(removed, "junk.json"), "not json");
        pc.Ok("device", "list");
    }

    private sealed class NoProcesses : IProcessLister
    {
        public IReadOnlyList<string> RunningProcessNames() => [];
    }

    private static Workspace Open(CliDevice d) => Workspace.Open(new LocalStore(d.LocalFolder), new NoProcesses());

    [Fact]
    public void A_revert_that_fails_deletes_nothing_shared_and_a_run_again_finishes_the_job()
    {
        var (pc, _) = Devices();
        var shared = CliDevice.FilesIn(pc.SyncRoot);

        pc.Faults = new FailAt(StepKind.WriteState, FaultPoint.BeforeStep);
        var failed = pc.Run("device", "remove", "--delete-orphan", Solo + ":saves", "--confirm-remove");
        pc.Faults = null;

        Assert.NotEqual(CliApp.ExitSuccess, failed.Exit);
        Assert.Equal(shared, CliDevice.FilesIn(pc.SyncRoot));
        Assert.True(File.Exists(Path.Combine(pc.LocalFolder, "bootstrap.json")));
        if (failed.Exit == CliApp.ExitRecoveryNeeded)
        {
            pc.Ok("recover");
        }

        pc.Ok("device", "remove", "--delete-orphan", Solo + ":saves", "--confirm-remove");
        Assert.False(File.Exists(Path.Combine(pc.LocalFolder, "bootstrap.json")));
        Assert.False(File.Exists(Path.Combine(Meta(pc), "devices", "pc.json")));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(pc.Source, "GAME1", "save.bin")));
    }
}
