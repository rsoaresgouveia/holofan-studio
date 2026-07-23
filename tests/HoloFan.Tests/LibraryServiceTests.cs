using HoloFan.Device;
using HoloFan.Web.Services;
using Microsoft.Extensions.Options;

namespace HoloFan.Tests;

public class LibraryServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "holofan-lib-" + Guid.NewGuid().ToString("N"));
    private static readonly int FrameBytes = DeviceModel.Fan42F2.FrameBytes;

    private LibraryService NewService()
        => new(Options.Create(new FfmpegOptions { DataDirectory = _dir }));

    private static byte[] Frames(int n) => new byte[FrameBytes * n];

    [Fact]
    public void Starts_empty()
        => Assert.Empty(NewService().List());

    [Fact]
    public void Adds_a_valid_clip_with_the_right_frame_count()
    {
        var s = NewService();
        var clip = s.Add("My Clip", Frames(3), "generated");

        Assert.Equal("My Clip", clip.Name);
        Assert.Equal(3, clip.FrameCount);
        Assert.Equal("generated", clip.Source);
        Assert.Single(s.List());
        Assert.NotNull(s.PathOf(clip.Id));
    }

    [Fact]
    public void Accepts_the_optional_20_byte_trailer()
    {
        var bytes = new byte[FrameBytes * 2 + 20];      // some encoders append a 20-byte trailer
        var clip = NewService().Add("Trailered", bytes, "imported");
        Assert.Equal(2, clip.FrameCount);
    }

    [Theory]
    [InlineData(1000)]                 // far too small
    [InlineData(FrameBytesPlusJunk)]   // not a whole number of frames
    public void Rejects_bytes_that_are_not_whole_frames(int length)
        => Assert.Throws<ArgumentException>(() => NewService().Add("bad", new byte[length], "imported"));

    private const int FrameBytesPlusJunk = 129024 + 7;

    [Fact]
    public void Rename_changes_the_name_and_persists()
    {
        var s = NewService();
        var clip = s.Add("old", Frames(1), "generated");

        Assert.True(s.Rename(clip.Id, "new name"));
        Assert.Equal("new name", NewService().Get(clip.Id)!.Name);   // fresh instance = read from disk
        Assert.False(s.Rename("no-such-id", "x"));
    }

    [Fact]
    public void Delete_removes_the_entry_and_its_file()
    {
        var s = NewService();
        var clip = s.Add("doomed", Frames(1), "generated");
        var path = s.PathOf(clip.Id)!;

        Assert.True(s.Delete(clip.Id));
        Assert.Empty(s.List());
        Assert.False(File.Exists(path));
        Assert.False(s.Delete(clip.Id));    // already gone
    }

    [Fact]
    public void Library_survives_a_restart()
    {
        var clip = NewService().Add("persist me", Frames(4), "imported");

        // A brand-new service instance (as if the app restarted) still sees it.
        var reloaded = NewService();
        var found = reloaded.Get(clip.Id);
        Assert.NotNull(found);
        Assert.Equal("persist me", found!.Name);
        Assert.Equal(4, found.FrameCount);
        Assert.NotNull(reloaded.PathOf(clip.Id));
    }

    [Fact]
    public void Blank_names_fall_back_and_long_names_are_bounded()
    {
        var s = NewService();
        Assert.Equal("clip", s.Add("   ", Frames(1), "generated").Name);
        Assert.True(s.Add(new string('x', 200), Frames(1), "generated").Name.Length <= 60);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
