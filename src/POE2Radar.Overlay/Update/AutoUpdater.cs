using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using POE2Radar.Core.Update;
using POE2Radar.Overlay.Config;

namespace POE2Radar.Overlay.Update;

/// <summary>
/// Silent self-updater. Chrome-style: during a session it downloads + verifies the newest release and
/// STAGES it; on the NEXT launch it atomically swaps Overlay.exe (safe because the running process is a
/// hardlink, not the file itself) and relaunches into it — inheriting the admin token, no .cmd, no UAC,
/// no SmartScreen. Retains Overlay.old.exe for one generation for crash rollback. Touches ONLY our own
/// files (Overlay.exe + CHANGELOG.md) — never config/ or icons/, never the game.
/// </summary>
internal static class AutoUpdater
{
    private const string Repo = "luther-rotmg/POE2GPS";
    // RollbackThreshold = 3: boot.json is written with Attempts=1 at promote time; the hardlink-bounce
    // pass (non-`--launched`) does NOT touch boot.json; each `--launched` boot increments by one via
    // RollbackIfCrashLooping. A healthy first --launched boot reaches Attempts=2 and is cleared by
    // ConfirmHealthy. Rollback fires when Attempts >= 3 (i.e., two consecutive crash-loop boots).
    private const int RollbackThreshold = 3;
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(120);
    private const long MaxBytes = 250L * 1024 * 1024;        // sanity cap on the download

    private static string Dir(string i)      => Path.Combine(i, ".update");
    private static string StagedExe(string i)=> Path.Combine(Dir(i), "Overlay.new.exe");
    private static string StagedLog(string i)=> Path.Combine(Dir(i), "CHANGELOG.md");
    private static string StatePath(string i)=> Path.Combine(Dir(i), "state.json");
    private static string BootPath(string i) => Path.Combine(Dir(i), "boot.json");
    private static string CanonExe(string i) => Path.Combine(i, "Overlay.exe");
    private static string BackupExe(string i)=> Path.Combine(i, "Overlay.old.exe");

    private record Boot(string Target, int Attempts);

    // ─────────────────────────── background check + stage (never blocks startup) ───────────────────────────

    public static async Task CheckAndStageAsync(string mode, string current, string installDir, CancellationToken ct, Task<UpdateChecker.Result>? precheck = null, RadarSettings? settings = null)
    {
        if (mode != "silent") return;
        try
        {
            // Discovery URL selection: when the user is on the preview (RC) channel OR has set a custom
            // release-list URL, take the settings-aware path (fetches directly, honouring the override
            // and the /releases list schema). Otherwise reuse the precheck the banner already fired so
            // stable-default users still make exactly ONE GitHub request per launch (unchanged v0.20.0
            // behaviour — byte-for-byte the same call).
            var preview = settings != null &&
                          string.Equals(settings.UpdateChannel, "preview", StringComparison.OrdinalIgnoreCase);
            var overrideUrl = settings?.UpdateUrl;
            var useCustomPath = preview || !string.IsNullOrWhiteSpace(overrideUrl);

            string? tag;
            if (useCustomPath) tag = await ResolveTargetTagAsync(preview, overrideUrl, ct);
            else               tag = (precheck != null ? await precheck : await UpdateChecker.CheckAsync()).Latest;

            if (tag is not { Length: > 0 } || !AutoUpdatePolicy.IsNewer(current, tag)) return;

            var state = ReadState(installDir);
            if (!AutoUpdatePolicy.ShouldAttempt(state, tag)) return;

            var ok = await TryStageAsync(tag, installDir, ct);
            // Note: only a definitive false (bad checksum / wrong content) burns a retry; a thrown transient
            // failure (offline/timeout) is swallowed below without incrementing the counter — by design.
            if (ok) { WriteState(installDir, new AutoUpdatePolicy.UpdateState(tag, 0)); }
            else    { BumpFailure(installDir, tag); }
        }
        catch { /* offline / rate-limited / IO — try again next launch */ }
    }

    /// <summary>
    /// Fetch the release-discovery URL and pull out a target tag. Preview channel scans /releases and
    /// returns the newest prerelease by <c>published_at</c> desc; if no prerelease exists we fall back
    /// to the default <c>/releases/latest</c> endpoint so the user is never left updateless. Stable
    /// channel just parses <c>tag_name</c> off the single release object /releases/latest returns.
    /// </summary>
    private static async Task<string?> ResolveTargetTagAsync(bool preview, string? urlOverride, CancellationToken ct)
    {
        var listUrl = AutoUpdatePolicy.Resolve(preview, urlOverride);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("POE2GPS-UpdateCheck");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        var json = await http.GetStringAsync(listUrl, ct).ConfigureAwait(false);
        var tag = preview ? PickNewestPrerelease(json) : ParseLatest(json);

        // Preview-channel fallback: no prerelease published yet → treat as if channel were stable for
        // this cycle (matches the "never leave the user updateless" invariant in the plan). Only fires
        // when we control the URL — an overridden URL might not offer /releases/latest, so we honour
        // whatever list-shape it returns instead of hitting a possibly-nonexistent path.
        if (preview && tag is null && string.IsNullOrWhiteSpace(urlOverride))
        {
            var fallback = AutoUpdatePolicy.Resolve(preview: false, urlOverride: null);
            var fjson = await http.GetStringAsync(fallback, ct).ConfigureAwait(false);
            tag = ParseLatest(fjson);
        }

        return tag;
    }

    /// <summary>Parse the tag off a single-release object (the /releases/latest schema).</summary>
    internal static string? ParseLatest(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("tag_name", out var t))
                return t.GetString();
        }
        catch { }
        return null;
    }

    /// <summary>Pick the newest prerelease's tag from a /releases list, ordered by <c>published_at</c>
    /// descending. Returns null if no prerelease is present.</summary>
    internal static string? PickNewestPrerelease(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            string? bestTag = null;
            DateTimeOffset bestTs = DateTimeOffset.MinValue;
            var haveTs = false;

            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (!e.TryGetProperty("prerelease", out var pr) || pr.ValueKind != JsonValueKind.True) continue;
                var tagEl = e.TryGetProperty("tag_name", out var t) ? t : default;
                if (tagEl.ValueKind != JsonValueKind.String) continue;
                var tag = tagEl.GetString();
                if (string.IsNullOrEmpty(tag)) continue;

                // Prefer published_at ordering (matches the "picks the newest prerelease via published_at
                // desc" contract). Fall back to list order for entries missing the timestamp so a draft-y
                // release still surfaces something to compare instead of dropping every candidate.
                if (e.TryGetProperty("published_at", out var pubEl) && pubEl.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(pubEl.GetString(), out var ts))
                {
                    if (!haveTs || ts > bestTs) { bestTs = ts; bestTag = tag; haveTs = true; }
                }
                else if (!haveTs && bestTag is null)
                {
                    bestTag = tag;
                }
            }
            return bestTag;
        }
        catch { }
        return null;
    }

    private static async Task<bool> TryStageAsync(string tag, string installDir, CancellationToken ct)
    {
        var dir = Dir(installDir);
        Directory.CreateDirectory(dir);
        var zipPath = Path.Combine(dir, "download.zip");
        try
        {
            using var http = new HttpClient { Timeout = DownloadTimeout };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("POE2GPS-AutoUpdate");

            // 1) Fetch the checksum manifest (REQUIRED — no checksum, no update).
            var sums = await http.GetStringAsync(AutoUpdatePolicy.ChecksumUrl(Repo, tag), ct);
            var expected = AutoUpdatePolicy.ExpectedSha(sums, AutoUpdatePolicy.AssetName(tag));
            if (expected is null) return false;

            // 2) Download the zip to a temp file (bounded).
            using (var resp = await http.GetAsync(AutoUpdatePolicy.ZipUrl(Repo, tag), HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                if (resp.Content.Headers.ContentLength is > MaxBytes) return false;
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(zipPath);
                await src.CopyToAsync(dst, ct);
            }
            if (new FileInfo(zipPath).Length > MaxBytes) return false;

            // 3) Verify SHA-256 BEFORE extraction.
            if (!string.Equals(Sha256Hex(zipPath), expected, StringComparison.OrdinalIgnoreCase)) return false;

            // 4) Extract Overlay.exe + CHANGELOG.md only.
            var tmpExe = Path.Combine(dir, "Overlay.extract.exe");
            var tmpLog = Path.Combine(dir, "CHANGELOG.extract.md");
            File.Delete(tmpExe); File.Delete(tmpLog);
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var exeEntry = zip.GetEntry("Overlay.exe");
                if (exeEntry is null) return false;
                exeEntry.ExtractToFile(tmpExe, overwrite: true);
                zip.GetEntry("CHANGELOG.md")?.ExtractToFile(tmpLog, overwrite: true);
            }

            // 5) Verify the extracted exe's FileVersion is actually the target (or newer).
            var fv = FileVersionInfo.GetVersionInfo(tmpExe).FileVersion;
            if (fv is null || !(AutoUpdatePolicy.IsNewer(UpdateChecker.Current, fv) ||
                                string.Equals(NormV(fv), NormV(tag), StringComparison.OrdinalIgnoreCase)))
                return false;

            StripMotw(tmpExe);

            // 6) Atomically promote to the staged names.
            File.Move(tmpExe, StagedExe(installDir), overwrite: true);
            if (File.Exists(tmpLog)) File.Move(tmpLog, StagedLog(installDir), overwrite: true);
            return true;
        }
        finally { try { File.Delete(zipPath); } catch { } }
    }

    /// <summary>Delete any staged update files (called on non-silent launch so stale stages don't linger).</summary>
    public static void DiscardStaged(string installDir)
    {
        SafeDelete(StagedExe(installDir));
        SafeDelete(StagedLog(installDir));
    }

    // ─────────────────────────── apply a staged update at startup (then relaunch) ───────────────────────────

    /// <returns>true if it swapped + relaunched (caller must return 0 immediately).</returns>
    public static bool ApplyStagedIfPresent(string current, string installDir)
    {
        var staged = StagedExe(installDir);
        if (!File.Exists(staged)) return false;
        try
        {
            var fv = FileVersionInfo.GetVersionInfo(staged).FileVersion;
            if (fv is null || !AutoUpdatePolicy.IsNewer(current, fv)) { SafeDelete(staged); return false; }

            var canon = CanonExe(installDir);
            var backup = BackupExe(installDir);
            SafeDelete(backup);
            if (File.Exists(canon)) File.Move(canon, backup, overwrite: true);  // keep old for rollback
            File.Move(staged, canon);                                            // atomic swap (we hold the hardlink inode)
            // Changelog copy is best-effort: a locked/missing file must NEVER unwind a successful exe swap.
            try { if (File.Exists(StagedLog(installDir)))
                      File.Copy(StagedLog(installDir), Path.Combine(installDir, "CHANGELOG.md"), overwrite: true); } catch { }

            WriteBoot(installDir, new Boot(NormV(fv), 1));
            SafeDelete(StagedLog(installDir));
            Relaunch(canon, installDir);
            return true;
        }
        catch
        {
            // If the promote failed after the backup move, restore Overlay.exe so a cold launch still works.
            try { var canon = CanonExe(installDir); var backup = BackupExe(installDir);
                  if (!File.Exists(canon) && File.Exists(backup)) File.Move(backup, canon); } catch { }
            // Purge the staged exe so a persistently-failing promote (e.g. AV lock) doesn't retry every launch.
            // Do NOT call BumpFailure — a promote failure is transient/local; re-staging fresh next session is correct.
            SafeDelete(StagedExe(installDir));
            return false;   // any failure -> continue booting the current version unchanged
        }
    }

    // ─────────────────────────── crash-loop rollback ───────────────────────────

    /// <returns>true if it rolled back + relaunched (caller must return 0).</returns>
    public static bool RollbackIfCrashLooping(string installDir)
    {
        var boot = ReadBoot(installDir);
        if (boot is null) return false;
        var attempts = boot.Attempts + 1;
        WriteBoot(installDir, boot with { Attempts = attempts });
        if (attempts < RollbackThreshold) return false;

        var backup = BackupExe(installDir);
        if (!File.Exists(backup)) { SafeDelete(BootPath(installDir)); return false; } // no backup -> nothing to do
        try
        {
            var canon = CanonExe(installDir);
            File.Move(backup, canon, overwrite: true);                 // restore last-known-good
            WriteState(installDir, new AutoUpdatePolicy.UpdateState(boot.Target, 2)); // mark target bad
            SafeDelete(BootPath(installDir));
            Relaunch(canon, installDir);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Called once the app has run briefly without crashing: the applied update is good.
    /// Clears boot.json (the crash-loop counter) but retains Overlay.old.exe for one full generation —
    /// it is only replaced when the NEXT update's ApplyStagedIfPresent runs SafeDelete(backup) before
    /// writing a fresh backup. This matches the README's "kept for one generation" promise.</summary>
    public static void ConfirmHealthy(string installDir)
    {
        SafeDelete(BootPath(installDir));
        // NOTE: BackupExe (Overlay.old.exe) is intentionally NOT deleted here; it persists until
        // the next update's ApplyStagedIfPresent replaces it, giving users a one-generation rollback.
    }

    /// <summary>The version currently staged for next launch (for the /api/version "pending" line), or null.</summary>
    public static string? PendingVersion(string installDir)
    {
        try
        {
            var staged = StagedExe(installDir);
            if (File.Exists(staged)) return FileVersionInfo.GetVersionInfo(staged).FileVersion;
        }
        catch { }
        return null;
    }

    // ─────────────────────────── helpers ───────────────────────────

    private static void Relaunch(string exe, string installDir)
    {
        var p = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = installDir });
        if (p is null) throw new InvalidOperationException("relaunch failed");
    }

    private static string Sha256Hex(string path)
    {
        using var fs = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    private static void StripMotw(string path)
    { try { File.Delete(path + ":Zone.Identifier"); } catch { } }

    private static string NormV(string v) => v.Trim().TrimStart('v', 'V');
    private static void SafeDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }

    private static AutoUpdatePolicy.UpdateState? ReadState(string i)
    { try { return JsonSerializer.Deserialize<AutoUpdatePolicy.UpdateState>(File.ReadAllText(StatePath(i))); } catch { return null; } }
    private static void WriteState(string i, AutoUpdatePolicy.UpdateState s) => AtomicJson(StatePath(i), s);
    private static void BumpFailure(string i, string tag)
    {
        var s = ReadState(i);
        var n = (s is not null && string.Equals(s.TargetVersion, tag, StringComparison.OrdinalIgnoreCase)) ? s.Failures + 1 : 1;
        WriteState(i, new AutoUpdatePolicy.UpdateState(tag, n));
    }

    private static Boot? ReadBoot(string i)
    { try { return JsonSerializer.Deserialize<Boot>(File.ReadAllText(BootPath(i))); } catch { return null; } }
    private static void WriteBoot(string i, Boot b) => AtomicJson(BootPath(i), b);

    private static void AtomicJson<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Process-unique temp name so two instances sharing an install dir can't collide.
            var tmp = path + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value));
            File.Move(tmp, path, overwrite: true);
        }
        catch { }
    }
}
