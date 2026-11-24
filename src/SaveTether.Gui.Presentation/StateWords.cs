using SaveTether.Core.Health;

namespace SaveTether.Gui.Presentation;

/// <summary>How a state is shown: amber needs the owner, grey is normal but not linked, green is linked.</summary>
public enum Shade
{
    Green,
    Grey,
    Amber,
}

/// <summary>The word for a mapping's state, and its shade (desktop-app-spec.md, section 5.1).</summary>
public sealed record StateWord(string Word, Shade Shade)
{
    public bool NeedsAttention => Shade == Shade.Amber;
}

/// <summary>Section 5.1's table: each core state and reason to a word and a shade.</summary>
public static class StateWords
{
    public static StateWord For(HealthState state, HealthReason? reason) => (state, reason) switch
    {
        (HealthState.Ok, _) => new("Linked", Shade.Green),

        (HealthState.NotApplied, HealthReason.NoSaveFolder) => new("Not on this device", Shade.Grey),

        // Unlike the core's Not applied, a folder setting that is wrong turns the icon amber (decision 4).
        (HealthState.NotApplied, HealthReason.SaveFolderNotAbsolute) => new("Device file error", Shade.Amber),
        (HealthState.NotApplied, _) => new("Ready to link", Shade.Grey),

        (HealthState.Replaced, HealthReason.FolderEmpty) => new("Link missing", Shade.Amber),
        (HealthState.Replaced, HealthReason.FolderMissing) => new("Folder gone", Shade.Amber),
        (HealthState.Replaced, HealthReason.NotAFolder) => new("Not a folder", Shade.Amber),
        (HealthState.Replaced, _) => new("Not linked", Shade.Amber),

        (HealthState.Broken, _) => new("Shared folder missing", Shade.Amber),
        (HealthState.Retargeted, _) => new("Linked elsewhere", Shade.Amber),
        (HealthState.DoubleSeed, _) => new("Two shared copies", Shade.Amber),
        (HealthState.ForeignLink, _) => new("Not SaveTether's link", Shade.Amber),
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "A state with no word in section 5.1."),
    };
}
