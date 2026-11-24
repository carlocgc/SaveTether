namespace SaveTether.Core.FileSystem;

/// <summary>Whether this device can link a save folder to a network share (design 5.16).</summary>
public enum ShareLinkSupport
{
    /// <summary>A symlink to the share can be made and Windows follows it.</summary>
    Available,

    /// <summary>Policy stops Windows following a symlink from this device to a share (<see cref="SymlinkPolicy"/>).</summary>
    PolicyOff,

    /// <summary>Making a symlink needs Developer Mode or an elevated process, and this one has neither.</summary>
    NeedsDevModeOrElevation,
}

/// <summary>
/// Tries what linking to a share needs, the way design 5.16 orders it: the policy, then a test symlink that is
/// removed again. SaveTether never elevates itself; this only reports what Windows allows.
/// </summary>
public static class SymlinkCapability
{
    private const int PrivilegeNotHeld = 1314;

    /// <summary>What the user can do when <see cref="ShareLinkSupport.NeedsDevModeOrElevation"/> is the answer.</summary>
    public const string DeveloperModeFix =
        "Windows lets SaveTether link a save folder to a network share only with Developer Mode on " +
        "(Settings, System, For developers), or when SaveTether runs from a terminal opened as administrator. " +
        "Turn on Developer Mode, or run this step once from an administrator terminal, then try again.";

    /// <summary>Why a share cannot be used when <see cref="ShareLinkSupport.PolicyOff"/> is the answer.</summary>
    public const string PolicyOffReason =
        "Windows on this device is set not to follow links to a network share " +
        "(\"fsutil behavior query SymlinkEvaluation\" shows Local to remote as disabled), so a save folder linked there would not open. " +
        "Ask whoever manages this PC to allow it, or use a folder on a local drive kept in step by a sync tool.";

    /// <summary>
    /// Checks the policy, then makes a test symlink to <paramref name="target"/> in <paramref name="scratchFolder"/>
    /// (an existing local folder) and removes it through <see cref="SafeFs"/>. Always available off Windows.
    /// </summary>
    public static ShareLinkSupport Probe(string scratchFolder, string target) =>
        Probe(scratchFolder, target, SymlinkPolicy.LocalToRemoteAllowed);

    internal static ShareLinkSupport Probe(string scratchFolder, string target, Func<bool> policyAllows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scratchFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        if (!OperatingSystem.IsWindows())
        {
            return ShareLinkSupport.Available;
        }

        if (!policyAllows())
        {
            return ShareLinkSupport.PolicyOff;
        }

        var probe = Path.Combine(scratchFolder, $".link-probe-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateSymbolicLink(probe, target);
        }
        catch (IOException e) when (IsPrivilegeNotHeld(e))
        {
            return ShareLinkSupport.NeedsDevModeOrElevation;
        }

        new SafeFs().RemoveLink(probe);
        return ShareLinkSupport.Available;
    }

    /// <summary>True for Win32 error 1314, ERROR_PRIVILEGE_NOT_HELD, which .NET reports as an <see cref="IOException"/>.</summary>
    internal static bool IsPrivilegeNotHeld(Exception e) => (e.HResult & 0xFFFF) == PrivilegeNotHeld;
}
