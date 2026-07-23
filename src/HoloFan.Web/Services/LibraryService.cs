using System.Security.Cryptography;
using System.Text.Json;
using HoloFan.Device;
using Microsoft.Extensions.Options;

namespace HoloFan.Web.Services;

/// <summary>
/// A persistent library of the fan's native <c>.bin</c> clips, living on the data volume
/// (<c>{DataDirectory}/library/</c>) so it survives Pi reboots.
///
/// The fan itself offers no per-file delete/rename and can't hand a clip back over WiFi
/// (confirmed by reverse-engineering — see REVERSE_ENGINEERING.md), so the app keeps its own
/// copy of every clip's bytes: everything it renders, plus whatever the user imports (e.g. the
/// factory demo bins). That library is what makes emulated per-file management possible —
/// "remove one clip from the fan" becomes Format Disk + re-upload the survivors from here.
/// </summary>
public sealed class LibraryService
{
    private readonly string _dir;
    private readonly string _manifest;
    private readonly object _lock = new();
    private readonly int _frameBytes = DeviceModel.Fan42F2.FrameBytes;   // 129024

    public LibraryService(IOptions<FfmpegOptions> opt)
    {
        _dir = Path.Combine(opt.Value.DataDirectory, "library");
        _manifest = Path.Combine(_dir, "library.json");
        Directory.CreateDirectory(_dir);
    }

    /// <summary>One stored clip. <c>FileName</c> is a server-generated <c>{id}.bin</c>.</summary>
    public sealed record LibraryClip(
        string Id, string Name, string FileName, long SizeBytes,
        int FrameCount, string Source, DateTimeOffset CreatedAt, string Sha256);

    public IReadOnlyList<LibraryClip> List()
    {
        lock (_lock) return Load().OrderByDescending(c => c.CreatedAt).ToList();
    }

    public LibraryClip? Get(string id)
    {
        lock (_lock) return Load().FirstOrDefault(c => c.Id == id);
    }

    /// <summary>Absolute path to a clip's bytes, or null if the id is unknown/missing.</summary>
    public string? PathOf(string id)
    {
        var clip = Get(id);
        if (clip is null) return null;
        var path = Path.Combine(_dir, clip.FileName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Adds a clip from raw bytes (an import or an in-memory render). Validates it against the
    /// fan's frame size — the length must be a whole number of 129024-byte frames (an optional
    /// 20-byte trailer is allowed) — so junk never enters the library.
    /// </summary>
    public LibraryClip Add(string name, byte[] bytes, string source)
    {
        ValidateBin(bytes.Length);
        var id = Guid.NewGuid().ToString("N");
        var fileName = id + ".bin";
        lock (_lock)
        {
            File.WriteAllBytes(Path.Combine(_dir, fileName), bytes);
            var clip = Describe(id, name, fileName, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), source);
            var all = Load();
            all.Add(clip);
            Save(all);
            return clip;
        }
    }

    /// <summary>Adds a clip by copying an existing file (e.g. a freshly rendered job output).</summary>
    public LibraryClip AddFromFile(string name, string sourcePath, string source)
    {
        var info = new FileInfo(sourcePath);
        ValidateBin(info.Length);
        var id = Guid.NewGuid().ToString("N");
        var fileName = id + ".bin";
        lock (_lock)
        {
            var dest = Path.Combine(_dir, fileName);
            File.Copy(sourcePath, dest, overwrite: true);
            string sha;
            using (var fs = File.OpenRead(dest)) sha = Convert.ToHexString(SHA256.HashData(fs));
            var clip = Describe(id, name, fileName, info.Length, sha, source);
            var all = Load();
            all.Add(clip);
            Save(all);
            return clip;
        }
    }

    public bool Rename(string id, string newName)
    {
        var clean = CleanName(newName);
        lock (_lock)
        {
            var all = Load();
            var i = all.FindIndex(c => c.Id == id);
            if (i < 0) return false;
            all[i] = all[i] with { Name = clean };
            Save(all);
            return true;
        }
    }

    public bool Delete(string id)
    {
        lock (_lock)
        {
            var all = Load();
            var clip = all.FirstOrDefault(c => c.Id == id);
            if (clip is null) return false;
            all.Remove(clip);
            Save(all);
            var path = Path.Combine(_dir, clip.FileName);
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
    }

    private LibraryClip Describe(string id, string name, string fileName, long size, string sha, string source)
        => new(id, CleanName(name), fileName, size, (int)(size / _frameBytes), NormalizeSource(source),
               DateTimeOffset.UtcNow, sha);

    private void ValidateBin(long length)
    {
        var remainder = length % _frameBytes;
        if (length < _frameBytes || (remainder != 0 && remainder != 20))
            throw new ArgumentException(
                $"Not a valid fan .bin: {length} bytes is not a whole number of {_frameBytes}-byte frames.");
    }

    // Names are display-only (files are GUIDs), but keep them tidy and bounded.
    private static string CleanName(string name)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) n = "clip";
        n = new string(n.Where(ch => !char.IsControl(ch)).ToArray());
        return n.Length > 60 ? n[..60] : n;
    }

    private static string NormalizeSource(string source) =>
        source is "factory" or "generated" or "imported" ? source : "imported";

    private List<LibraryClip> Load()
    {
        if (!File.Exists(_manifest)) return new();
        try { return JsonSerializer.Deserialize<List<LibraryClip>>(File.ReadAllText(_manifest)) ?? new(); }
        catch { return new(); }
    }

    private void Save(List<LibraryClip> clips)
    {
        // Write-then-rename so a crash mid-write can't corrupt the manifest.
        var tmp = _manifest + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(clips, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _manifest, overwrite: true);
    }
}
