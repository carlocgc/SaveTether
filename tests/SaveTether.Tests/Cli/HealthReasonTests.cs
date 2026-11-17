using System.Text.Json.Nodes;
using SaveTether.Core.FileSystem;
using SaveTether.Tests.FileSystem;

namespace SaveTether.Tests.Cli;

/// <summary>
/// The reason <c>status</c> gives where one state covers situations that need different words or
/// a different fix (desktop-app-spec.md, sections 5.1 and 13).
/// </summary>
public class HealthReasonTests
{
    private const string Id = CliDevice.MappingId;

    [Fact]
    public void Each_situation_behind_not_applied_replaced_and_broken_has_its_own_reason()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");
        pc.SetUp(createRoot: true);
        var fs = new SafeFs();

        Assert.Equal(("not-applied", "not-linked-yet"), Health(pc));

        pc.Save("GAME1/save.bin", "one");
        pc.Ok("apply", Id, "--copy");
        Assert.Equal(("ok", null), Health(pc));

        // The link is replaced by a folder holding files, by an empty folder, by nothing, and by a file.
        fs.RemoveLink(pc.Source);
        pc.Save("GAME2/save.bin", "stranded");
        Assert.Equal(("replaced", "folder-holds-files"), Health(pc));

        fs.DeleteTree(pc.Source);
        Directory.CreateDirectory(Path.Combine(pc.Source, "empty-subfolder"));
        Assert.Equal(("replaced", "folder-empty"), Health(pc));

        fs.DeleteTree(pc.Source);
        Assert.Equal(("replaced", "folder-missing"), Health(pc));
        Assert.Contains("savetether app add", pc.Ok("status").Out, StringComparison.Ordinal);

        File.WriteAllText(pc.Source, "not a folder");
        Assert.Equal(("replaced", "not-a-folder"), Health(pc));
        fs.DeleteTree(pc.Source);

        // Linked again, then the shared folder goes, then the whole sync root.
        fs.CreateDirectoryLink(pc.Source, pc.Target);
        Assert.Equal(("ok", null), Health(pc));
        var keep = pc.Target + ".kept";
        Directory.Move(pc.Target, keep);
        Assert.Equal(("broken", "shared-folder-missing"), Health(pc));
        Directory.Move(keep, pc.Target);

        var away = pc.SyncRoot + ".away";
        Directory.Move(pc.SyncRoot, away);
        Assert.Equal(("broken", "sync-root-unavailable"), Health(pc));
        Directory.Move(away, pc.SyncRoot);
        Assert.Equal(("ok", null), Health(pc));

        sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void A_mapping_without_a_save_folder_here_or_with_a_relative_one_says_which()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");
        pc.SetUp(createRoot: true);

        // The device file is edited by hand from any device; a relative path is a mistake to point out.
        var deviceFile = Path.Combine(pc.SyncRoot, "savetether", ".savetether", "devices", "pc.json");
        var device = JsonNode.Parse(File.ReadAllText(deviceFile))!;
        device["paths"]![Id]!["source"] = "relative/SAVEDATA";
        File.WriteAllText(deviceFile, device.ToJsonString());
        Assert.Equal(("not-applied", "save-folder-not-absolute"), Health(pc));

        pc.Ok("map", "remove", Id);
        Assert.Equal(("not-applied", "no-save-folder"), Health(pc));
    }

    private static (string? State, string? Reason) Health(CliDevice device)
    {
        var mapping = device.Ok("status", "--json").Json().GetProperty("status").GetProperty("mappings")[0];
        var reason = mapping.TryGetProperty("reason", out var value) ? value.GetString() : null;
        return (mapping.GetProperty("state").GetString(), reason);
    }
}
