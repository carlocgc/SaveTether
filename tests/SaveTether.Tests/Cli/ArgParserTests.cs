using SaveTether.Cli;

namespace SaveTether.Tests.Cli;

public class ArgParserTests
{
    [Fact]
    public void Every_command_accepts_every_one_of_its_flags_and_options()
    {
        foreach (var spec in ArgParser.Commands)
        {
            var args = new List<string>(spec.Name.Split(' '));
            args.AddRange(Enumerable.Range(0, spec.MaxPositionals).Select(i => $"positional{i}"));
            args.AddRange(spec.Flags);
            args.AddRange(spec.Options.SelectMany(o => new[] { o, "value" }));
            args.AddRange(spec.Repeatable.SelectMany(o => new[] { o, "one", o, "two" }));
            args.Add("--json");

            var parsed = ArgParser.Parse(args);

            Assert.Equal(spec.Name, parsed.Name);
            Assert.Equal(spec.MaxPositionals, parsed.Positionals.Count);
            Assert.All(spec.Flags, flag => Assert.True(parsed.Has(flag), $"{spec.Name} {flag}"));
            Assert.All(spec.Options, option => Assert.Equal("value", parsed.Value(option)));
            Assert.All(spec.Repeatable, option => Assert.Equal(["one", "two"], parsed.Values(option)));
            Assert.True(parsed.Has(ArgParser.Json));
        }
    }

    [Fact]
    public void The_command_table_is_what_the_design_lists()
    {
        Assert.Equal(
            ["init", "map add", "map remove", "status", "apply", "revert", "backup list", "backup restore", "recover"],
            ArgParser.Commands.Select(c => c.Name));
    }

    [Fact]
    public void The_help_text_names_every_command_flag_and_option()
    {
        foreach (var spec in ArgParser.Commands)
        {
            Assert.Contains("savetether " + spec.Name, CliApp.Usage, StringComparison.Ordinal);
            Assert.All(
                spec.Flags.Concat(spec.Options).Concat(spec.Repeatable),
                option => Assert.Contains(option, CliApp.Usage, StringComparison.Ordinal));
        }

        Assert.Contains("--json", CliApp.Usage, StringComparison.Ordinal);
        Assert.Contains("Exit codes", CliApp.Usage, StringComparison.Ordinal);
    }

    [Fact]
    public void Options_may_come_before_or_after_positionals_and_use_either_value_form()
    {
        var parsed = ArgParser.Parse(
            ["map", "add", "--source=/saves/psp", "ppsspp.savedata", "--target", "ppsspp/SAVEDATA", "--process", "a", "--process=b"]);

        Assert.Equal("map add", parsed.Name);
        Assert.Equal(["ppsspp.savedata"], parsed.Positionals);
        Assert.Equal("/saves/psp", parsed.Value("--source"));
        Assert.Equal("ppsspp/SAVEDATA", parsed.Value("--target"));
        Assert.Equal(["a", "b"], parsed.Values("--process"));
        Assert.Null(parsed.Value("--backup-root"));
        Assert.Empty(parsed.Values("--nothing"));
    }

    [Fact]
    public void A_value_may_contain_an_equals_sign_or_start_with_a_dash()
    {
        var parsed = ArgParser.Parse(["init", "--sync-root=/mnt/a=b", "--device", "-odd"]);

        Assert.Equal("/mnt/a=b", parsed.Value("--sync-root"));
        Assert.Equal("-odd", parsed.Value("--device"));
    }

    [Fact]
    public void Everything_after_a_double_dash_is_a_positional()
    {
        var parsed = ArgParser.Parse(["revert", "--assume-closed", "--", "--dry-run"]);

        Assert.Equal(["--dry-run"], parsed.Positionals);
        Assert.True(parsed.Has("--assume-closed"));
        Assert.False(parsed.Has("--dry-run"));
    }

    [Fact]
    public void Help_is_accepted_on_an_incomplete_command_line()
    {
        Assert.True(ArgParser.Parse(["revert", "--help"]).Has(ArgParser.Help));
    }

    [Theory]
    [InlineData("unknown command or option 'frobnicate'", "frobnicate")]
    [InlineData("unknown command or option '--bogus'", "--bogus")]
    [InlineData("unknown command or option '--version'", "--version", "extra")]
    [InlineData("'map' needs a subcommand: add, remove", "map")]
    [InlineData("'map' needs a subcommand: add, remove", "map", "--json")]
    [InlineData("unknown command 'map rename'; 'map' takes: add, remove", "map", "rename")]
    [InlineData("'backup' needs a subcommand: list, restore", "backup")]
    [InlineData("unknown option '--force' for 'apply'", "apply", "x.y", "--force")]
    [InlineData("unknown option '--copy' for 'revert'", "revert", "x.y", "--copy")]
    [InlineData("unknown option '-f' for 'apply'", "apply", "-f")]
    [InlineData("option '--sync-root' needs a value", "init", "--sync-root")]
    [InlineData("option '--sync-root' needs a value", "init", "--sync-root", "--", "x")]
    [InlineData("option '--device' needs a value that is not empty", "init", "--device=")]
    [InlineData("option '--device' was given more than once", "init", "--device", "a", "--device", "b")]
    [InlineData("option '--copy' does not take a value", "apply", "x.y", "--copy=yes")]
    [InlineData("option '--json' does not take a value", "status", "--json=1")]
    [InlineData("'revert' needs an argument", "revert")]
    [InlineData("'map add' needs an argument", "map", "add", "--source", "/x")]
    [InlineData("unexpected argument 'b.c' for 'revert'", "revert", "a.b", "b.c")]
    [InlineData("unexpected argument 'now' for 'recover'", "recover", "now")]
    public void Malformed_input_is_refused_with_a_message_that_names_the_problem(string message, params string[] args)
    {
        var error = Assert.Throws<UsageException>(() => ArgParser.Parse(args));

        Assert.Equal(message, error.Message);
    }

    [Fact]
    public void No_arguments_at_all_is_a_usage_error_for_the_parser()
    {
        Assert.Throws<UsageException>(() => ArgParser.Parse([]));
    }
}
