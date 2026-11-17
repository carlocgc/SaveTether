using System.Xml.Linq;
using SaveTether.Cli;
using SaveTether.Core.Scheduling;
using SaveTether.Tests.FileSystem;

namespace SaveTether.Tests.Cli;

/// <summary>"schedule" (plan P2.7): the generated files, compared with the expected text, and installing and removing them.</summary>
public class ScheduleTests
{
    private const string Exe = "/home/deck/.local/bin/savetether";

    // ---- The generated systemd units ----

    [Fact]
    public void The_systemd_units_are_exactly_these()
    {
        var files = Schedule.SystemdUnits(new ScheduleRequest(Exe, Snapshots: true, Keep: 5, SaveTetherHome: null), "/units");

        Assert.Equal(
            ["savetether-status.service", "savetether-status.timer", "savetether-snapshot.service", "savetether-snapshot.timer"],
            files.Select(f => Path.GetFileName(f.Path)));
        Assert.All(files, f => Assert.Equal(Path.Combine("/units", Path.GetFileName(f.Path)), f.Path));

        Assert.Equal(
            """
            # Written by 'savetether schedule install'. Remove with 'savetether schedule remove'.
            [Unit]
            Description=SaveTether: check the save links after login

            [Service]
            Type=oneshot
            ExecStart=/home/deck/.local/bin/savetether status --notify --log

            """.ReplaceLineEndings("\n"),
            files[0].Content);
        Assert.Equal(
            """
            # Written by 'savetether schedule install'. Remove with 'savetether schedule remove'.
            [Unit]
            Description=SaveTether: check the save links after login

            [Timer]
            OnStartupSec=2min

            [Install]
            WantedBy=timers.target

            """.ReplaceLineEndings("\n"),
            files[1].Content);
        Assert.Equal(
            """
            # Written by 'savetether schedule install'. Remove with 'savetether schedule remove'.
            [Unit]
            Description=SaveTether: daily snapshot of the shared save folders

            [Service]
            Type=oneshot
            ExecStart=/home/deck/.local/bin/savetether snapshot --keep 5 --notify --log

            """.ReplaceLineEndings("\n"),
            files[2].Content);
        Assert.Equal(
            """
            # Written by 'savetether schedule install'. Remove with 'savetether schedule remove'.
            [Unit]
            Description=SaveTether: daily snapshot of the shared save folders

            [Timer]
            OnCalendar=daily
            Persistent=true
            RandomizedDelaySec=15min

            [Install]
            WantedBy=timers.target

            """.ReplaceLineEndings("\n"),
            files[3].Content);
    }

    [Fact]
    public void Paths_with_spaces_and_percent_signs_are_quoted_for_systemd_and_the_home_override_is_passed_on()
    {
        var files = Schedule.SystemdUnits(new ScheduleRequest("/home/deck/My Tools/save%tether", false, 3, "/home/deck/st home"), "/units");

        var service = files[0].Content;
        Assert.Contains("Environment=\"SAVETETHER_HOME=/home/deck/st home\"\n", service, StringComparison.Ordinal);
        Assert.Contains("ExecStart=\"/home/deck/My Tools/save%%tether\" status --notify --log\n", service, StringComparison.Ordinal);
        Assert.Equal(2, files.Count);
    }

    [Fact]
    public void The_unit_folder_follows_xdg_config_home()
    {
        Assert.Equal(Path.Combine("/home/deck", ".config", "systemd", "user"), Schedule.UnitFolder("/home/deck", _ => null));
        Assert.Equal(Path.Combine("/cfg", "systemd", "user"), Schedule.UnitFolder("/home/deck", n => n == "XDG_CONFIG_HOME" ? "/cfg" : null));
    }

    // ---- The generated Windows tasks ----

    private static readonly XNamespace Task = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    [Fact]
    public void The_windows_login_task_runs_status_headless_for_this_user_only()
    {
        var xml = Schedule.WindowsTask(new ScheduleRequest(@"C:\Program Files\SaveTether\savetether.exe", false, 5, null), Schedule.StatusJob, @"PC\owner");

        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n<Task version=\"1.2\"", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", xml.Replace("\r\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        var task = XDocument.Parse(xml).Root!;
        var logon = task.Element(Task + "Triggers")!.Element(Task + "LogonTrigger")!;
        Assert.Equal(@"PC\owner", logon.Element(Task + "UserId")!.Value);
        Assert.Equal("PT2M", logon.Element(Task + "Delay")!.Value);

        var principal = task.Element(Task + "Principals")!.Element(Task + "Principal")!;
        Assert.Equal("InteractiveToken", principal.Element(Task + "LogonType")!.Value);
        Assert.Equal("LeastPrivilege", principal.Element(Task + "RunLevel")!.Value);

        var exec = task.Element(Task + "Actions")!.Element(Task + "Exec")!;
        Assert.Equal("conhost.exe", exec.Element(Task + "Command")!.Value);
        Assert.Equal("--headless \"C:\\Program Files\\SaveTether\\savetether.exe\" status --notify --log", exec.Element(Task + "Arguments")!.Value);
    }

    [Fact]
    public void The_windows_snapshot_task_runs_daily_and_catches_up_when_missed()
    {
        var xml = Schedule.WindowsTask(new ScheduleRequest(@"C:\Tools\savetether.exe", true, 7, null), Schedule.SnapshotJob, @"PC\owner");

        var task = XDocument.Parse(xml).Root!;
        var calendar = task.Element(Task + "Triggers")!.Element(Task + "CalendarTrigger")!;
        Assert.Equal("1", calendar.Element(Task + "ScheduleByDay")!.Element(Task + "DaysInterval")!.Value);
        Assert.Equal("true", task.Element(Task + "Settings")!.Element(Task + "StartWhenAvailable")!.Value);
        Assert.Equal(@"--headless C:\Tools\savetether.exe snapshot --keep 7 --notify --log", task.Element(Task + "Actions")!.Element(Task + "Exec")!.Element(Task + "Arguments")!.Value);
    }

    // ---- Installing and removing ----

    private static CliDevice SetUpDevice(Sandbox sandbox)
    {
        var deck = new CliDevice(sandbox, "deck");
        deck.SetUp(createRoot: true);
        return deck;
    }

    [Fact]
    public void Install_on_linux_writes_the_units_and_enables_the_timers()
    {
        using var sandbox = new Sandbox();
        var deck = SetUpDevice(sandbox);

        var run = deck.Ok("schedule", "install", "--snapshots", "--keep", "3");

        Assert.Equal(4, Directory.GetFiles(deck.UnitFolder).Length);
        Assert.Contains("--keep 3", File.ReadAllText(Path.Combine(deck.UnitFolder, "savetether-snapshot.service")), StringComparison.Ordinal);
        Assert.Equal(
            [
                "systemctl --user daemon-reload",
                "systemctl --user enable --now savetether-status.timer",
                "systemctl --user enable --now savetether-snapshot.timer",
            ],
            deck.Commands.Calls);
        Assert.Contains("Scheduled: status, snapshot", run.Out, StringComparison.Ordinal);
        Assert.Contains("Game Mode", run.Out, StringComparison.Ordinal);
    }

    [Fact]
    public void Installing_again_without_snapshots_takes_the_snapshot_job_away()
    {
        using var sandbox = new Sandbox();
        var deck = SetUpDevice(sandbox);
        deck.Ok("schedule", "install", "--snapshots");
        deck.Commands.Calls.Clear();

        deck.Ok("schedule", "install");

        Assert.Equal(["savetether-status.service", "savetether-status.timer"], Directory.GetFiles(deck.UnitFolder).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Contains("systemctl --user disable --now savetether-snapshot.timer", deck.Commands.Calls);
    }

    [Fact]
    public void Remove_on_linux_disables_and_deletes_everything_and_show_says_so()
    {
        using var sandbox = new Sandbox();
        var deck = SetUpDevice(sandbox);
        deck.Ok("schedule", "install", "--snapshots");
        deck.Commands.Calls.Clear();

        deck.Ok("schedule", "remove");

        Assert.Empty(Directory.GetFiles(deck.UnitFolder));
        Assert.Contains("systemctl --user disable --now savetether-status.timer", deck.Commands.Calls);
        Assert.Contains("systemctl --user disable --now savetether-snapshot.timer", deck.Commands.Calls);
        Assert.Equal("systemctl --user daemon-reload", deck.Commands.Calls[^1]);
        Assert.Contains("Nothing is scheduled.", deck.Ok("schedule", "show").Out, StringComparison.Ordinal);
        Assert.Contains("Nothing was scheduled.", deck.Ok("schedule", "remove").Out, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failing_systemctl_is_reported()
    {
        using var sandbox = new Sandbox();
        var deck = SetUpDevice(sandbox);
        deck.Commands.Answer = line => line.Contains("enable", StringComparison.Ordinal) ? (1, "Failed to connect to bus") : (0, string.Empty);

        var run = deck.Expect(CliApp.ExitRefused, "schedule", "install");

        Assert.Contains("Failed to connect to bus", run.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void Install_on_windows_creates_the_tasks_with_schtasks()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc") { ScheduleAsWindows = true, Executable = @"C:\Tools\savetether.exe" };
        pc.SetUp(createRoot: true);

        pc.Ok("schedule", "install", "--snapshots");

        Assert.Contains(pc.Commands.Calls, c => c.StartsWith(@"schtasks.exe /Create /TN \SaveTether\Status /XML ", StringComparison.Ordinal) && c.EndsWith(" /F", StringComparison.Ordinal));
        Assert.Contains(pc.Commands.Calls, c => c.StartsWith(@"schtasks.exe /Create /TN \SaveTether\Snapshot /XML ", StringComparison.Ordinal));
        Assert.DoesNotContain(Directory.GetFiles(Path.GetTempPath(), "savetether-*-*.xml"), f => File.GetCreationTimeUtc(f) > DateTime.UtcNow.AddMinutes(-1));

        pc.Commands.Calls.Clear();
        pc.Ok("schedule", "remove");
        Assert.Contains(@"schtasks.exe /Delete /TN \SaveTether\Status /F", pc.Commands.Calls);
    }

    [Fact]
    public void Install_refuses_from_anything_but_the_savetether_binary_and_on_a_device_not_set_up()
    {
        using var sandbox = new Sandbox();
        var dotnet = new CliDevice(sandbox, "pc") { Executable = "/usr/share/dotnet/dotnet" };
        dotnet.SetUp(createRoot: true);
        Assert.Contains("installed savetether binary", dotnet.Expect(CliApp.ExitRefused, "schedule", "install").Err, StringComparison.Ordinal);

        var fresh = new CliDevice(sandbox, "fresh");
        fresh.Expect(CliApp.ExitRefused, "schedule", "install");
        Assert.Empty(fresh.Commands.Calls);
    }
}
