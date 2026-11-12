using SaveTether.Core.Backup;
using SaveTether.Core.FileSystem;
using SaveTether.Tests.FileSystem;

namespace SaveTether.Tests.Backup;

public class BackupStoreTests
{
    private const string Mapping = "ppsspp.savedata";

    [Fact]
    public void Create_makes_a_plain_copy_that_verifies_and_matches_the_source()
    {
        using var sandbox = new Sandbox();
        var source = BuildSaves(sandbox);
        var store = new BackupStore(sandbox.Dir("backups"));

        var backup = store.Create(Mapping, source, BackupReason.PreApply);

        Assert.StartsWith(Mapping + "/", backup.Id, StringComparison.Ordinal);
        Assert.Equal(3, backup.FileCount);
        Assert.Equal("slot one".Length + "slot two".Length + "cfg".Length, backup.TotalBytes);
        Assert.True(store.Verify(backup.Id).IsValid);

        // The data folder is an ordinary tree: same entries, same bytes, as the source.
        var data = store.DataPathOf(backup.Id);
        Assert.Equal(
            SafeFs.EnumerateTree(source).Select(e => (e.RelativePath, e.Kind, e.Length)),
            SafeFs.EnumerateTree(data).Select(e => (e.RelativePath, e.Kind, e.Length)));
        foreach (var file in SafeFs.EnumerateTree(source).Where(e => e.Kind == EntryKind.File))
        {
            Assert.Equal(
                File.ReadAllBytes(Path.Combine(source, file.RelativePath)),
                File.ReadAllBytes(Path.Combine(data, file.RelativePath)));
        }

        Assert.Equal(EntryKind.Directory, SafeFs.Probe(Path.Combine(data, "empty-folder")));
    }

    [Fact]
    public void Manifest_on_disk_is_readable_json_with_hashes_and_the_reason()
    {
        using var sandbox = new Sandbox();
        var source = sandbox.Dir("saves");
        sandbox.FileAt("abc", "saves", "a.sav");
        var store = new BackupStore(sandbox.Dir("backups"));

        var backup = store.Create(Mapping, source, BackupReason.PreRevert);

        var json = File.ReadAllText(Path.Combine(backup.Location, "manifest.json"));
        Assert.Contains("\"schema\": 1", json, StringComparison.Ordinal);
        Assert.Contains("\"mapping_id\": \"ppsspp.savedata\"", json, StringComparison.Ordinal);
        Assert.Contains("\"reason\": \"pre-revert\"", json, StringComparison.Ordinal);
        Assert.Contains("\"path\": \"a.sav\"", json, StringComparison.Ordinal);
        Assert.Contains("\"size\": 3", json, StringComparison.Ordinal);

        // SHA-256 of "abc", a published test vector.
        Assert.Contains("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_detects_a_changed_a_missing_and_an_extra_file()
    {
        using var sandbox = new Sandbox();
        var source = BuildSaves(sandbox);
        var store = new BackupStore(sandbox.Dir("backups"));
        var backup = store.Create(Mapping, source, BackupReason.Baseline);
        var data = store.DataPathOf(backup.Id);

        // Same length, one character different.
        File.WriteAllText(Path.Combine(data, "slot1.sav"), "slot 0ne");
        var changed = store.Verify(backup.Id);
        Assert.False(changed.IsValid);
        Assert.Contains(changed.Problems, p => p.Contains("content differs", StringComparison.Ordinal) && p.Contains("slot1.sav", StringComparison.Ordinal));

        File.WriteAllText(Path.Combine(data, "slot1.sav"), "longer than before");
        Assert.Contains(store.Verify(backup.Id).Problems, p => p.Contains("size differs", StringComparison.Ordinal));

        File.WriteAllText(Path.Combine(data, "slot1.sav"), "slot one");
        Assert.True(store.Verify(backup.Id).IsValid);

        File.WriteAllText(Path.Combine(data, "sneaked-in.sav"), "x");
        Assert.Contains(store.Verify(backup.Id).Problems, p => p.Contains("Not in the manifest: sneaked-in.sav", StringComparison.Ordinal));

        new SafeFs().DeleteTree(Path.Combine(data, "sneaked-in.sav"));
        new SafeFs().DeleteTree(Path.Combine(data, "game2"));
        var missing = store.Verify(backup.Id).Problems;
        Assert.Contains(missing, p => p.Contains("File missing from the backup: game2/slot2.sav", StringComparison.Ordinal));
        Assert.Contains(missing, p => p.Contains("Folder missing from the backup: game2", StringComparison.Ordinal));
    }

    [Fact]
    public void Verify_reports_a_missing_or_corrupt_manifest()
    {
        using var sandbox = new Sandbox();
        var store = new BackupStore(sandbox.Dir("backups"));
        var backup = store.Create(Mapping, BuildSaves(sandbox), BackupReason.Baseline);
        var manifest = Path.Combine(backup.Location, "manifest.json");

        File.WriteAllText(manifest, "{ not json");
        Assert.False(store.Verify(backup.Id).IsValid);
        Assert.Empty(store.List());

        File.WriteAllText(manifest, "{ \"schema\": 99 }");
        Assert.False(store.Verify(backup.Id).IsValid);
    }

    [Fact]
    public void A_failed_backup_removes_only_itself()
    {
        using var sandbox = new Sandbox();
        var source = BuildSaves(sandbox);
        var store = new BackupStore(sandbox.Dir("backups"), time: new FakeTime());
        var earlier = store.Create(Mapping, source, BackupReason.Baseline);

        // Corrupt the copy before it is verified, as a failing disk or a still-running app would.
        store.BeforeVerify = partial => File.WriteAllText(Path.Combine(partial, "data", "slot1.sav"), "garbage!");
        var failure = Assert.Throws<BackupException>(() => store.Create(Mapping, source, BackupReason.PreApply));
        Assert.Contains("did not match the original", failure.Message, StringComparison.Ordinal);
        store.BeforeVerify = null;

        // Nothing unfinished is left, the earlier backup is intact, the source is untouched.
        var folders = Directory.GetDirectories(sandbox.At("backups", Mapping));
        Assert.Equal([earlier.Location], folders);
        Assert.Equal([earlier.Id], store.List().Select(b => b.Id));
        Assert.True(store.Verify(earlier.Id).IsValid);
        Assert.Equal("slot one", File.ReadAllText(Path.Combine(source, "slot1.sav")));
    }

    [Fact]
    public void Links_inside_the_source_are_recorded_and_never_followed()
    {
        using var sandbox = new Sandbox();
        var source = LinkyTree.Build(sandbox);
        var store = new BackupStore(sandbox.Dir("backups"));

        var backup = store.Create(Mapping, source, BackupReason.PreApply);

        var data = store.DataPathOf(backup.Id);
        Assert.DoesNotContain(SafeFs.EnumerateTree(data), e => e.Kind is EntryKind.Symlink or EntryKind.Junction);
        Assert.DoesNotContain(SafeFs.EnumerateTree(data), e => e.RelativePath.EndsWith("keep.txt", StringComparison.Ordinal));
        Assert.True(store.Verify(backup.Id).IsValid);

        var json = File.ReadAllText(Path.Combine(backup.Location, "manifest.json"));
        Assert.Contains("\"path\": \"link-to-canary\"", json, StringComparison.Ordinal);
        Assert.Contains("\"path\": \"sub/a/b/c/deep-link\"", json, StringComparison.Ordinal);
        Assert.Contains(OperatingSystem.IsWindows() ? "\"kind\": \"junction\"" : "\"kind\": \"symlink\"", json, StringComparison.Ordinal);
        sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void Backups_taken_in_the_same_second_get_distinct_ids()
    {
        using var sandbox = new Sandbox();
        var source = BuildSaves(sandbox);
        var store = new BackupStore(sandbox.Dir("backups"), time: new FakeTime());

        var ids = Enumerable.Range(0, 3).Select(_ => store.Create(Mapping, source, BackupReason.Snapshot).Id).ToList();

        Assert.Equal(
            ["ppsspp.savedata/20260102T030405Z", "ppsspp.savedata/20260102T030405Z-2", "ppsspp.savedata/20260102T030405Z-3"],
            ids);
        Assert.All(ids, id => Assert.True(store.Verify(id).IsValid));
    }

    [Fact]
    public void List_is_oldest_first_and_can_filter_by_mapping()
    {
        using var sandbox = new Sandbox();
        var source = BuildSaves(sandbox);
        var time = new FakeTime();
        var store = new BackupStore(sandbox.Dir("backups"), time: time);

        var first = store.Create(Mapping, source, BackupReason.Baseline);
        time.Advance(TimeSpan.FromHours(1));
        var other = store.Create("eden.save", source, BackupReason.PreApply);
        time.Advance(TimeSpan.FromHours(1));
        var second = store.Create(Mapping, source, BackupReason.PreRevert);

        Assert.Equal([first.Id, second.Id], store.List(Mapping).Select(b => b.Id));
        Assert.Equal([other.Id, first.Id, second.Id], store.List().Select(b => b.Id));
        Assert.Empty(store.List("never.used"));

        var listed = store.List(Mapping)[1];
        Assert.Equal(BackupReason.PreRevert, listed.Reason);
        Assert.Equal(PathRules.Normalize(source), listed.SourcePath);
        Assert.Equal(time.GetUtcNow(), listed.CreatedUtc);
    }

    [Fact]
    public void Prune_keeps_the_newest_and_always_the_first()
    {
        using var sandbox = new Sandbox();
        var source = BuildSaves(sandbox);
        var time = new FakeTime();
        var store = new BackupStore(sandbox.Dir("backups"), time: time);

        var all = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            all.Add(store.Create(Mapping, source, BackupReason.Snapshot).Id);
            time.Advance(TimeSpan.FromMinutes(1));
        }

        var untouched = store.Create("eden.save", source, BackupReason.Snapshot).Id;

        var deleted = store.Prune(Mapping, keep: 2);

        Assert.Equal([all[1], all[2], all[3]], deleted);
        Assert.Equal([all[0], all[4], all[5]], store.List(Mapping).Select(b => b.Id));
        Assert.Equal([untouched], store.List("eden.save").Select(b => b.Id));
        Assert.Empty(store.Prune(Mapping, keep: 2));
        Assert.Empty(store.Prune("never.used"));
    }

    [Fact]
    public void Prune_clears_unfinished_backups_left_by_a_crash()
    {
        using var sandbox = new Sandbox();
        var store = new BackupStore(sandbox.Dir("backups"));
        var backup = store.Create(Mapping, BuildSaves(sandbox), BackupReason.Baseline);
        var stale = sandbox.Dir("backups", Mapping, "20200101T000000Z.partial", "data");

        Assert.Equal([backup.Id], store.List(Mapping).Select(b => b.Id));
        store.Prune(Mapping);

        Assert.Equal(EntryKind.Missing, SafeFs.Probe(Path.GetDirectoryName(stale)!));
        Assert.True(store.Verify(backup.Id).IsValid);
    }

    [Fact]
    public void The_backup_location_must_exist_and_be_outside_the_sync_root()
    {
        using var sandbox = new Sandbox();
        var syncRoot = sandbox.Dir("sync");
        var inside = sandbox.Dir("sync", "backups");
        var alias = sandbox.At("alias");
        Sandbox.Link(alias, inside);

        Assert.Throws<BackupException>(() => new BackupStore(sandbox.At("not-there")));
        Assert.Throws<BackupException>(() => new BackupStore("relative/backups"));
        Assert.Throws<BackupException>(() => new BackupStore(inside, syncRoot));
        Assert.Throws<BackupException>(() => new BackupStore(syncRoot, syncRoot));
        Assert.Throws<BackupException>(() => new BackupStore(sandbox.Work, syncRoot));

        // A link that leads into the sync root does not get round the rule.
        Assert.Throws<BackupException>(() => new BackupStore(alias, syncRoot));
    }

    [Fact]
    public void Create_refuses_bad_requests_and_writes_nothing()
    {
        using var sandbox = new Sandbox();
        var backups = sandbox.Dir("backups");
        var store = new BackupStore(backups);
        var source = BuildSaves(sandbox);
        var link = sandbox.At("link");
        Sandbox.Link(link, source);

        Assert.Throws<BackupException>(() => store.Create("../escape", source, BackupReason.PreApply));
        Assert.Throws<BackupException>(() => store.Create("NoDots", source, BackupReason.PreApply));
        Assert.Throws<BackupException>(() => store.Create(Mapping, sandbox.At("missing"), BackupReason.PreApply));
        Assert.Throws<BackupException>(() => store.Create(Mapping, Path.Combine(source, "slot1.sav"), BackupReason.PreApply));
        Assert.Throws<BackupException>(() => store.Create(Mapping, link, BackupReason.PreApply));
        Assert.Throws<BackupException>(() => store.Create(Mapping, "relative", BackupReason.PreApply));

        Assert.Empty(store.List());
        Assert.Throws<BackupException>(() => store.Verify("ppsspp.savedata/../../etc"));
        Assert.Throws<BackupException>(() => store.Verify("ppsspp.savedata/20260101T000000Z"));
    }

    private static string BuildSaves(Sandbox sandbox)
    {
        var saves = sandbox.Dir("saves");
        sandbox.FileAt("slot one", "saves", "slot1.sav");
        sandbox.FileAt("slot two", "saves", "game2", "slot2.sav");
        sandbox.FileAt("cfg", "saves", "game2", "settings.ini");
        sandbox.Dir("saves", "empty-folder");
        return saves;
    }

    private sealed class FakeTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
