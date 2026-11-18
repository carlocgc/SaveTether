using SaveTether.Core.Storage;
using SaveTether.Gui.Presentation;
using SaveTether.Tests.Cli;
using SaveTether.Tests.FileSystem;

namespace SaveTether.Tests.Gui;

/// <summary>First-run setup (desktop-app-spec.md, section 6), against fixture folders: the same refusals as init.</summary>
public class SetupFlowTests
{
    [Fact]
    public void Step_1_tells_a_missing_folder_an_empty_one_and_one_set_up_apart_and_writes_nothing()
    {
        using var sandbox = new Sandbox();
        var (flow, local) = NewFlow(sandbox, "pc");

        Assert.Equal(SyncFolderFinding.NotAPath, flow.CheckSyncFolder("relative/sync").Finding);
        Assert.Equal(SyncFolderFinding.NotAPath, flow.CheckSyncFolder("  ").Finding);

        var missing = flow.CheckSyncFolder(sandbox.At("unplugged"));
        Assert.Equal(SyncFolderFinding.Missing, missing.Finding);
        Assert.Contains("connect it", missing.Sentence, StringComparison.Ordinal);
        Assert.False(missing.CanContinue);

        var empty = sandbox.Dir("sync");
        var notSetUp = flow.CheckSyncFolder(empty);
        Assert.Equal(SyncFolderFinding.NotSetUp, notSetUp.Finding);
        Assert.True(notSetUp.OffersFirstDevice);
        Assert.Null(flow.SyncFolder);

        // Looking wrote nothing, here or in the local folder.
        Assert.Empty(Directory.GetFileSystemEntries(empty));
        Assert.Null(local.TryReadBootstrap());

        var other = new CliDevice(sandbox, "deck");
        other.Ok("init", "--sync-root", other.SyncRoot, "--device", "deck", "--create-root");
        var setUp = flow.CheckSyncFolder(other.SyncRoot);
        Assert.Equal(SyncFolderFinding.AlreadySetUp, setUp.Finding);
        Assert.Equal(["deck"], setUp.Devices);
        Assert.Contains("on: deck", setUp.Sentence, StringComparison.Ordinal);
        Assert.Same(setUp, flow.SyncFolder);
    }

    [Fact]
    public void The_first_device_creates_the_shared_folder_only_after_its_confirmation()
    {
        using var sandbox = new Sandbox();
        var (flow, local) = NewFlow(sandbox, "Gaming-PC");
        var sync = sandbox.Dir("sync");

        flow.CheckSyncFolder(sync);
        Assert.Equal("gaming-pc", flow.SuggestedName);
        Assert.Throws<InvalidOperationException>(() => flow.ChooseName("gaming-pc"));

        flow.ConfirmFirstDevice();
        Assert.NotNull(flow.SyncFolder);
        Assert.Empty(flow.ExistingDevices());
        flow.ChooseName("gaming-pc");
        Assert.Equal(Path.Combine(local.Folder, "backups"), flow.SuggestedBackupRoot);
        Assert.Empty(Directory.GetFileSystemEntries(sync));

        var outcome = flow.Finish(flow.SuggestedBackupRoot);
        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.True(outcome.Result!.CreatedRoot);
        Assert.True(outcome.Result.NewDevice);
        Assert.Equal("gaming-pc", local.TryReadBootstrap()!.DeviceName);
        Assert.True(Directory.Exists(Path.Combine(sync, "savetether", ".savetether")));
    }

    [Fact]
    public void A_name_already_set_up_is_taken_over_or_refused_never_both()
    {
        using var sandbox = new Sandbox();
        var deck = new CliDevice(sandbox, "deck");
        var backups = sandbox.Dir("deck-backups");
        deck.Ok("init", "--sync-root", deck.SyncRoot, "--device", "deck", "--create-root", "--backup-root", backups);

        var (flow, local) = NewFlow(sandbox, "steamdeck", "reinstall");
        flow.CheckSyncFolder(deck.SyncRoot);
        Assert.Equal([new ExistingDevice("deck", backups)], flow.ExistingDevices());

        Assert.Null(flow.NameProblem("steamdeck"));
        Assert.Contains("already set up", flow.NameProblem("deck"), StringComparison.Ordinal);
        Assert.Contains("cannot be used", flow.NameProblem("Deck!"), StringComparison.Ordinal);
        Assert.Contains("cannot be used", flow.NameProblem("_deck"), StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => flow.ChooseName("deck"));

        // Taking over: the device file's backup location is offered, and kept.
        flow.ChooseTakeOver("deck");
        Assert.Equal(backups, flow.SuggestedBackupRoot);
        var outcome = flow.Finish(flow.SuggestedBackupRoot);
        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.True(outcome.Result!.TookOver);
        Assert.Equal(backups, outcome.Result.BackupRoot);
        Assert.Equal("deck", local.TryReadBootstrap()!.DeviceName);
    }

    [Fact]
    public void A_taken_over_backup_location_that_is_not_there_is_noted_and_not_created()
    {
        using var sandbox = new Sandbox();
        var deck = new CliDevice(sandbox, "deck");
        var sdCard = sandbox.Dir("sd", "backups");
        deck.Ok("init", "--sync-root", deck.SyncRoot, "--device", "deck", "--create-root", "--backup-root", sdCard);
        // The SD card is taken out.
        Directory.Delete(sandbox.At("sd"), recursive: true);

        var (flow, _) = NewFlow(sandbox, "steamdeck", "reinstall");
        flow.CheckSyncFolder(deck.SyncRoot);
        flow.ChooseTakeOver("deck");
        Assert.Contains("may not be connected", flow.BackupRootNote(flow.SuggestedBackupRoot), StringComparison.Ordinal);

        var outcome = flow.Finish(flow.SuggestedBackupRoot);
        Assert.True(outcome.Succeeded, outcome.Error);
        Assert.Contains(outcome.Result!.Notes, n => n.Text.Contains("does not exist", StringComparison.Ordinal));
        Assert.False(Directory.Exists(sdCard));
    }

    [Fact]
    public void A_refusal_says_its_step_and_writes_nothing()
    {
        using var sandbox = new Sandbox();
        var (flow, local) = NewFlow(sandbox, "pc");
        var sync = sandbox.Dir("sync");
        flow.CheckSyncFolder(sync);
        flow.ConfirmFirstDevice();
        flow.ChooseName("pc");

        // The operation lock is held, as a CLI apply would hold it. A second open of the lock file conflicts
        // even within one process, so this stands in for another process.
        using (OperationLock.Acquire(local, "cli", "apply"))
        {
            var held = flow.Finish(flow.SuggestedBackupRoot);
            Assert.False(held.Succeeded);
            Assert.Equal(SetupStep.Backups, held.FailedAt);
            Assert.Contains("Another SaveTether operation is running", held.Error, StringComparison.Ordinal);
        }

        Assert.Null(local.TryReadBootstrap());
        Assert.Empty(Directory.GetFileSystemEntries(sync));

        // A backup location inside the sync folder is the core's refusal, at step 3.
        var inside = flow.Finish(Path.Combine(sync, "backups"));
        Assert.Equal(SetupStep.Backups, inside.FailedAt);
        Assert.NotNull(inside.Error);
        Assert.Null(local.TryReadBootstrap());

        Assert.Equal(SetupStep.Backups, flow.Finish("relative").FailedAt);
    }

    private static (SetupFlow Flow, LocalStore Local) NewFlow(Sandbox sandbox, string hostName, string folder = "local")
    {
        var local = new LocalStore(sandbox.At(folder));
        return (new SetupFlow(local, Path.Combine(local.Folder, "backups"), hostName), local);
    }
}
