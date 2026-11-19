namespace SaveTether.Core.Model;

/// <summary>
/// Something the owner can do about a refusal, a problem or a state, said without naming any
/// front end's commands. Each front end turns it into its own words: the CLI into the command and
/// flags to type, the desktop app into a button (design: desktop-app-spec.md, section 13).
/// </summary>
public enum NextStepKind
{
    /// <summary>Finish or undo the unfinished operation.</summary>
    Recover,

    /// <summary>Make the mapping's save folder a real folder again. <see cref="NextStep.MappingId"/> is set.</summary>
    Revert,

    /// <summary>Link the mapping. <see cref="NextStep.MappingId"/> is set.</summary>
    Apply,

    /// <summary>Set this device's save folder for an existing mapping. <see cref="NextStep.MappingId"/> is set.</summary>
    SetSaveFolder,

    /// <summary>Create the mapping, with its shared folder, and set this device's save folder. <see cref="NextStep.MappingId"/> is set.</summary>
    AddMapping,

    /// <summary>A new mapping needs its shared folder named. <see cref="NextStep.MappingId"/> is set.</summary>
    GiveTarget,

    /// <summary>Drop this device's save folder for the mapping, reverting it first if applied. <see cref="NextStep.MappingId"/> is set.</summary>
    RemoveMapping,

    /// <summary>Change the mapping's entry depth. <see cref="NextStep.MappingId"/> is set.</summary>
    ChangeEntryDepth,

    /// <summary>Set this device up: choose the sync root and a device name.</summary>
    SetUp,

    /// <summary>Run setup again, for a device whose own file is missing from the sync root.</summary>
    SetUpAgain,

    /// <summary>On the first device only: create SaveTether's folder in the sync root.</summary>
    CreateRoot,

    /// <summary>Choose another backup location for this device.</summary>
    ChooseBackupLocation,

    /// <summary>Take over a device name that another install holds.</summary>
    TakeOverName,

    /// <summary>Say which install of the app to use.</summary>
    ChooseInstall,

    /// <summary>Say which of the app's roles to add.</summary>
    NameRoles,

    /// <summary>Give a value the app's location is found from: install folder, SD card or Wine prefix.</summary>
    GiveLocation,

    /// <summary>More than one SD card or Wine prefix was found: say which to use.</summary>
    ChooseLocation,

    /// <summary>The app was not where it was last time: give its new location.</summary>
    GiveNewLocation,

    /// <summary>Add the app again, so its new location is picked up.</summary>
    FindAppAgain,

    /// <summary>Choose what happens to this device's saves, from the refusal's allowed choices.</summary>
    ChooseSetup,

    /// <summary>The choice given is not allowed here: use one of the refusal's allowed choices.</summary>
    UseAllowedChoice,

    /// <summary>Apply without a choice, which just links.</summary>
    ApplyWithoutChoice,

    /// <summary>Confirm, separately and explicitly, that shared data is replaced on every device.</summary>
    ConfirmReplace,

    /// <summary>Make this device's saves the shared copy again, with the sync stated finished. <see cref="NextStep.MappingId"/> is set.</summary>
    ReseedFromHere,

    /// <summary>Make this device's copy the one every device keeps. <see cref="NextStep.MappingId"/> is set.</summary>
    MakeThisCopyWin,

    /// <summary>Let SaveTether give a Flatpak app write access to the SaveTether folder in the sync root.</summary>
    GrantSandbox,

    /// <summary>See which backups there are.</summary>
    ListBackups,

    /// <summary>Restore a backup of a shared folder. <see cref="NextStep.Value"/> is the backup ID.</summary>
    RestoreSharedBackup,
}

/// <param name="MappingId">The mapping it is about, where the kind says one is set.</param>
/// <param name="Value">Anything else the kind needs, such as a backup ID.</param>
/// <param name="Choices">For <see cref="NextStepKind.ChooseSetup"/> and <see cref="NextStepKind.UseAllowedChoice"/>: the choices to pick from.</param>
public sealed record NextStep(
    NextStepKind Kind, string? MappingId = null, string? Value = null, IReadOnlyList<SetupChoice>? Choices = null);

/// <summary>
/// A message for the owner: what is wrong or what happened, with the facts, in words that suit every
/// front end; and what can be done next, as <see cref="NextStep"/>s each front end words itself.
/// </summary>
public sealed record Note(string Text, IReadOnlyList<NextStep> Next)
{
    public Note(string text)
        : this(text, [])
    {
    }

    public Note(string text, NextStep next)
        : this(text, [next])
    {
    }

    public static implicit operator Note(string text) => new(text);

    public override string ToString() => Text;
}
