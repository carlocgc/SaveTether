using SaveTether.Core.FileSystem;

namespace SaveTether.Tests.FileSystem;

public class SymlinkCapabilityTests
{
    [Fact]
    public void Error_1314_is_told_from_other_errors()
    {
        Assert.True(SymlinkCapability.IsPrivilegeNotHeld(new IOException("no", unchecked((int)0x80070522))));
        Assert.False(SymlinkCapability.IsPrivilegeNotHeld(new IOException("no", unchecked((int)0x80070005))));
        Assert.False(SymlinkCapability.IsPrivilegeNotHeld(new SafeFsException("no")));
    }

    [Fact]
    public void Policy_off_refuses_before_any_link_is_tried()
    {
        using var sandbox = new Sandbox();
        var scratch = sandbox.At("scratch");

        var answer = SymlinkCapability.Probe(scratch, @"\\server\share", () => false);

        Assert.Equal(OperatingSystem.IsWindows() ? ShareLinkSupport.PolicyOff : ShareLinkSupport.Available, answer);
        Assert.False(Directory.Exists(scratch));
    }

    [Fact]
    public void The_probe_leaves_nothing_behind_whatever_it_answers()
    {
        using var sandbox = new Sandbox();
        var scratch = sandbox.Dir("scratch");

        var answer = SymlinkCapability.Probe(scratch, @"\\server\share", () => true);

        Assert.True(answer is ShareLinkSupport.Available or ShareLinkSupport.NeedsDevModeOrElevation);
        Assert.Empty(Directory.EnumerateFileSystemEntries(scratch));
    }

    [Fact]
    public void A_local_target_is_still_a_junction_and_a_share_gets_a_symlink()
    {
        using var sandbox = new Sandbox();
        var target = sandbox.Dir("target");
        sandbox.FileAt("save", "target", "slot1.sav");
        var fs = new SafeFs();

        fs.CreateDirectoryLink(sandbox.At("local-link"), target);
        Assert.Equal(OperatingSystem.IsWindows() ? EntryKind.Junction : EntryKind.Symlink, SafeFs.Probe(sandbox.At("local-link")));

        var link = sandbox.At("share-link");
        if (NetworkDriveTests.LocalAdminShare(target) is not { } share)
        {
            // Without Developer Mode or elevation the typed refusal comes back and nothing is left.
            var unc = $@"\\localhost\{target[0]}${target[2..]}";
            if (OperatingSystem.IsWindows() && Directory.Exists(unc))
            {
                Assert.Throws<SymlinkPrivilegeException>(() => fs.CreateDirectoryLink(link, unc));
                Assert.Equal(EntryKind.Missing, SafeFs.Probe(link));
            }

            return;
        }

        fs.CreateDirectoryLink(link, share);

        Assert.Equal(EntryKind.Symlink, SafeFs.Probe(link));
        Assert.True(PathRules.AreSame(share, SafeFs.ReadLinkTarget(link)));
        Assert.Equal("save", File.ReadAllText(Path.Combine(link, "slot1.sav")));
        fs.RemoveLink(link);
        sandbox.AssertCanaryIntact();
    }
}
