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
public sealed record MappingState(string MappingId, string AppName, string Role, StateWord Word, MappingHealth Health);

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
        OverallCondition.DeviceNameInUse => "Device name used by another install",
        OverallCondition.NeedsAttention => AttentionHeadline(),
        OverallCondition.CheckFailed => "Health check failed",
        _ => "All linked",
    };

    public IReadOnlyList<MappingState> Attention => Mappings.Where(m => m.Word.NeedsAttention).ToList();

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
            var mappings = report.Mappings
                .Select(m => new MappingState(m.MappingId, AppName(workspace, m.MappingId), RoleOf(m.MappingId), StateWords.For(m.State, m.Reason), m))
                .OrderBy(m => m.Word.NeedsAttention ? 0 : 1)
                .ThenBy(m => m.AppName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(m => m.MappingId, StringComparer.Ordinal)
                .ToList();

            var condition =
                report.JournalOpen ? OverallCondition.Unfinished
                : !report.SyncRootAvailable ? OverallCondition.SyncRootUnavailable
                : report.DeviceFileMismatch ? OverallCondition.DeviceNameInUse
                : mappings.Any(m => m.Word.NeedsAttention) ? OverallCondition.NeedsAttention
                : OverallCondition.AllLinked;
            return new OverallStatus(condition, report, mappings, report.SyncRootProblem);
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

    private static string AppName(Workspace workspace, string mappingId)
    {
        var appId = mappingId.Split('.')[0];
        try
        {
            return workspace.Definitions.Find(appId)?.Definition?.Name ?? appId;
        }
        catch (IOException)
        {
            return appId;
        }
        catch (UnauthorizedAccessException)
        {
            return appId;
        }
    }

    private static string RoleOf(string mappingId) => mappingId.Contains('.', StringComparison.Ordinal) ? mappingId[(mappingId.IndexOf('.', StringComparison.Ordinal) + 1)..] : mappingId;

    /// <summary>A status check runs no plan, so it never asks which processes are running.</summary>
    private sealed class NoProcesses : IProcessLister
    {
        public static readonly NoProcesses Instance = new();

        public IReadOnlyList<string> RunningProcessNames() => [];
    }
}
