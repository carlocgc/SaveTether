using SaveTether.Core.Storage;
using SaveTether.Gui.Presentation;
using SaveTether.Tests.Cli;
using SaveTether.Tests.FileSystem;

namespace SaveTether.Tests.Gui;

/// <summary>Phase 3.6, T6: the window's state for a device that was removed from the sync folder.</summary>
public sealed class OverallRemovalTests : IDisposable
{
    private readonly Sandbox _sandbox = new();

    public void Dispose()
    {
        _sandbox.AssertCanaryIntact();
        _sandbox.Dispose();
    }

    [Fact]
    public void A_device_another_removed_and_one_whose_removal_stopped_are_told_apart()
    {
        var pc = new CliDevice(_sandbox, "pc");
        pc.SetUp(createRoot: true);
        var deck = new CliDevice(_sandbox, "deck");
        pc.DeliverTo(deck);
        deck.Ok("init", "--sync-root", deck.SyncRoot, "--device", "deck");
        deck.DeliverTo(pc);
        var local = new LocalStore(pc.LocalFolder);
        Assert.Equal(OverallCondition.AllLinked, StatusCheck.Run(local).Condition);

        deck.Ok("device", "remove", "--other", "pc", "--confirm-remove");
        deck.DeliverTo(pc, mirror: true);
        var removed = StatusCheck.Run(local);
        Assert.Equal(OverallCondition.RemovedByAnother, removed.Condition);
        Assert.Equal("Removed from the sync folder", removed.Headline);
        Assert.True(removed.IsAmber);

        // The same tombstone naming this device means its own removal stopped.
        var file = Path.Combine(pc.SyncRoot, "savetether", ".savetether", "removed", "pc.json");
        File.WriteAllText(file, File.ReadAllText(file).Replace("\"removed_by\": \"deck\"", "\"removed_by\": \"pc\"", StringComparison.Ordinal));
        var unfinished = StatusCheck.Run(local);
        Assert.Equal(OverallCondition.RemovalUnfinished, unfinished.Condition);
        Assert.Equal("Removal not finished", unfinished.Headline);
    }
}
