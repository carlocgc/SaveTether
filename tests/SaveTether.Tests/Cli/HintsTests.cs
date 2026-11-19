using System.Text.RegularExpressions;
using SaveTether.Cli;
using SaveTether.Core.Model;
using SaveTether.Tests.FileSystem;

namespace SaveTether.Tests.Cli;

/// <summary>
/// The core says what can be done next without naming a command; the CLI words each next step as
/// the command to type (desktop-app-spec.md, section 13).
/// </summary>
public partial class HintsTests
{
    private const string Id = CliDevice.MappingId;

    public static TheoryData<NextStepKind> AllKinds() => [.. Enum.GetValues<NextStepKind>()];

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Every_next_step_has_a_command_line_hint(NextStepKind kind)
    {
        var hint = Hints.For(new NextStep(kind, Id, "backup-id", [SetupChoice.NoCopy, SetupChoice.AddMissing]));

        Assert.False(string.IsNullOrWhiteSpace(hint));
        Assert.EndsWith(".", hint, StringComparison.Ordinal);
    }

    [Fact]
    public void A_note_is_its_text_then_one_hint_per_next_step()
    {
        var note = new Note(
            "Something is wrong.",
            [new NextStep(NextStepKind.Revert, Id), new NextStep(NextStepKind.Recover)]);

        Assert.Equal(
            $"Something is wrong. Run 'savetether revert {Id}' first. Run 'savetether recover'.",
            Hints.Compose(note));
        Assert.Equal("Just text.", Hints.Compose(new Note("Just text.")));
    }

    [Fact]
    public void The_choice_hint_lists_only_the_choices_offered()
    {
        var firstDevice = Hints.For(new NextStep(NextStepKind.ChooseSetup, Id, Choices: [SetupChoice.Copy, SetupChoice.NoCopy]));
        Assert.Contains("(--copy)", firstDevice, StringComparison.Ordinal);
        Assert.Contains("(--no-copy)", firstDevice, StringComparison.Ordinal);
        Assert.DoesNotContain("--add-missing", firstDevice, StringComparison.Ordinal);

        var guard = Hints.For(new NextStep(
            NextStepKind.UseAllowedChoice, Id, Choices: [SetupChoice.NoCopy, SetupChoice.AddMissing, SetupChoice.ReplaceShared]));
        Assert.Equal("Use --no-copy, --add-missing, or --replace-shared --confirm-replace.", guard);
    }

    [Fact]
    public void Status_text_and_json_keep_the_command_to_run()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");
        pc.SetUp(createRoot: true);

        Assert.Contains($"Not applied on this device yet. Run 'savetether apply {Id}'.", pc.Ok("status").Out, StringComparison.Ordinal);
        var detail = pc.Ok("status", "--json").Json().GetProperty("status").GetProperty("mappings")[0].GetProperty("detail").GetString();
        Assert.Equal($"Not applied on this device yet. Run 'savetether apply {Id}'.", detail);
    }

    [Fact]
    public void A_store_refusal_keeps_the_command_to_run()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");
        pc.SetUp(createRoot: true);

        var refused = pc.Expect(CliApp.ExitRefused, "map", "add", "other.saves", "--source", Path.Combine(pc.Home, "other"));

        Assert.Contains("There is no mapping 'other.saves' yet", refused.Err, StringComparison.Ordinal);
        Assert.Contains("Give it with --target <app>/<save folder>, for example --target other/saves.", refused.Err, StringComparison.Ordinal);
    }

    // ---- The core names no command-line command or flag ----

    [GeneratedRegex(@"'savetether\s|(?<![\w-])--[a-z][a-z-]+")]
    private static partial Regex CliWording();

    [Fact]
    public void Core_messages_name_no_command_line_command_or_flag()
    {
        var repo = FindRepositoryRoot();
        var core = Path.Combine(repo, "src", "SaveTether.Core");
        var offences = new List<string>();
        // FlatpakSandbox runs the flatpak tool, whose options are its own, not SaveTether's. Autostart writes
        // the desktop app's own command line, with its --minimized, into the session's entry; it is no message.
        var sources = Directory.EnumerateFiles(core, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsBuildOutput(f) && Path.GetFileName(f) is not ("FlatpakSandbox.cs" or "Autostart.cs"))
            .ToList();
        Assert.NotEmpty(sources);
        foreach (var file in sources)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();

                // Comments may name commands. A message may tell the owner to run the flatpak tool itself.
                if (line.StartsWith("//", StringComparison.Ordinal) || line.Contains("'flatpak ", StringComparison.Ordinal))
                {
                    continue;
                }

                if (CliWording().IsMatch(line))
                {
                    offences.Add($"{Path.GetRelativePath(repo, file)}:{i + 1}: {line}");
                }
            }
        }

        Assert.True(
            offences.Count == 0,
            "Core messages must not name CLI commands or flags; give the message a NextStep and word it in the CLI's Hints. Found:\n" +
            string.Join("\n", offences));
    }

    [Theory]
    [InlineData("\"Run 'savetether recover' first.\"")]
    [InlineData("\"Add --confirm-replace to go ahead.\"")]
    [InlineData("$\"choose one with --install <id>\"")]
    public void The_wording_audit_catches_commands_and_flags(string line) => Assert.Matches(CliWording(), line);

    [Theory]
    [InlineData("\"Recover it first.\"")]
    [InlineData("\"a well-known path\"")]
    [InlineData("var count = i--;")]
    public void The_wording_audit_allows_plain_words(string line) => Assert.DoesNotMatch(CliWording(), line);

    private static bool IsBuildOutput(string file)
    {
        var separator = Path.DirectorySeparatorChar;
        return file.Contains($"{separator}obj{separator}", StringComparison.Ordinal)
            || file.Contains($"{separator}bin{separator}", StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SaveTether.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not find SaveTether.slnx above the test folder.");
    }
}
