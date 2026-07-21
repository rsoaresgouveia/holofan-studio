using HoloFan.Core;

namespace HoloFan.Tests;

public class RgbColorTests
{
    [Theory]
    [InlineData("#ff8800", 255, 136, 0)]
    [InlineData("00ff00", 0, 255, 0)]
    [InlineData("#abc", 170, 187, 204)]
    public void Parses_hex_forms(string hex, int r, int g, int b)
    {
        var c = RgbColor.Parse(hex);
        Assert.Equal((r, g, b), (c.R, c.G, c.B));
    }

    [Fact]
    public void Empty_input_is_black()
    {
        Assert.Equal(RgbColor.Black, RgbColor.Parse(""));
        Assert.Equal(RgbColor.Black, RgbColor.Parse(null));
    }

    [Fact]
    public void Invalid_hex_throws()
    {
        Assert.Throws<FormatException>(() => RgbColor.Parse("nothex"));
    }

    [Fact]
    public void Round_trips_to_hex()
    {
        Assert.Equal("#ff8800", new RgbColor(255, 136, 0).ToHex());
    }
}

public class FanPresetTests
{
    [Fact]
    public void Catalog_is_not_empty_and_ids_are_unique()
    {
        Assert.NotEmpty(FanPreset.Catalog);
        var ids = FanPreset.Catalog.Select(p => p.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void Presets_use_even_resolutions()
    {
        Assert.All(FanPreset.Catalog, p => Assert.True(p.Resolution % 2 == 0, $"{p.Id} is odd"));
    }

    [Fact]
    public void FindById_is_case_insensitive()
    {
        Assert.NotNull(FanPreset.FindById("FAN-50"));
        Assert.Null(FanPreset.FindById("nope"));
    }
}
