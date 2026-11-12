using SaveTether.Core.Backup;
using SaveTether.Core.Execution;
using SaveTether.Core.FileSystem;
using SaveTether.Core.Model;
using SaveTether.Core.Planning;
using SaveTether.Tests.FileSystem;

namespace SaveTether.Tests.Execution;

/// <summary>
/// The fault matrix of the implementation plan (M5): for every kind of operation, the process
/// "dies" at each step in turn, both before the step runs and after it ran but before the
/// journal says so. Recovery then runs in a fresh executor, as it would after a restart.
/// </summary>
public class FaultMatrixTests
{
    public static TheoryData<string> Scenarios =>
        ["copy", "no-copy", "add-missing", "replace-shared", "link-only", "revert", "restore-local", "restore-shared"];

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void A_crash_at_any_step_loses_nothing_and_recovery_leaves_a_sound_state(string scenario)
    {
        int stepCount;
        using (var probe = new ExecWorld())
        {
            stepCount = Arrange(probe, scenario).Plan.Steps.Count;
        }

        Assert.True(stepCount >= 3, $"{scenario}: expected a plan with several steps");

        foreach (var point in new[] { FaultPoint.BeforeStep, FaultPoint.AfterStep })
        {
            for (var index = 0; index < stepCount; index++)
            {
                using var world = new ExecWorld();
                var (plan, assertCompleted) = Arrange(world, scenario);
                var label = $"[{scenario}, {point} step {index + 1} of {stepCount}: {plan.Steps[index].Kind}] ";
                var before = Before.Capture(world);

                Assert.Throws<SimulatedCrashException>(() => world.Executor(new CrashAt(index, point)).Run(plan));
                Assert.True(world.Journal.IsOpen, label + "the journal should be open after a crash");

                var result = world.Executor().Recover();

                Assert.True(
                    result.Status is ExecutionStatus.RolledBack or ExecutionStatus.Completed,
                    label + $"recovery ended as {result.Status}: {result.Error}");
                Assert.False(world.Journal.IsOpen, label + "the journal should be closed after recovery");

                // (a) Every file that existed before still exists, byte for byte, somewhere it can be found.
                var after = Contents(world.Source, world.Target, world.BackupRoot, world.Aside, world.Staging);
                var lost = before.Held.Except(after).ToList();
                Assert.True(lost.Count == 0, label + "lost: " + string.Join(", ", lost));

                // (b) S is what it was, or what the operation was meant to make it. Never missing.
                if (result.Status == ExecutionStatus.RolledBack)
                {
                    Assert.True(before.SourceKind == SafeFs.Probe(world.Source), label + "S changed kind");
                    Assert.True(before.SourceListing.SequenceEqual(Listing(world.Source)), label + "S's content changed");
                }
                else
                {
                    assertCompleted(world, label);
                }

                Assert.True(SafeFs.Probe(world.Source) != EntryKind.Missing, label + "S is missing");

                // (c) Nothing is left lying beside S without the owner being told.
                foreach (var leftover in new[] { world.Aside, world.Staging })
                {
                    Assert.True(
                        SafeFs.Probe(leftover) == EntryKind.Missing
                        || result.Messages.Any(m => m.Contains(leftover, StringComparison.Ordinal)),
                        label + $"'{leftover}' was left without being reported");
                }

                // (d)
                world.Sandbox.AssertCanaryIntact();
                Assert.Equal(ExecutionStatus.NothingToRecover, world.Executor().Recover().Status);
            }
        }
    }

    private static (Plan Plan, Action<ExecWorld, string> AssertCompleted) Arrange(ExecWorld world, string scenario)
    {
        const string id = ExecWorld.MappingId;
        switch (scenario)
        {
            case "copy":
                LocalSaves(world);
                Sandbox.Link(Path.Combine(world.Source, "link-to-canary"), world.Sandbox.Canary);
                return (ExecWorld.Planned(world.PlanApply(new ApplyRequest(SetupChoice.Copy))), Linked);

            case "no-copy":
                LocalSaves(world);
                SharedSaves(world);
                return (ExecWorld.Planned(world.PlanApply(new ApplyRequest(SetupChoice.NoCopy))), Linked);

            case "add-missing":
                LocalSaves(world);
                SharedSaves(world);
                return (ExecWorld.Planned(world.PlanApply(new ApplyRequest(SetupChoice.AddMissing))), Linked);

            case "replace-shared":
                LocalSaves(world);
                SharedSaves(world);
                world.Shared.WriteSeedMarker(new SeedMarker
                {
                    Schema = SeedMarker.CurrentSchema, MappingId = id, DeviceName = "desktop",
                    SeededUtc = DateTimeOffset.UnixEpoch, Files = [new SeedFile("GAME1/save.bin", 8)],
                });
                return (
                    ExecWorld.Planned(world.PlanApply(new ApplyRequest(SetupChoice.ReplaceShared, ConfirmReplace: true))),
                    Linked);

            case "link-only":
                Directory.CreateDirectory(Path.Combine(world.Source, "empty-layout"));
                SharedSaves(world);
                return (ExecWorld.Planned(world.PlanApply()), Linked);

            case "revert":
                LocalSaves(world);
                Assert.Equal(ExecutionStatus.Completed, world.Apply(new ApplyRequest(SetupChoice.Copy)).Status);
                world.SharedSave("GAME9/save.bin", "played-after-linking");
                return (ExecWorld.Planned(Planner.PlanRevert(world.Read())), (w, label) =>
                {
                    Assert.True(SafeFs.Probe(w.Source) == EntryKind.Directory, label + "S should be a real folder");
                    Assert.True(Same(ExecWorld.Files(w.Target), ExecWorld.Files(w.Source)), label + "S should hold what T holds");
                    Assert.True(w.Local.ReadState().Mappings.Count == 0, label + "the mapping should no longer be recorded");
                });

            case "restore-local":
            {
                world.LocalSave("GAME1/save.bin", "restore-v1-a");
                world.LocalSave("GAME2/save.bin", "restore-v1-b");
                var original = ExecWorld.Files(world.Source);
                var backupId = world.Backups.Create(id, world.Source, BackupReason.Baseline).Id;
                world.LocalSave("GAME1/save.bin", "restore-v2-a");
                world.LocalSave("GAME5/save.bin", "restore-v2-c");
                return (ExecWorld.Planned(world.PlanRestore(backupId)), (w, label) =>
                    Assert.True(Same(original, ExecWorld.Files(w.Source)), label + "S should hold the backup's content"));
            }

            case "restore-shared":
            {
                LocalSaves(world);
                world.SharedSave("GAME1/save.bin", "shared-A");
                var backupId = world.Backups.Create(id, world.Target, BackupReason.Snapshot).Id;
                world.SharedSave("GAME1/save.bin", "shared-B");
                world.SharedSave("GAME4/save.bin", "shared-B-extra");
                return (
                    ExecWorld.Planned(world.PlanRestore(backupId, new RestoreRequest(ConfirmReplace: true))),
                    (_, label) => Assert.Fail(label + "an interrupted restore of shared data must be reported, not finished"));
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "unknown scenario");
        }
    }

    private static void LocalSaves(ExecWorld world)
    {
        world.LocalSave("GAME1/save.bin", "local-1");
        world.LocalSave("GAME2/deep/save.bin", "local-2");
    }

    private static void SharedSaves(ExecWorld world)
    {
        world.SharedSave("GAME1/save.bin", "shared-1");
        world.SharedSave("GAME3/save.bin", "shared-3");
    }

    private static void Linked(ExecWorld world, string label)
    {
        Assert.True(ExecWorld.IsLink(world.Source), label + "S should be a link");
        Assert.True(
            PathRules.AreSame(PathRules.ResolveAll(SafeFs.ReadLinkTarget(world.Source)), PathRules.ResolveAll(world.Target)),
            label + "S should link to T");
        Assert.True(world.Local.ReadState().Mappings.ContainsKey(ExecWorld.MappingId), label + "the mapping should be recorded");
    }

    private static bool Same(Dictionary<string, string> expected, Dictionary<string, string> actual) =>
        expected.Count == actual.Count && expected.All(pair => actual.TryGetValue(pair.Key, out var value) && value == pair.Value);

    /// <summary>The content of every file under the given folders. Links are never entered.</summary>
    private static HashSet<string> Contents(params string[] roots) =>
        roots.SelectMany(root => ExecWorld.Files(root).Values).ToHashSet(StringComparer.Ordinal);

    /// <summary>Every entry below S with its kind, and each file's content.</summary>
    private static string[] Listing(string source) =>
        SafeFs.Probe(source) != EntryKind.Directory
            ? []
            : SafeFs.EnumerateTree(source)
                .Select(e => e.Kind == EntryKind.File
                    ? $"{e.RelativePath}|{File.ReadAllText(Path.Combine(source, e.RelativePath))}"
                    : $"{e.RelativePath}|{e.Kind}")
                .ToArray();

    private sealed record Before(EntryKind SourceKind, string[] SourceListing, HashSet<string> Held)
    {
        public static Before Capture(ExecWorld world) => new(
            SafeFs.Probe(world.Source), Listing(world.Source), FaultMatrixTests.Contents(world.Source, world.Target, world.BackupRoot));
    }
}
