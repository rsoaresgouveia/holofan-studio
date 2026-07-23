using HoloFan.Device;

namespace HoloFan.Web.Services;

/// <summary>
/// Emulated per-clip management for the fan. The device has no delete-one/rename command and
/// can't hand a clip back over WiFi (see REVERSE_ENGINEERING.md), so the only way to change what
/// it holds is <b>Format Disk + re-upload</b>. This service does exactly that against a chosen
/// set of library clips: format the card, then upload the survivors, in order. From that one
/// primitive you get delete (drop it from the selection), rename (rename in the library first),
/// bulk delete and reorder.
///
/// It runs in the background with progress, because uploading several clips over WiFi takes a
/// while. Only one sync runs at a time.
/// </summary>
public sealed class FanSyncService
{
    private readonly FanClient _fan;
    private readonly LibraryService _library;
    private readonly ILogger<FanSyncService> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile Snapshot _state = Snapshot.Idle;

    public FanSyncService(FanClient fan, LibraryService library, ILogger<FanSyncService> log)
    {
        _fan = fan;
        _library = library;
        _log = log;
    }

    /// <summary>Progress for the current/last sync. <c>State</c> ∈ idle|running|done|failed.</summary>
    public sealed record Snapshot(
        string State, string Message, double Progress, int Total, int Done,
        string? Error, IReadOnlyList<string>? Playlist)
    {
        public static readonly Snapshot Idle = new("idle", "", 0, 0, 0, null, null);
    }

    public Snapshot State => _state;
    public bool IsRunning => _state.State == "running";

    /// <summary>
    /// Clips that a sync to <paramref name="targetNames"/> would lose: on the fan now but not in
    /// the target set (case-insensitive by name). Surfacing these prevents silent data loss —
    /// they exist on the card but not in the library, so a Format would erase them for good.
    /// </summary>
    public static IReadOnlyList<string> ClipsLost(
        IEnumerable<string> currentFanNames, IEnumerable<string> targetNames)
    {
        var keep = new HashSet<string>(targetNames, StringComparer.OrdinalIgnoreCase);
        return currentFanNames.Where(n => !keep.Contains(n)).ToList();
    }

    /// <summary>The clips currently on the fan, from the last connect.</summary>
    public IReadOnlyList<string> CurrentFanClips() => _fan.List();

    /// <summary>
    /// Starts a background sync to exactly the given library clips, in order. Resolves the clips
    /// up front so a bad id fails immediately, before anything on the fan is touched.
    /// </summary>
    public void Start(IReadOnlyList<string> clipIds)
    {
        if (!_gate.Wait(0))
            throw new InvalidOperationException("A sync is already running.");
        try
        {
            var clips = clipIds
                .Select(id => _library.Get(id) ?? throw new InvalidOperationException($"Unknown clip id {id}."))
                .ToList();
            _state = new Snapshot("running", "Formatting the fan…", 0, clips.Count, 0, null, null);
            _ = Task.Run(() => RunAsync(clips));
        }
        catch
        {
            _gate.Release();
            throw;
        }
    }

    private async Task RunAsync(List<LibraryService.LibraryClip> clips)
    {
        try
        {
            await _fan.ConnectAsync();
            await _fan.SendAsync(FanCommand.FormatDisk, confirmDestructive: true);

            // Format is asynchronous on the device — wait until it reports an empty card. If it
            // never empties we abort BEFORE uploading, so we never append to a still-full card.
            var emptied = false;
            for (var i = 0; i < 15 && !emptied; i++)
            {
                await Task.Delay(2000);
                try { await _fan.ConnectAsync(); emptied = _fan.List().Count == 0; }
                catch { /* the card can drop the socket during a format; keep polling */ }
            }
            if (!emptied)
                throw new InvalidOperationException(
                    "The fan did not report an empty card after Format — aborting before re-upload " +
                    "so nothing is duplicated. Try again once the fan has settled.");

            for (var i = 0; i < clips.Count; i++)
            {
                var c = clips[i];
                _state = _state with
                {
                    Message = $"Uploading “{c.Name}” ({i + 1}/{clips.Count})…",
                    Done = i,
                    Progress = (double)i / Math.Max(1, clips.Count),
                };
                var path = _library.PathOf(c.Id)
                    ?? throw new InvalidOperationException($"Clip “{c.Name}” is missing from the library.");
                var bytes = await File.ReadAllBytesAsync(path);
                await _fan.UploadAsync(c.Name, bytes);
            }

            await _fan.ConnectAsync();
            _state = new Snapshot("done",
                clips.Count == 0 ? "Fan cleared." : $"Done — {clips.Count} clip(s) on the fan.",
                1, clips.Count, clips.Count, null, _fan.List().ToList());
            _log.LogInformation("Fan sync complete: {Count} clips", clips.Count);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Fan sync failed");
            _state = _state with { State = "failed", Error = ex.Message };
        }
        finally
        {
            _gate.Release();
        }
    }
}
