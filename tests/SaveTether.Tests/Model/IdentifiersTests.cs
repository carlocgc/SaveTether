using SaveTether.Core.Model;

namespace SaveTether.Tests.Model;

public class IdentifiersTests
{
    [Theory]
    [InlineData("ppsspp.savedata")]
    [InlineData("eden.nand-system")]
    [InlineData("final-fantasy-ix.saves")]
    [InlineData("app2.role_1")]
    [InlineData("a.b.c")]
    public void Valid_mapping_ids_are_accepted(string id)
    {
        Assert.True(Identifiers.IsValidMappingId(id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nodot")]
    [InlineData("PPSSPP.savedata")]
    [InlineData("ppsspp.")]
    [InlineData(".savedata")]
    [InlineData("ppsspp..savedata")]
    [InlineData("../escape")]
    [InlineData("a/b.c")]
    [InlineData("a\\b.c")]
    [InlineData("a b.c")]
    [InlineData("-a.b")]
    [InlineData("a.b:c")]
    public void Invalid_mapping_ids_are_rejected(string? id)
    {
        Assert.False(Identifiers.IsValidMappingId(id));
    }

    [Fact]
    public void Very_long_mapping_ids_are_rejected()
    {
        Assert.False(Identifiers.IsValidMappingId(new string('a', 99) + ".b"));
    }
}

public class TargetAndDeviceNameTests
{
    [Theory]
    [InlineData("ppsspp.savedata", "ppsspp/SAVEDATA")]
    [InlineData("ppsspp.states", "ppsspp/PPSSPP_STATE")]
    [InlineData("vn-dohna.saves", "vn-dohna/Save Data")]
    [InlineData("eden.save", "eden/セーブ")]
    public void Valid_targets_are_accepted(string id, string target)
    {
        Assert.Null(Identifiers.DescribeTargetProblem(id, target));
    }

    [Theory]
    [InlineData("ppsspp.savedata", null)]
    [InlineData("ppsspp.savedata", "")]
    [InlineData("ppsspp.savedata", "SAVEDATA")]
    [InlineData("ppsspp.savedata", "ppsspp/PSP/SAVEDATA")]
    [InlineData("ppsspp.savedata", "ppsspp/")]
    [InlineData("ppsspp.savedata", "/SAVEDATA")]
    [InlineData("ppsspp.savedata", "eden/SAVEDATA")]
    [InlineData("ppsspp.savedata", "PPSSPP/SAVEDATA")]
    [InlineData("ppsspp.savedata", @"ppsspp\SAVEDATA")]
    [InlineData("ppsspp.savedata", "ppsspp/..")]
    [InlineData("ppsspp.savedata", "ppsspp/.")]
    [InlineData("ppsspp.savedata", "ppsspp/.savetether")]
    [InlineData("ppsspp.savedata", "ppsspp/.hidden")]
    [InlineData("ppsspp.savedata", "ppsspp/saves.")]
    [InlineData("ppsspp.savedata", "ppsspp/saves ")]
    [InlineData("ppsspp.savedata", "ppsspp/sa:ves")]
    [InlineData("ppsspp.savedata", "ppsspp/sa*ves")]
    [InlineData("ppsspp.savedata", "ppsspp/sa\tves")]
    [InlineData("ppsspp.savedata", "ppsspp/NUL")]
    [InlineData("ppsspp.savedata", "ppsspp/com1.txt")]
    [InlineData("ppsspp.savedata", "C:/SAVEDATA")]
    public void Invalid_targets_are_rejected_with_an_explanation(string id, string? target)
    {
        var problem = Identifiers.DescribeTargetProblem(id, target);
        Assert.False(string.IsNullOrWhiteSpace(problem));
    }

    [Theory]
    [InlineData("deck")]
    [InlineData("desktop-pc")]
    [InlineData("steam_frame2")]
    public void Valid_device_names_are_accepted(string name)
    {
        Assert.True(Identifiers.IsValidDeviceName(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Deck")]
    [InlineData("my deck")]
    [InlineData("deck.local")]
    [InlineData("../deck")]
    [InlineData("-deck")]
    [InlineData("nul")]
    [InlineData("com1")]
    [InlineData("a-name-that-is-much-too-long-to-be-a-device-name")]
    public void Invalid_device_names_are_rejected(string? name)
    {
        Assert.False(Identifiers.IsValidDeviceName(name));
    }

    [Fact]
    public void AppOf_is_the_part_before_the_first_dot()
    {
        Assert.Equal("ppsspp", Identifiers.AppOf("ppsspp.savedata"));
        Assert.Equal("a", Identifiers.AppOf("a.b.c"));
    }
}
