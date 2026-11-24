using SaveTether.Core.FileSystem;

namespace SaveTether.Tests.FileSystem;

/// <summary>Network kinds, mapped letters and the symlink policy. The Windows lookups are replaced, so no test reaches for a network.</summary>
public class NetworkPathsTests
{
    private static string? Mapped(string letter) => letter == "Z:" ? @"\\mint\sttest" : null;

    private static string? Subst(string letter) => letter == "S:" ? @"\\mint\sttest\sub" : null;

    [Fact]
    public void Kinds_come_from_the_path_text_and_the_lookups()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(NetworkKind.Unc, NetworkPaths.Kind(@"\\mint\sttest\sync", Mapped, Subst));
        Assert.Equal(NetworkKind.Unc, NetworkPaths.Kind(@"\\?\UNC\mint\sttest", Mapped, Subst));
        Assert.Equal(NetworkKind.MappedDrive, NetworkPaths.Kind(@"z:\sync", Mapped, Subst));
        Assert.Equal(NetworkKind.Subst, NetworkPaths.Kind(@"S:\", Mapped, Subst));
        Assert.Equal(NetworkKind.None, NetworkPaths.Kind(@"C:\Sync", Mapped, Subst));
        Assert.Equal(NetworkKind.None, NetworkPaths.Kind(@"\\?\C:\Sync", Mapped, Subst));
        Assert.Equal(NetworkKind.None, NetworkPaths.Kind(@"\\.\C:\Sync", Mapped, Subst));
    }

    [Fact]
    public void A_mapped_or_subst_letter_becomes_its_share_and_other_paths_stay()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(@"\\mint\sttest\sync", NetworkPaths.ToUncIfMapped(@"Z:\sync", Mapped, Subst));
        Assert.Equal(@"\\mint\sttest", NetworkPaths.ToUncIfMapped("Z:", Mapped, Subst));
        Assert.Equal(@"\\mint\sttest\sub\x", NetworkPaths.ToUncIfMapped(@"S:\x", Mapped, Subst));
        Assert.Equal(@"C:\Sync", NetworkPaths.ToUncIfMapped(@"C:\Sync", Mapped, Subst));
        Assert.Equal(@"\\nas\x", NetworkPaths.ToUncIfMapped(@"\\nas\x", Mapped, Subst));
    }

    [Fact]
    public void Off_windows_nothing_is_a_network_path()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(NetworkKind.None, PathRules.NetworkKind("/mnt/nas/Sync"));
        Assert.Equal("/mnt/nas/Sync", PathRules.ToUncIfMapped("/mnt/nas/Sync"));
        Assert.True(SymlinkPolicy.LocalToRemoteAllowed());
    }

    [Fact]
    public void A_path_that_does_not_answer_in_time_is_unreachable()
    {
        using var release = new ManualResetEventSlim();

        Assert.False(NetworkPaths.IsReachable("x", TimeSpan.FromMilliseconds(50), _ =>
        {
            release.Wait(TimeSpan.FromSeconds(10));
            return true;
        }));
        release.Set();

        Assert.True(NetworkPaths.IsReachable("x", exists: _ => true));
        Assert.False(NetworkPaths.IsReachable("x", exists: _ => false));
        Assert.False(NetworkPaths.IsReachable("x", exists: _ => throw new IOException("gone")));
    }

    [Fact]
    public void The_symlink_policy_is_on_unless_the_value_is_zero()
    {
        Assert.True(SymlinkPolicy.Allowed(null));
        Assert.True(SymlinkPolicy.Allowed(1));
        Assert.False(SymlinkPolicy.Allowed(0));
    }
}
