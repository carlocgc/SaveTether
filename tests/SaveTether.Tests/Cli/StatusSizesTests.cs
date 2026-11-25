using System.Text.Json;
using SaveTether.Tests.FileSystem;

namespace SaveTether.Tests.Cli;

public class StatusSizesTests
{
    private static JsonElement Mapping(CliDevice device, params string[] extra) =>
        device.Ok(["status", .. extra, "--json"]).Json().GetProperty("status").GetProperty("mappings")[0];

    [Fact]
    public void Sizes_are_given_only_when_asked_and_only_for_a_shared_copy_that_is_there()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");
        pc.SetUp(createRoot: true);
        pc.Save("GAME1/save.bin", "four");
        pc.Save("GAME2/save.bin", "sixsix");
        pc.Ok("apply", CliDevice.MappingId, "--copy");

        var sized = Mapping(pc, "--sizes");
        Assert.Equal(10, sized.GetProperty("bytes").GetInt64());
        Assert.Equal(2, sized.GetProperty("files").GetInt32());
        Assert.Contains("2 file(s)", pc.Ok("status", "--sizes").Out, StringComparison.Ordinal);

        var plain = Mapping(pc);
        Assert.False(plain.TryGetProperty("bytes", out _));
        Assert.False(plain.TryGetProperty("files", out _));

        Directory.Delete(pc.Target, recursive: true);
        var missing = Mapping(pc, "--sizes");
        Assert.Equal("broken", missing.GetProperty("state").GetString());
        Assert.False(missing.TryGetProperty("bytes", out _));
        Assert.False(missing.TryGetProperty("files", out _));
        sandbox.AssertCanaryIntact();
    }
}
