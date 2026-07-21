using HoloFan.Core;

namespace HoloFan.Tests;

public class ConversionValidatorTests
{
    private static ConversionOptions Valid() => new()
    {
        SourceWidth = 1280,
        SourceHeight = 720,
        CropX = 280,
        CropY = 0,
        CropSide = 720,
        OutputSize = 512,
        Fps = 30,
        Speed = 1.0,
    };

    [Fact]
    public void Accepts_a_sensible_configuration()
    {
        var result = ConversionValidator.Validate(Valid());
        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Rejects_crop_beyond_source_width()
    {
        var result = ConversionValidator.Validate(Valid() with { CropX = 700, CropSide = 720 });
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("width", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Rejects_odd_output_size()
    {
        var result = ConversionValidator.Validate(Valid() with { OutputSize = 511 });
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("even"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(200)]
    public void Rejects_fps_out_of_range(int fps)
    {
        Assert.False(ConversionValidator.Validate(Valid() with { Fps = fps }).IsValid);
    }

    [Theory]
    [InlineData(0.05)]
    [InlineData(9.0)]
    public void Rejects_speed_out_of_range(double speed)
    {
        Assert.False(ConversionValidator.Validate(Valid() with { Speed = speed }).IsValid);
    }

    [Fact]
    public void Rejects_trim_end_before_start()
    {
        var result = ConversionValidator.Validate(Valid() with { TrimStart = 5, TrimEnd = 3 });
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Trim end"));
    }

    [Fact]
    public void Rejects_out_of_range_brightness()
    {
        Assert.False(ConversionValidator.Validate(Valid() with { Brightness = 2 }).IsValid);
    }
}
