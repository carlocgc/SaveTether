using SaveTether.Core.Backup;
using SaveTether.Tests.FileSystem;

namespace SaveTether.Tests.Cli;

/// <summary>
/// Retention of the backups taken before apply, revert and restore (design 5.12, "Backup store"):
/// after each completed operation, the first backup of the mapping and the newest five are kept.
/// </summary>
public class BackupRetentionTests
{
    private const string Id = CliDevice.MappingId;

    private static List<string> BackupIds(CliDevice pc) =>
        [.. pc.Ok("backup", "list", "--json").Json().GetProperty("backups").EnumerateArray().Select(b => b.GetProperty("id").GetString()!)];

    [Fact]
    public void Repeated_operations_keep_the_first_backup_and_the_newest_five()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");
        pc.SetUp(createRoot: true);
        pc.Save("GAME1/save.bin", "original");
        pc.Ok("apply", Id, "--copy");
        var first = BackupIds(pc)[0];

        var seen = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            pc.Ok("revert", Id);
            pc.Save("GAME1/save.bin", $"local {i}");
            pc.Ok("apply", Id, "--no-copy");
            seen.AddRange(BackupIds(pc).Where(id => !seen.Contains(id)));
        }

        var kept = BackupIds(pc);
        Assert.Equal(1 + BackupStore.DefaultKeep, kept.Count);
        Assert.Equal(first, kept[0]);
        Assert.Equal(seen.Where(id => id != first).TakeLast(BackupStore.DefaultKeep), kept.Skip(1));

        // The first backup is still the device's saves from before SaveTether touched them.
        var data = Path.Combine(pc.BackupRoot, first.Replace('/', Path.DirectorySeparatorChar), "data", "GAME1", "save.bin");
        Assert.Equal("original", File.ReadAllText(data));
        sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void The_operation_says_which_older_backups_it_removed()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");
        pc.SetUp(createRoot: true);
        pc.Save("GAME1/save.bin", "original");
        pc.Ok("apply", Id, "--copy");

        string? removedMessage = null;
        for (var i = 0; i < 6 && removedMessage is null; i++)
        {
            var revert = pc.Ok("revert", Id).Out;
            pc.Save("GAME1/save.bin", $"local {i}");
            var apply = pc.Ok("apply", Id, "--no-copy").Out;
            removedMessage = new[] { revert, apply }.SelectMany(o => o.Split('\n')).FirstOrDefault(l => l.StartsWith("Removed ", StringComparison.Ordinal));
        }

        Assert.NotNull(removedMessage);
        Assert.Contains($"older backup(s) of {Id}, keeping its first backup and the newest {BackupStore.DefaultKeep}", removedMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Backups_of_other_mappings_are_left_alone()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");
        pc.SetUp(createRoot: true);
        var other = Path.Combine(pc.AppFolder, "STATES");
        CliDevice.Write(other, "s.ppst", "state");
        pc.Ok("map", "add", "ppsspp.states", "--target", "ppsspp/STATES", "--source", other);
        pc.Ok("apply", "ppsspp.states", "--copy");
        var otherBackups = BackupIds(pc);

        pc.Save("GAME1/save.bin", "original");
        pc.Ok("apply", Id, "--copy");
        for (var i = 0; i < 6; i++)
        {
            pc.Ok("revert", Id);
            pc.Save("GAME1/save.bin", $"local {i}");
            pc.Ok("apply", Id, "--no-copy");
        }

        Assert.All(otherBackups, id => Assert.Contains(id, BackupIds(pc)));
    }
}
