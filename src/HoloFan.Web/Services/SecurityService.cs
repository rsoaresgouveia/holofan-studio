using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace HoloFan.Web.Services;

/// <summary>
/// Guards destructive fan actions (Format Disk, Clear Cache) behind an admin passphrase.
///
/// The passphrase is stored only as a salted PBKDF2 hash in the persistent data volume
/// (<c>{DataDirectory}/security.json</c>), so it survives Pi reboots, never appears in the
/// repo, and the plaintext is never written anywhere. Set it once from the UI; changing it
/// requires the current one.
/// </summary>
public sealed class SecurityService
{
    private const int Iterations = 100_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    private readonly string _path;
    private readonly object _lock = new();

    public SecurityService(IOptions<FfmpegOptions> opt)
        => _path = Path.Combine(opt.Value.DataDirectory, "security.json");

    private sealed record Stored(string Salt, string Hash, int Iterations);

    public bool IsPassphraseSet() => File.Exists(_path);

    /// <summary>
    /// Sets or changes the passphrase. When one already exists, <paramref name="current"/> must
    /// match. Returns false if the current passphrase is wrong; throws if the new one is too weak.
    /// </summary>
    public bool SetPassphrase(string? current, string newPassphrase)
    {
        if (string.IsNullOrWhiteSpace(newPassphrase) || newPassphrase.Trim().Length < 4)
            throw new ArgumentException("Passphrase must be at least 4 characters.");

        lock (_lock)
        {
            if (IsPassphraseSet() && !Verify(current))
                return false;

            var salt = RandomNumberGenerator.GetBytes(SaltBytes);
            var hash = Rfc2898DeriveBytes.Pbkdf2(
                newPassphrase.Trim(), salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
            var json = JsonSerializer.Serialize(new Stored(
                Convert.ToBase64String(salt), Convert.ToBase64String(hash), Iterations));

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, json);
            return true;
        }
    }

    /// <summary>Constant-time check of a passphrase against the stored hash.</summary>
    public bool Verify(string? passphrase)
    {
        if (!IsPassphraseSet() || string.IsNullOrEmpty(passphrase)) return false;

        Stored s;
        try { s = JsonSerializer.Deserialize<Stored>(File.ReadAllText(_path))!; }
        catch { return false; }

        var expected = Convert.FromBase64String(s.Hash);
        var actual = Rfc2898DeriveBytes.Pbkdf2(
            passphrase.Trim(), Convert.FromBase64String(s.Salt), s.Iterations,
            HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
