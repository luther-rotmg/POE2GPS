using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using POE2Radar.Core.Update;

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
    private const int RollbackThreshold = 3;                 // healthy boot clears by attempt 2 (hardlink adds one pass)
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

    public static async Task CheckAndStageAsync(string mode, string current, string installDir, CancellationToken ct)
    {
        if (mode != "silent") return;
        try
        {
            var res = await UpdateChecker.CheckAsync();
            if (res.Latest is not { Length: > 0 } tag || !AutoUpdatePolicy.IsNewer(current, tag)) return;

            var state = ReadState(installDir);
            if (!AutoUpdatePolicy.ShouldAttempt(state, tag)) return;

            var ok = await TryStageAsync(tag, installDir, ct);
            if (ok) { WriteState(installDir, new AutoUpdatePolicy.UpdateState(tag, 0)); }
            else    { BumpFailure(installDir, tag); }
        }
        catch { /* offline / rate-limited / IO — try again next launch */ }
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
            if (File.Exists(StagedLog(installDir)))
                try { File.Copy(StagedLog(installDir), Path.Combine(installDir, "CHANGELOG.md"), overwrite: true); } catch { }

            WriteBoot(installDir, new Boot(NormV(fv), 1));
            SafeDelete(StagedLog(installDir));
            Relaunch(canon, installDir);
            return true;
        }
        catch { return false; }   // any failure -> continue booting the current version unchanged
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

    /// <summary>Called once the app has run briefly without crashing: the applied update is good.</summary>
    public static void ConfirmHealthy(string installDir)
    {
        SafeDelete(BootPath(installDir));
        SafeDelete(BackupExe(installDir));
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
        => Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = installDir });

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
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value));
            File.Move(tmp, path, overwrite: true);
        }
        catch { }
    }
}
