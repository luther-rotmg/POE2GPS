using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace POE2Radar.Tests;

/// <summary>Task 11 CF-DASH-BUTTONS — verifies that <c>merge_community.py --buffs</c> folds an
/// APPROVED Worker-echoed buffs-pack issue into the ``poe2_notable_buffs_community.json`` sidecar.
/// SL #9 override: the JSON-sidecar path lets buff-fold ship in v0.21 without waiting for a
/// generic ``TieredCatalog&lt;T&gt;`` seed loader (that ships with v0.22).</summary>
public class MergeCommunityBuffsFoldTests
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
    public void Buffs_fold_writes_sidecar_from_worker_echoed_issue_body()
    {
        var repo = RepoRoot();
        var script = Path.Combine(repo, "resources", "poe2-data", "merge_community.py");
        Assert.True(File.Exists(script), $"merge_community.py not found at {script}");

        var workdir = Path.Combine(Path.GetTempPath(), "poe2gps-merge-buffs-" + Guid.NewGuid().ToString("N"));
        try
        {
            var stagedGame = Path.Combine(workdir, "src", "POE2Radar.Core", "Game");
            var stagedWeb  = Path.Combine(workdir, "src", "POE2Radar.Overlay", "Web");
            Directory.CreateDirectory(stagedGame);
            Directory.CreateDirectory(stagedWeb);
            File.WriteAllText(Path.Combine(stagedGame, "entity_names.json"), "{}");
            File.WriteAllText(Path.Combine(stagedWeb, "labels.json"), "{}");

            // Worker-echoed body shape (see cloudflare-worker/worker.js:203-214 buildIssue('buffs')).
            // Two submissions of the same 'monster_enrage' Deadly buff exercise the aggregate counter.
            var packJson1 = "{\"buffs\":[{\"path\":\"monster_enrage\",\"tier\":\"Deadly\"},{\"path\":\"chilling_presence_aura\",\"tier\":\"Notable\"}]}";
            var packJson2 = "{\"buffs\":[{\"path\":\"monster_enrage\",\"tier\":\"Deadly\"}]}";
            string Wrap(string p) =>
                "**buffs** (auto-filtered)\n\n<details><summary>Full pack JSON</summary>\n\n"
                + "```json\n" + p + "\n```\n</details>";

            var issues = new object[]
            {
                new {
                    body   = Wrap(packJson1),
                    author = new { login = "syrairc" },
                    number = 100,
                    url    = "https://github.com/luther-rotmg/POE2GPS/issues/100",
                    labels = new object[] {
                        new { name = "community-pack" },
                        new { name = "approved" },
                        new { name = "buffs" },
                    },
                },
                new {
                    body   = Wrap(packJson2),
                    author = new { login = "syrairc" },
                    number = 101,
                    url    = "https://github.com/luther-rotmg/POE2GPS/issues/101",
                    labels = new object[] {
                        new { name = "community-pack" },
                        new { name = "approved" },
                        new { name = "buffs" },
                    },
                },
            };
            var overridePath = Path.Combine(workdir, "gh.json");
            File.WriteAllText(overridePath, JsonSerializer.Serialize(issues));

            var psi = new ProcessStartInfo(PickPython(), $"\"{script}\" --buffs")
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
            Assert.True(p.WaitForExit(30_000), "merge_community.py --buffs did not exit in 30s");
            Assert.Equal(0, p.ExitCode);
            Assert.Contains("buffs fold: 2 unique paths", stdout);

            var sidecar = Path.Combine(stagedGame, "poe2_notable_buffs_community.json");
            Assert.True(File.Exists(sidecar),
                $"expected sidecar at {sidecar}\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");

            using var doc = JsonDocument.Parse(File.ReadAllText(sidecar));
            Assert.True(doc.RootElement.TryGetProperty("buffs", out var arr));
            var enrage = arr.EnumerateArray()
                .FirstOrDefault(e => e.GetProperty("path").GetString() == "monster_enrage");
            Assert.Equal(JsonValueKind.Object, enrage.ValueKind);
            Assert.Equal(2, enrage.GetProperty("count").GetInt32());
            Assert.True(enrage.GetProperty("tiers").TryGetProperty("Deadly", out var deadly));
            Assert.Equal(2, deadly.GetInt32());
        }
        finally
        {
            try { Directory.Delete(workdir, recursive: true); } catch { }
        }
    }
}
