using SaveTether.Core.FileSystem;
using SaveTether.Core.Health;
using SaveTether.Core.Operations;
using SaveTether.Core.Planning;
using SaveTether.Core.Storage;

namespace SaveTether.Gui.Presentation;

/// <summary>Which row of section 3's table applies. The first that matches wins, in this order.</summary>
public enum OverallCondition
{
    /// <summary>Row 1: no <c>bootstrap.json</c>.</summary>
    NotSetUp,

    /// <summary>Row 2: a file was written by a newer SaveTether.</summary>
    UpdateNeeded,

    /// <summary>
    /// The core refused to report for another reason, such as this device's file missing from the
    /// sync folder. Not in section 3's table as written; it sits after row 2.
    /// </summary>
    CannotCheck,

    /// <summary>Row 3: the journal is open.</summary>
    Unfinished,

    /// <summary>Row 4: the sync folder cannot be used.</summary>
    SyncRootUnavailable,

    /// <summary>Phase 3.6, D6: another device removed this one from the sync folder. Texts come with the Maintenance screen.</summary>
    RemovedByAnother,

    /// <summary>Phase 3.6, D6: this device's own removal did not finish.</summary>
    RemovalUnfinished,

    /// <summary>Row 5: another install uses this device's name.</summary>
    DeviceNameInUse,

    /// <summary>Row 6: a mapping needs the owner.</summary>
    NeedsAttention,

    /// <summary>Row 7: everything is as it should be.</summary>
    AllLinked,

    /// <summary>Checks failed unexpectedly three times in a row (section 10).</summary>
    CheckFailed,
}

/// <summary>One mapping as the window and the notifications show it.</summary>
/// <param name="AppName">The app's name from its definition, or the part of the mapping ID before the dot.</param>
/// <param name="Role">The part of the mapping ID after the dot.</param>
public sealed record MappingState(string MappingId, string AppName, string Role, StateWord Word, MappingHealth Health)
{
    /// <summary>For a mapping with no folder on this device: the devices that have one set (section 5.1, "Set up on &lt;devices&gt;").</summary>
    public IReadOnlyList<string> SetUpOn { get; init; } = [];

    /// <summary>For a save folder that is a link elsewhere (Retargeted, Foreign link): where it points.</summary>
    public string? LinkTarget { get; init; }

    /// <summary>What a linked folder is: "Symlink" or "Junction". Null when it is not linked, or the kind could not be told.</summary>
    public string? LinkKind { get; init; }

    /// <summary>True when the catalog has a usable definition for the app, so Add app can find it (section 8).</summary>
    public bool HasDefinition { get; init; }

    /// <summary>The part of the mapping ID before the dot: the app's ID in the catalog.</summary>
    public string AppId => MappingId.Split('.')[0];
}

/// <summary>The result of one check: the state of the device, as section 3 and 5.1 show it.</summary>
/// <param name="Detail">The core's message, for the conditions that come from one.</param>
public sealed record OverallStatus(
    OverallCondition Condition, StatusReport? Report, IReadOnlyList<MappingState> Mappings, string? Detail = null)
{
    public bool IsAmber => Condition != OverallCondition.AllLinked;

    /// <summary>The first line of the tray menu, and the state word in the window's header.</summary>
    public string Headline => Condition switch
    {
        OverallCondition.NotSetUp => "Not set up",
        OverallCondition.UpdateNeeded => "Update SaveTether",
        OverallCondition.CannotCheck => "Cannot check this device",
        OverallCondition.Unfinished => "An operation did not finish",
        OverallCondition.SyncRootUnavailable => "Sync folder not available",
        OverallCondition.RemovedByAnother => "Removed from the sync folder",
        OverallCondition.RemovalUnfinished => "Removal not finished",
        OverallCondition.DeviceNameInUse => "Device name used by another install",
        OverallCondition.NeedsAttention => AttentionHeadline(),
        OverallCondition.CheckFailed => "Health check failed",
        _ => GreenHeadline(),
    };

    public IReadOnlyList<MappingState> Attention => Mappings.Where(m => m.Word.NeedsAttention).ToList();

    /// <summary>
    /// Green, so nothing needs the owner, but "All linked" only when it is so: the folders this device has and has not
    /// linked yet ("Ready to link") are counted, as "Apply all" counts them. "Not set up here" is not counted: a
    /// mapping set up on another device and not wanted here is normal (5.14). Found by the owner on 2026-11-20, with
    /// every link reverted and the header still saying "All linked".
    /// </summary>
    private string GreenHeadline()
    {
        var ready = Mappings.Count(m => m.Health.State == HealthState.NotApplied && m.Health.Reason != HealthReason.NoSaveFolder);
        if (ready == 0)
        {
            return "All linked";
        }

        var linked = Mappings.Count(m => m.Health.State == HealthState.Ok);
        return linked == 0 ? $"{ready} ready to link" : $"{linked} linked, {ready} ready to link";
    }

    private string AttentionHeadline()
    {
        var apps = Attention.Select(m => m.AppName).Distinct(StringComparer.Ordinal).Count();
        return apps == 1 ? "1 app needs attention" : $"{apps} apps need attention";
    }
}

/// <summary>Works out the overall state from the core. Reads only, and quickly (design 5.5).</summary>
public static class StatusCheck
{
    public static OverallStatus Run(LocalStore local)
    {
        ArgumentNullException.ThrowIfNull(local);
        try
        {
            if (local.TryReadBootstrap() is null)
            {
                return new OverallStatus(OverallCondition.NotSetUp, null, []);
            }

            var workspace = Workspace.Open(local, NoProcesses.Instance);
            var report = workspace.Status();
            var others = report.SyncRootAvailable ? OtherDevicesPaths(workspace) : [];
            var mappings = report.Mappings
                .Select(m =>
                {
                    var (name, defined) = AppOf(workspace, m.MappingId);
                    return new MappingState(m.MappingId, name, RoleOf(m.MappingId), StateWords.For(m.State, m.Reason), m)
                    {
                        HasDefinition = defined,
                        SetUpOn = m.Reason == HealthReason.NoSaveFolder
                            ? others.Where(d => d.Value.Contains(m.MappingId)).Select(d => d.Key).Order(StringComparer.Ordinal).ToList()
                            : [],
                        LinkTarget = m.State is HealthState.Retargeted or HealthState.ForeignLink ? LinkTargetOf(m.Source) : null,
                        LinkKind = m.State == HealthState.Ok && m.Source is { } linked ? LinkKindOf(linked) : null,
                    };
                })
                .OrderBy(m => m.Word.NeedsAttention ? 0 : 1)
                .ThenBy(m => m.AppName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(m => m.MappingId, StringComparer.Ordinal)
                .ToList();

            var condition =
                report.JournalOpen ? OverallCondition.Unfinished
                : !report.SyncRootAvailable ? OverallCondition.SyncRootUnavailable
                : report.Removal == RemovalKind.RemovedByAnother ? OverallCondition.RemovedByAnother
                : report.Removal == RemovalKind.Unfinished ? OverallCondition.RemovalUnfinished
                : report.DeviceFileMismatch ? OverallCondition.DeviceNameInUse
                : mappings.Any(m => m.Word.NeedsAttention) ? OverallCondition.NeedsAttention
                : OverallCondition.AllLinked;
            // Banner B3 is one sentence with the reason in a few words; the core's longer message would repeat it.
            var detail = condition == OverallCondition.SyncRootUnavailable
                ? new SharedStore(report.SyncRoot).NotAvailableReason
                : report.SyncRootProblem;
            return new OverallStatus(condition, report, mappings, detail);
        }
        catch (StoreException e) when (e.Problem == StoreProblem.SchemaTooNew)
        {
            return new OverallStatus(OverallCondition.UpdateNeeded, null, [], e.Message);
        }
        catch (StoreException e)
        {
            return new OverallStatus(OverallCondition.CannotCheck, null, [], e.Message);
        }
    }

    /// <summary>The app's name from its definition, and whether there is a usable one; the ID and false otherwise.</summary>
    private static (string Name, bool Defined) AppOf(Workspace workspace, string mappingId)
    {
        var parts = mappingId.Split('.', 2);
        return AppNames.Resolve(workspace.Definitions, parts[0], parts.Length == 2 ? parts[1] : null, () => workspace.Shared.TryReadMapping(mappingId)?.Target);
    }

    private static string? LinkKindOf(string folder) => SafeFs.Probe(folder) switch
    {
        EntryKind.Symlink => "Symlink",
        EntryKind.Junction => "Junction",
        _ => null,
    };

    /// <summary>Each other device's mappings with a folder set. A device file that cannot be read is left out.</summary>
    private static Dictionary<string, HashSet<string>> OtherDevicesPaths(Workspace workspace)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var name in workspace.Shared.ListDeviceNames().Where(n => n != workspace.DeviceName))
        {
            try
            {
                if (workspace.Shared.TryReadDevice(name) is { } device)
                {
                    result[name] = device.Paths.Keys.ToHashSet(StringComparer.Ordinal);
                }
            }
            catch (IOException)
            {
            }
        }

        return result;
    }

    private static string? LinkTargetOf(string? source)
    {
        try
        {
            return source is not null && SafeFs.Probe(source) is EntryKind.Symlink or EntryKind.Junction ? SafeFs.ReadLinkTarget(source) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string RoleOf(string mappingId) => mappingId.Contains('.', StringComparison.Ordinal) ? mappingId[(mappingId.IndexOf('.', StringComparison.Ordinal) + 1)..] : mappingId;
}
