using System.Text.Json.Serialization;
using SaveTether.Core.Model;

namespace SaveTether.Core.Planning;

/// <summary>Which row of the classification table applies (design 5.3). Checked in this order.</summary>
public enum Classification
{
    /// <summary>The structural checks failed, so the situation was not classified.</summary>
    [JsonStringEnumMemberName("not-classified")]
    NotClassified,

    /// <summary>Row 1: S is already a link to T.</summary>
    [JsonStringEnumMemberName("already-applied")]
    AlreadyApplied,

    /// <summary>S is a link to T, but T is gone.</summary>
    [JsonStringEnumMemberName("broken-link")]
    BrokenLink,

    /// <summary>Row 2: S is a link to somewhere else.</summary>
    [JsonStringEnumMemberName("foreign-link")]
    ForeignLink,

    /// <summary>Row 3: a device seeded this mapping, but T is still empty here.</summary>
    [JsonStringEnumMemberName("not-arrived")]
    NotArrived,

    /// <summary>Row 4: this device has no saves, so it just links.</summary>
    [JsonStringEnumMemberName("link-only")]
    LinkOnly,

    /// <summary>Row 5: this device has saves and the shared location is empty.</summary>
    [JsonStringEnumMemberName("first-device")]
    FirstDevice,

    /// <summary>Row 6: both this device and the shared location hold saves.</summary>
    [JsonStringEnumMemberName("guard")]
    Guard,
}

/// <summary>What the executor does for one step.</summary>
public enum StepKind
{
    /// <summary>Copy S to the backup store and verify the copy.</summary>
    [JsonStringEnumMemberName("back-up-source")]
    BackUpSource,

    /// <summary>Copy T to the backup store and verify the copy. Only before replacing shared data.</summary>
    [JsonStringEnumMemberName("back-up-target")]
    BackUpTarget,

    /// <summary>Create T if it does not exist.</summary>
    [JsonStringEnumMemberName("ensure-target")]
    EnsureTarget,

    /// <summary>Delete what T holds. Only for replace-shared: the one exception to invariant 2.</summary>
    [JsonStringEnumMemberName("clear-target")]
    ClearTarget,

    /// <summary>Copy from S into T: everything, or only the named top-level entries.</summary>
    [JsonStringEnumMemberName("copy-into-target")]
    CopyIntoTarget,

    /// <summary>Rename S aside, so it can be put back if anything fails.</summary>
    [JsonStringEnumMemberName("rename-aside")]
    RenameAside,

    /// <summary>Create the link S -> T.</summary>
    [JsonStringEnumMemberName("create-link")]
    CreateLink,

    /// <summary>Check S is a link to T and shows T's content.</summary>
    [JsonStringEnumMemberName("verify-link")]
    VerifyLink,

    /// <summary>Record that this device's saves became the shared copy.</summary>
    [JsonStringEnumMemberName("write-seed-marker")]
    WriteSeedMarker,

    /// <summary>Mark other devices' seed markers as superseded.</summary>
    [JsonStringEnumMemberName("supersede-markers")]
    SupersedeMarkers,

    /// <summary>Record the result in this device's state file.</summary>
    [JsonStringEnumMemberName("write-state")]
    WriteState,

    /// <summary>Delete the folder that was renamed aside. Its content is in the backup.</summary>
    [JsonStringEnumMemberName("delete-aside")]
    DeleteAside,
}

/// <summary>One step of a plan.</summary>
/// <param name="Path">The path the step acts on.</param>
/// <param name="OtherPath">The second path, where a step has one (the link's target, the aside name).</param>
/// <param name="Entries">For copy-into-target: the top-level entries to copy, or null for everything.</param>
public sealed record PlanStep(StepKind Kind, string Path, string? OtherPath = null, IReadOnlyList<string>? Entries = null);

/// <summary>Why the tool will not go ahead.</summary>
public enum RefusalKind
{
    /// <summary>One or more preconditions failed. See <see cref="Refusal.Failures"/>.</summary>
    [JsonStringEnumMemberName("precondition")]
    Precondition,

    /// <summary>The owner has to choose what happens to this device's saves.</summary>
    [JsonStringEnumMemberName("choice-needed")]
    ChoiceNeeded,

    /// <summary>The choice given is not allowed in this situation.</summary>
    [JsonStringEnumMemberName("choice-not-allowed")]
    ChoiceNotAllowed,

    /// <summary>Replacing shared data needs its second, explicit confirmation.</summary>
    [JsonStringEnumMemberName("confirmation-needed")]
    ConfirmationNeeded,

    /// <summary>The shared copy may not have fully arrived.</summary>
    [JsonStringEnumMemberName("not-arrived")]
    NotArrived,

    /// <summary>S is a link SaveTether did not create.</summary>
    [JsonStringEnumMemberName("foreign-link")]
    ForeignLink,

    /// <summary>S links to T, but T is missing.</summary>
    [JsonStringEnumMemberName("broken-link")]
    BrokenLink,
}

/// <summary>A precondition from design 5.3.</summary>
public enum PrecheckKind
{
    [JsonStringEnumMemberName("sync-root-not-initialized")]
    SyncRootNotInitialized,

    [JsonStringEnumMemberName("unfinished-operation")]
    UnfinishedOperation,

    [JsonStringEnumMemberName("target-outside-data-root")]
    TargetOutsideDataRoot,

    [JsonStringEnumMemberName("source-inside-sync-root")]
    SourceInsideSyncRoot,

    [JsonStringEnumMemberName("paths-overlap")]
    PathsOverlap,

    [JsonStringEnumMemberName("source-parent-missing")]
    SourceParentMissing,

    [JsonStringEnumMemberName("source-unusable")]
    SourceUnusable,

    [JsonStringEnumMemberName("target-unusable")]
    TargetUnusable,

    [JsonStringEnumMemberName("app-running")]
    AppRunning,

    [JsonStringEnumMemberName("backup-location-unusable")]
    BackupLocationUnusable,

    [JsonStringEnumMemberName("target-not-local")]
    TargetNotLocal,

    [JsonStringEnumMemberName("not-enough-space")]
    NotEnoughSpace,
}

public sealed record PrecheckFailure(PrecheckKind Kind, string Message);

/// <summary>What the owner asked for on the command line.</summary>
/// <param name="Choice">What to do with this device's saves; null when not given.</param>
/// <param name="ConfirmReplace">The second confirmation that replace-shared needs.</param>
/// <param name="AssumeSynced">The owner states the shared copy has fully arrived.</param>
/// <param name="AssumeClosed">The owner states the app is closed, overriding the process check.</param>
public sealed record ApplyRequest(
    SetupChoice? Choice = null, bool ConfirmReplace = false, bool AssumeSynced = false, bool AssumeClosed = false);

/// <summary>The steps to run, in order. A dry run prints exactly this; a real run executes it.</summary>
public sealed record Plan(
    string MappingId,
    Classification Classification,
    SetupChoice? Choice,
    string Source,
    string Target,
    IReadOnlyList<PlanStep> Steps,
    IReadOnlyList<string> Notes,
    bool NeedsFirstSeedConfirmation);

/// <summary>Why nothing will be done, and what the owner can do about it. Nothing has been changed.</summary>
public sealed record Refusal(
    string MappingId,
    RefusalKind Kind,
    Classification Classification,
    string Message,
    IReadOnlyList<PrecheckFailure> Failures,
    IReadOnlyList<SetupChoice> AllowedChoices,
    bool AssumeSyncedWouldHelp,
    Comparison? Comparison);

/// <summary>Either a plan or a refusal, never both.</summary>
public sealed record PlanOutcome(Plan? Plan, Refusal? Refusal)
{
    [JsonIgnore]
    public bool IsPlanned => Plan is not null;
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PlanOutcome))]
internal sealed partial class PlanningJsonContext : JsonSerializerContext;
