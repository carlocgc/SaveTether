using SaveTether.Core.Startup;
using SaveTether.Gui.Presentation;
using SaveTether.Tests.FileSystem;

namespace SaveTether.Tests.Gui;

/// <summary>Section 11: the "Start with the session" switch, the rewrite at each start, and banner B5.</summary>
public class SessionStartTests
{
    [Fact]
    public void The_switch_writes_and_removes_the_entry_with_this_apps_path()
    {
        using var sandbox = new Sandbox();
        var store = new XdgAutostartStore(sandbox.At("config", "autostart"));
        var program = sandbox.At("bin", "savetether-gui");
        var start = new SessionStart(store, program);

        Assert.True(start.Available);
        Assert.False(start.IsOn);
        Assert.Null(start.Set(on: true));
        Assert.True(start.IsOn);
        Assert.Equal(program, store.ReadProgram());
        Assert.Null(start.Set(on: false));
        Assert.False(start.IsOn);

        var none = new SessionStart(null, program);
        Assert.False(none.Available);
        Assert.NotNull(none.Set(on: true));
    }

    [Fact]
    public void At_start_an_entry_naming_another_program_is_rewritten_and_none_is_never_created()
    {
        using var sandbox = new Sandbox();
        var store = new XdgAutostartStore(sandbox.At("config", "autostart"));
        var program = sandbox.At("bin", "savetether-gui");

        // Off: starting leaves it off.
        Assert.Null(new SessionStart(store, program).RefreshAtStart());
        Assert.Null(store.ReadProgram());

        // On, from where the app was before it moved: rewritten with where it is now.
        store.Write(sandbox.At("old", "savetether-gui"));
        Assert.StartsWith("autostart entry refreshed", new SessionStart(store, program).RefreshAtStart(), StringComparison.Ordinal);
        Assert.Equal(program, store.ReadProgram());

        // Already right: nothing to do.
        Assert.Null(new SessionStart(store, program).RefreshAtStart());

        // A copy with its own local folder (a test copy) leaves the entry where it is, and says so in the log.
        var testCopy = sandbox.At("test", "savetether-gui");
        Assert.StartsWith("autostart entry left at", new SessionStart(store, testCopy).RefreshAtStart(ownLocalFolder: true), StringComparison.Ordinal);
        Assert.Equal(program, store.ReadProgram());
    }

    [Fact]
    public void Banner_b5_names_a_missing_program_and_its_fix_rewrites_the_entry()
    {
        using var sandbox = new Sandbox();
        var store = new XdgAutostartStore(sandbox.At("config", "autostart"));
        var program = sandbox.At("bin", "savetether-gui");
        Directory.CreateDirectory(Path.GetDirectoryName(program)!);
        File.WriteAllText(program, "binary");
        var start = new SessionStart(store, program);

        Assert.Null(start.MissingBanner);
        var gone = sandbox.At("gone", "savetether-gui");
        store.Write(gone);
        Assert.Equal($"SaveTether is set to start with the session, but the entry points to {gone}, which is missing.", start.MissingBanner);

        Assert.Null(start.Set(on: true));
        Assert.Null(start.MissingBanner);
        Assert.Equal(program, store.ReadProgram());
    }
}
