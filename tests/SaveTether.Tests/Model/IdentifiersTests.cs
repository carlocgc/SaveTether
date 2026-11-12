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
