using SaveTether.Cli;
using SaveTether.Core.FileSystem;
using SaveTether.Core.Startup;
using SaveTether.Tests.Cli;
using SaveTether.Tests.FileSystem;

namespace SaveTether.Tests.Startup;

/// <summary>Starting with the session (desktop-app-spec.md, section 11): the entries' text, the Linux store, and status's check.</summary>
public class AutostartTests
{
    [Fact]
    public void The_desktop_entry_and_the_run_value_are_the_expected_text()
    {
        Assert.Equal(
            "[Desktop Entry]\n" +
            "Type=Application\n" +
            "Name=SaveTether\n" +
            "Comment=Checks that game save folders stay linked to the synced folder\n" +
            "Exec=\"/home/deck/.local/bin/savetether-gui\" --minimized\n" +
            "Terminal=false\n" +
            "X-GNOME-Autostart-enabled=true\n",
            Autostart.DesktopEntry("/home/deck/.local/bin/savetether-gui"));
        Assert.Equal(
            "\"C:\\Users\\Owner\\AppData\\Local\\Programs\\SaveTether\\savetether-gui.exe\" --minimized",
            Autostart.RunValue(@"C:\Users\Owner\AppData\Local\Programs\SaveTether\savetether-gui.exe"));
    }

    [Theory]
    [InlineData("/home/deck/.local/bin/savetether-gui")]
    [InlineData("/home/deck/My Games/save tether/savetether-gui")]
    [InlineData("/opt/odd \"name\" $HOME `x` back\\slash/savetether-gui")]
    public void The_program_is_read_back_from_the_desktop_entry_it_was_written_into(string program)
    {
        Assert.Equal(program, Autostart.ProgramInDesktopEntry(Autostart.DesktopEntry(program)));
    }

    [Theory]
    [InlineData(@"C:\Users\Owner\AppData\Local\Programs\SaveTether\savetether-gui.exe")]
    [InlineData(@"D:\My Games\Save Tether\savetether-gui.exe")]
    public void The_program_is_read_back_from_the_run_value_it_was_written_into(string program)
    {
        // A Windows path cannot hold a double quote, so the quotes around it are enough.
        Assert.Equal(program, Autostart.ProgramInRunValue(Autostart.RunValue(program)));
    }

    [Fact]
    public void Entries_written_by_hand_are_read_too()
    {
        Assert.Equal("/usr/bin/savetether-gui", Autostart.ProgramInDesktopEntry("[Desktop Entry]\r\nExec=/usr/bin/savetether-gui --minimized\r\n"));
        Assert.Null(Autostart.ProgramInDesktopEntry("[Desktop Entry]\nName=Other\n"));
        Assert.Equal(@"C:\bin\savetether-gui.exe", Autostart.ProgramInRunValue(@"C:\bin\savetether-gui.exe --minimized"));
    }

    [Fact]
    public void The_linux_entry_is_written_read_and_removed_and_a_link_in_its_place_is_never_followed()
    {
        using var sandbox = new Sandbox();
        var folder = sandbox.At("config", "autostart");
        var store = new XdgAutostartStore(folder);
        Assert.Null(store.ReadProgram());
        store.Remove();

        var program = sandbox.At("bin", "savetether-gui");
        store.Write(program);
        Assert.Equal(program, store.ReadProgram());
        Assert.Equal(Autostart.DesktopEntry(program), File.ReadAllText(Path.Combine(folder, XdgAutostartStore.FileName)));

        // Written again with the current path, as the app does at each start.
        var moved = sandbox.At("elsewhere", "savetether-gui");
        store.Write(moved);
        Assert.Equal(moved, store.ReadProgram());

        store.Remove();
        Assert.Null(store.ReadProgram());
        Assert.Equal(EntryKind.Missing, SafeFs.Probe(store.Location));

        // Something SaveTether did not write, a link, is refused rather than deleted through.
        Sandbox.Link(store.Location, sandbox.Canary);
        Assert.Throws<SafeFsException>(store.Remove);
        sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void The_windows_value_is_written_read_and_removed()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // A key of the test's own, never the real Run key: a test must not change what starts with the session.
        var keyPath = $@"Software\SaveTether-tests\{Guid.NewGuid():N}";
        try
        {
            var store = new WindowsRunKeyStore(keyPath);
            Assert.Null(store.ReadProgram());
            store.Remove();

            store.Write(@"D:\My Games\SaveTether\savetether-gui.exe");
            Assert.Equal(@"D:\My Games\SaveTether\savetether-gui.exe", store.ReadProgram());
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(keyPath))
            {
                Assert.Equal("\"D:\\My Games\\SaveTether\\savetether-gui.exe\" --minimized", key!.GetValue(WindowsRunKeyStore.DefaultValueName));
            }

            store.Remove();
            Assert.Null(store.ReadProgram());
            Assert.EndsWith(@$"{keyPath}\SaveTether", store.Location, StringComparison.Ordinal);
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\SaveTether-tests", throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void A_missing_program_is_what_status_warns_about()
    {
        using var sandbox = new Sandbox();
        var present = sandbox.At("bin", "savetether-gui");
        CliDevice.Write(Path.GetDirectoryName(present)!, "savetether-gui", "binary");

        Assert.False(Autostart.Check(null).Enabled);
        Assert.Null(Autostart.Check(new Fixed(present)).MissingProgram);
        Assert.True(Autostart.Check(new Fixed(present)).Enabled);
        Assert.Equal("/gone/savetether-gui", Autostart.Check(new Fixed("/gone/savetether-gui")).MissingProgram);
    }

    [Fact]
    public void Status_warns_when_the_entry_points_to_a_missing_program_and_says_nothing_otherwise()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");
        pc.SetUp(createRoot: true);

        Assert.DoesNotContain("start with the session", pc.Ok("status").Err, StringComparison.Ordinal);
        Assert.False(pc.Ok("status", "--json").Json().GetProperty("status").TryGetProperty("autostart_missing", out _));

        pc.Autostart = new Fixed("/gone/savetether-gui");
        Assert.Contains("the entry points to '/gone/savetether-gui', which is missing", pc.Ok("status").Err, StringComparison.Ordinal);
        Assert.Equal("/gone/savetether-gui", pc.Ok("status", "--json").Json().GetProperty("status").GetProperty("autostart_missing").GetString());

        // Status only reads: the entry is left as it was.
        Assert.Equal("/gone/savetether-gui", pc.Autostart.ReadProgram());
        Assert.Equal(CliApp.ExitSuccess, pc.Run("status").Exit);
    }

    /// <summary>An entry that names a program, and refuses to change.</summary>
    internal sealed class Fixed(string program) : IAutostartStore
    {
        public string Location => "test entry";

        public string? ReadProgram() => program;

        public void Write(string program) => throw new InvalidOperationException("Status must not write the entry.");

        public void Remove() => throw new InvalidOperationException("Status must not remove the entry.");
    }
}
