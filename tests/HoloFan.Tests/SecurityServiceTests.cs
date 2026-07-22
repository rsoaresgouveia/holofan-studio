using HoloFan.Web.Services;
using Microsoft.Extensions.Options;

namespace HoloFan.Tests;

public class SecurityServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "holofan-sec-" + Guid.NewGuid().ToString("N"));

    private SecurityService NewService()
        => new(Options.Create(new FfmpegOptions { DataDirectory = _dir }));

    [Fact]
    public void Starts_with_no_passphrase()
        => Assert.False(NewService().IsPassphraseSet());

    [Fact]
    public void Sets_then_verifies_the_passphrase()
    {
        var s = NewService();
        Assert.True(s.SetPassphrase(null, "open sesame"));
        Assert.True(s.IsPassphraseSet());
        Assert.True(s.Verify("open sesame"));
        Assert.False(s.Verify("wrong"));
        Assert.False(s.Verify(""));
    }

    [Fact]
    public void Persists_across_instances()
    {
        NewService().SetPassphrase(null, "persistent-pass");
        // A fresh service (as after a reboot) reads the same stored hash.
        Assert.True(NewService().Verify("persistent-pass"));
    }

    [Fact]
    public void Changing_requires_the_current_passphrase()
    {
        var s = NewService();
        s.SetPassphrase(null, "first-one");
        Assert.False(s.SetPassphrase("wrong", "second-one"));   // rejected
        Assert.True(s.Verify("first-one"));                     // unchanged
        Assert.True(s.SetPassphrase("first-one", "second-one"));
        Assert.True(s.Verify("second-one"));
    }

    [Fact]
    public void Rejects_too_short_passphrases()
        => Assert.Throws<ArgumentException>(() => NewService().SetPassphrase(null, "ab"));

    [Fact]
    public void Never_stores_the_plaintext()
    {
        NewService().SetPassphrase(null, "supersecret42");
        var stored = File.ReadAllText(Path.Combine(_dir, "security.json"));
        Assert.DoesNotContain("supersecret42", stored);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
