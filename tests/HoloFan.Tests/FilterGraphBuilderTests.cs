using HoloFan.Core;

namespace HoloFan.Tests;

public class FilterGraphBuilderTests
{
    private static ConversionOptions Base() => new()
    {
        SourceWidth = 1920,
        SourceHeight = 1080,
        CropX = 420,
        CropY = 0,
        CropSide = 1080,
        OutputSize = 512,
        Fps = 30,
        Speed = 1.0,
        CircularMask = true,
        Background = RgbColor.Black,
    };

    [Fact]
    public void Builds_crop_scale_and_fps_in_order()
    {
        var vf = FilterGraphBuilder.BuildVideoFilter(Base());

        Assert.StartsWith("crop=1080:1080:420:0,scale=512:512:flags=lanczos", vf);
        Assert.Contains("fps=30", vf);
        Assert.EndsWith("format=yuv420p", vf);
    }

    [Fact]
    public void Omits_setpts_when_speed_is_one()
    {
        Assert.DoesNotContain("setpts", FilterGraphBuilder.BuildVideoFilter(Base()));
    }

    [Fact]
    public void Adds_setpts_for_non_unit_speed()
    {
        var vf = FilterGraphBuilder.BuildVideoFilter(Base() with { Speed = 2.5 });
        Assert.Contains("setpts=PTS/2.5", vf);
    }

    [Fact]
    public void Circular_mask_paints_corners_with_background_and_escapes_commas()
    {
        var vf = FilterGraphBuilder.BuildVideoFilter(Base() with { Background = new RgbColor(255, 0, 0) });

        Assert.Contains("format=rgba", vf);
        Assert.Contains("geq=", vf);
        // Outside the circle the red channel becomes 255, green/blue become 0.
        Assert.Contains("r(X\\,Y)\\,255)", vf);
        Assert.Contains("g(X\\,Y)\\,0)", vf);
        Assert.Contains("hypot(X-W/2\\,Y-H/2)", vf);
    }

    [Fact]
    public void No_mask_means_no_geq()
    {
        var vf = FilterGraphBuilder.BuildVideoFilter(Base() with { CircularMask = false });
        Assert.DoesNotContain("geq", vf);
        Assert.DoesNotContain("format=rgba", vf);
    }

    [Fact]
    public void Eq_only_added_when_colour_changes()
    {
        Assert.DoesNotContain(",eq=", FilterGraphBuilder.BuildVideoFilter(Base()));
        var vf = FilterGraphBuilder.BuildVideoFilter(Base() with { Brightness = 0.2, Contrast = 1.3, Saturation = 0.8 });
        Assert.Contains(",eq=brightness=0.2:contrast=1.3:saturation=0.8", vf);
    }

    [Fact]
    public void Arguments_include_codec_drop_audio_and_output()
    {
        var args = FilterGraphBuilder.BuildArguments("in.mp4", "out.mp4", Base());

        Assert.Equal("in.mp4", args[args.ToList().IndexOf("-i") + 1]);
        Assert.Contains("-an", args);
        Assert.Contains("libx264", args);
        Assert.Contains("yuv420p", args);
        Assert.Equal("out.mp4", args[^1]);
        Assert.Contains("-filter:v", args);
    }

    [Fact]
    public void Trim_adds_seek_and_duration()
    {
        var args = FilterGraphBuilder.BuildArguments("in.mp4", "out.mp4", Base() with { TrimStart = 2, TrimEnd = 5 });
        var list = args.ToList();

        var ss = list.IndexOf("-ss");
        Assert.True(ss >= 0);
        Assert.Equal("2", list[ss + 1]);
        var t = list.IndexOf("-t");
        Assert.True(t >= 0);
        Assert.Equal("3", list[t + 1]); // 5 - 2
    }

    [Fact]
    public void Uses_invariant_decimal_separator()
    {
        var vf = FilterGraphBuilder.BuildVideoFilter(Base() with { Speed = 1.5 });
        Assert.Contains("1.5", vf);
        Assert.DoesNotContain("1,5", vf);
    }
}
