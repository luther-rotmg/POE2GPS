using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace POE2Radar.Tests;

/// <summary>Task 11 CF-DASH-BUTTONS — verifies that <c>merge_community.py --preload</c> folds an
/// APPROVED Worker-echoed preload-pack issue into the ``poe2_notable_paths_community.json`` sidecar.
/// Drives the script via <see cref="POE2GPS_MERGE_GH_JSON"/> override so the test never shells out
/// to ``gh`` and never mutates the real repo.</summary>
public class MergeCommunityPreloadFoldTests
{
    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "POE2Radar.slnx"))) d = d.Parent;
        if (d == null) throw new InvalidOperationException("repo root (POE2Radar.slnx) not found");
        return d.FullName;
    }

    private static string PickPython()
    {
        // Test host may be minimal — search PATHEXT-agnostic candidates.
        foreach (var name in new[] { "python", "python3", "py" })
        {
            try
            {
                var psi = new ProcessStartInfo(name, "--version")
                {
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                };
                using var p = Process.Start(psi)!;
                p.WaitForExit(10_000);
                if (p.ExitCode == 0) return name;
            }
            catch { }
        }
        return "python";
    }

    [Fact]
    public void Preload_fold_writes_sidecar_from_worker_echoed_issue_body()
    {
        var repo = RepoRoot();
        var script = Path.Combine(repo, "resources", "poe2-data", "merge_community.py");
        Assert.True(File.Exists(script), $"merge_community.py not found at {script}");

        var workdir = Path.Combine(Path.GetTempPath(), "poe2gps-merge-preload-" + Guid.NewGuid().ToString("N"));
        try
        {
            var stagedGame = Path.Combine(workdir, "src", "POE2Radar.Core", "Game");
            var stagedWeb  = Path.Combine(workdir, "src", "POE2Radar.Overlay", "Web");
            Directory.CreateDirectory(stagedGame);
            Directory.CreateDirectory(stagedWeb);
            File.WriteAllText(Path.Combine(stagedGame, "entity_names.json"), "{}");
            File.WriteAllText(Path.Combine(stagedWeb, "labels.json"), "{}");

            // Worker-echoed body shape (see cloudflare-worker/worker.js:215-224 buildIssue('preload')).
            var packJson = "{\"preloads\":[" +
                "{\"path\":\"metadata/monsters/leaguebreach/rat\",\"freq\":0.42}," +
                "{\"path\":\"metadata/leagues/breach/portal\",\"freq\":0.05}]}";
            var body = "**2 preload paths** (auto-filtered; bare .dds/.ao rejected)\n\n"
                     + "<details><summary>Full pack JSON</summary>\n\n"
                     + "```json\n" + packJson + "\n```\n"
                     + "</details>\n\n*Review, then label `approved` to fold into PreloadCatalog.*";

            var issues = new object[]
            {
                new {
                    body,
                    author = new { login = "syrairc" },
                    number = 99,
                    url    = "https://github.com/luther-rotmg/POE2GPS/issues/99",
                    labels = new object[] {
                        new { name = "community-pack" },
                        new { name = "approved" },
                        new { name = "preload" },
                    },
                }
            };
            var overridePath = Path.Combine(workdir, "gh.json");
            File.WriteAllText(overridePath, JsonSerializer.Serialize(issues));

            var psi = new ProcessStartInfo(PickPython(), $"\"{script}\" --preload")
            {
                WorkingDirectory = workdir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.Environment["POE2GPS_MERGE_GH_JSON"] = overridePath;

            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            Assert.True(p.WaitForExit(30_000), "merge_community.py --preload did not exit in 30s");
            Assert.Equal(0, p.ExitCode);
            Assert.Contains("preload fold: 2 unique paths", stdout);

            var sidecar = Path.Combine(stagedGame, "poe2_notable_paths_community.json");
            Assert.True(File.Exists(sidecar),
                $"expected sidecar at {sidecar}\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");

            using var doc = JsonDocument.Parse(File.ReadAllText(sidecar));
            Assert.True(doc.RootElement.TryGetProperty("paths", out var paths));
            Assert.Equal(JsonValueKind.Array, paths.ValueKind);
            var rat = paths.EnumerateArray()
                .FirstOrDefault(e => e.GetProperty("path").GetString() == "metadata/monsters/leaguebreach/rat");
            Assert.Equal(JsonValueKind.Object, rat.ValueKind);
            Assert.Equal(1, rat.GetProperty("count").GetInt32());
            Assert.Equal(0.42, rat.GetProperty("freq_sum").GetDouble(), 3);
        }
        finally
        {
            try { Directory.Delete(workdir, recursive: true); } catch { }
        }
    }
}
