using System.Text.RegularExpressions;
using SaveTether.Cli;
using SaveTether.Core;

namespace SaveTether.Tests.Cli;

public class CliAppTests
{
    [Fact]
    public void Version_prints_name_and_semver_and_succeeds()
    {
        var (exit, stdout, stderr) = Run("--version");

        Assert.Equal(CliApp.ExitSuccess, exit);
        Assert.Matches(new Regex(@"^savetether \d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?\r?\n$"), stdout);
        Assert.Empty(stderr);
    }

    [Fact]
    public void Version_has_no_build_metadata_suffix()
    {
        Assert.DoesNotContain('+', ProductInfo.Version);
    }

    [Theory]
    [InlineData]
    [InlineData("--help")]
    [InlineData("-h")]
    public void Help_prints_usage_and_succeeds(params string[] args)
    {
        var (exit, stdout, stderr) = Run(args);

        Assert.Equal(CliApp.ExitSuccess, exit);
        Assert.Contains("Usage:", stdout, StringComparison.Ordinal);
        Assert.Empty(stderr);
    }

    [Theory]
    [InlineData("frobnicate")]
    [InlineData("--bogus")]
    [InlineData("--version", "extra")]
    public void Unknown_input_fails_on_stderr_only(params string[] args)
    {
        var (exit, stdout, stderr) = Run(args);

        Assert.Equal(CliApp.ExitUsage, exit);
        Assert.Empty(stdout);
        Assert.Contains("unknown command or option", stderr, StringComparison.Ordinal);
    }

    private static (int Exit, string Stdout, string Stderr) Run(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = CliApp.Run(args, stdout, stderr);
        return (exit, stdout.ToString(), stderr.ToString());
    }
}

[Collection("self-test")]
public class CliSelfTestTests
{
    [Fact]
    public void Self_test_flag_runs_and_succeeds()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exit = CliApp.Run(["--self-test"], stdout, stderr);

        Assert.Equal(CliApp.ExitSuccess, exit);
        Assert.Contains("self-test passed", stdout.ToString(), StringComparison.Ordinal);
        Assert.Empty(stderr.ToString());
    }
}
