using SaveTether.Core.Backup;
using SaveTether.Core.FileSystem;
using SaveTether.Core.Model;
using SaveTether.Core.Planning;
using SaveTether.Core.Storage;

namespace SaveTether.Core.Diagnostics;

/// <summary>
/// A quick check that this build works on this machine: it writes and reads one of every
/// SaveTether file, makes a link, and takes a backup, all inside a throwaway temporary folder.
/// It exists mainly to prove the native (AOT) binary, where JSON and P/Invoke mistakes only
/// show at run time. It touches nothing outside its temporary folder.
/// </summary>
public static class SelfTest
{
    public static bool Run(TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var root = Directory.CreateTempSubdirectory("savetether-selftest-").FullName;
        var fs = new SafeFs();
        try
        {
            foreach (var (name, step) in Steps(root))
            {
                step();
                output.WriteLine($"ok: {name}");
            }

            output.WriteLine("self-test passed");
            return true;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            output.WriteLine($"FAILED: {e.Message}");
            return false;
        }
        finally
        {
            fs.DeleteTree(root);
        }
    }

    private static IEnumerable<(string Name, Action Step)> Steps(string root)
    {
        var syncRoot = Path.Combine(root, "sync");
        var saves = Path.Combine(root, "app", "saves");
        var backups = Path.Combine(root, "backups");
        var shared = new SharedStore(syncRoot);
        var local = new LocalStore(Path.Combine(root, "local"));
        var now = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        const string mappingId = "selftest.saves";

        yield return ("create folders", () =>
        {
            Directory.CreateDirectory(syncRoot);
            Directory.CreateDirectory(saves);
            Directory.CreateDirectory(backups);
            File.WriteAllText(Path.Combine(saves, "slot1.sav"), "save data");
            shared.CreateRoot();
        });

        yield return ("mapping file", () =>
        {
            shared.WriteMapping(new MappingFile
            {
                Schema = MappingFile.CurrentSchema, Id = mappingId, Target = "selftest/saves", Mechanism = Mechanism.Link,
            });
            Require(shared.ReadMapping(mappingId).Target == "selftest/saves", "mapping did not read back");
            Require(shared.ListMappings().Count == 1, "mapping was not listed");
        });

        yield return ("device file", () =>
        {
            var device = DeviceIdentity.Claim(shared, "selftest-device", backups, takeOver: false);
            shared.WriteDevice(device with
            {
                Paths = new Dictionary<string, DevicePath>
                {
                    [mappingId] = new() { Source = saves, ProcessNames = ["selftest"] },
                },
            });
            var read = shared.ReadDevice("selftest-device");
            Require(read.Paths[mappingId].Source == saves, "device path did not read back");
            Require(read.Paths[mappingId].ProcessNames.Count == 1, "process names did not read back");
        });

        yield return ("bootstrap file", () =>
        {
            var device = shared.ReadDevice("selftest-device");
            local.WriteBootstrap(new BootstrapFile
            {
                Schema = BootstrapFile.CurrentSchema, SyncRoot = syncRoot, DeviceName = device.DeviceName, InstallId = device.InstallId,
            });
            Require(DeviceIdentity.Matches(local.TryReadBootstrap()!, device), "bootstrap does not match the device file");
        });

        yield return ("seed marker", () =>
        {
            shared.WriteSeedMarker(new SeedMarker
            {
                Schema = SeedMarker.CurrentSchema, MappingId = mappingId, DeviceName = "other-device", SeededUtc = now,
                Files = [new SeedFile("slot1.sav", 9)],
            });
            var superseded = shared.SupersedeOtherMarkers(mappingId, "selftest-device", now);
            var marker = shared.ReadSeedMarkers(mappingId).Single();
            Require(superseded.Count == 1 && !marker.IsActive && marker.Files[0].Size == 9, "seed marker did not read back");
        });

        yield return ("state file", () =>
        {
            local.WriteState(new StateFile
            {
                Schema = StateFile.CurrentSchema,
                Mappings = new Dictionary<string, AppliedMapping>
                {
                    [mappingId] = new()
                    {
                        Source = saves, Target = "selftest/saves", AppliedUtc = now, SetupChoice = SetupChoice.AddMissing,
                        LinkType = OperatingSystem.IsWindows() ? LinkType.Junction : LinkType.Symlink,
                        BackupIds = ["selftest.saves/20260102T030405Z"],
                    },
                },
            });
            Require(local.ReadState().Mappings[mappingId].SetupChoice == SetupChoice.AddMissing, "state did not read back");
        });

        yield return ("backup and verify", () =>
        {
            var store = new BackupStore(backups, syncRoot);
            var backup = store.Create(mappingId, saves, BackupReason.PreApply);
            Require(store.Verify(backup.Id).IsValid, "backup did not verify");
            Require(store.List(mappingId).Single().Reason == BackupReason.PreApply, "backup was not listed");
        });

        yield return ("plan and render", () =>
        {
            var device = shared.ReadDevice("selftest-device");
            var snapshot = SnapshotReader.Read(
                shared, shared.ReadMapping(mappingId), device.DeviceName, device.Paths[mappingId], backups,
                new SystemProcessLister(), journalOpen: false);
            var outcome = Planner.Plan(snapshot, new ApplyRequest(SetupChoice.Copy, AssumeSynced: true));
            Require(outcome.Plan is { Classification: Classification.FirstDevice }, "the planner did not see a first device");
            Require(PlanRenderer.ToJson(outcome).Contains("\"first-device\"", StringComparison.Ordinal), "the plan did not render as JSON");
            Require(PlanRenderer.ToText(outcome).Contains("1. Back up", StringComparison.Ordinal), "the plan did not render as text");
        });

        yield return ("directory link", () =>
        {
            var link = Path.Combine(root, "link");
            var fs = new SafeFs(syncRoot);
            fs.CreateDirectoryLink(link, saves);
            Require(File.ReadAllText(Path.Combine(link, "slot1.sav")) == "save data", "could not read through the link");
            fs.RemoveLink(link);
            Require(SafeFs.Probe(link) == EntryKind.Missing, "the link was not removed");
            Require(SafeFs.Probe(Path.Combine(saves, "slot1.sav")) == EntryKind.File, "removing the link touched its target");
        });
    }

    private static void Require(bool condition, string failure)
    {
        if (!condition)
        {
            throw new InvalidOperationException(failure);
        }
    }
}
