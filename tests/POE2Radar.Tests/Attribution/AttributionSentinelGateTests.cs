// Ported from ExileCampaigns2 by syrairc under TODO(syrairc-license) — upstream commit TODO(syrairc-hash).
// (Sentinel tokens above document the attribution-gate contract this file tests. Not a runtime port.)
using System;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace POE2Radar.Tests.Attribution;

// The [Collection] attribute puts every method in this class into a single, single-threaded xunit
// collection. Each test spawns a PowerShell child process; running them in parallel with the rest of
// the suite (SSE-integration tests, HTTP-listener tests, atlas provider tests) triggers a spurious
// -196608 (0xFFFD_0000) child exit code under contention on the CI runner + dev machines. Serialising
// this class dodges the race without touching the rest of the suite's parallel budget.
[CollectionDefinition(nameof(AttributionGateSequential), DisableParallelization = true)]
public sealed class AttributionGateSequential { }

/// <summary>
/// End-to-end tests for <c>scripts/attribution-sentinel-gate.ps1</c>. Each test spins up an isolated
/// temp-directory fixture that mimics the shape the DRAFT-mode gate scans (README.md, CHANGELOG.md,
/// HEADER.md, DashboardHtml.cs, discord-v0.21.md, and the four ported .cs shims) and asserts the
/// script's exit code + stdout for both PASS and FAIL cases.
///
/// The gate ships with two modes:
///   • Draft     — enforces sentinel presence + rejects bare angle-bracket tokens + rejects any
///                 superpowers/ path in a public surface. (This task, EC2-ATTR-DRAFT.)
///   • Formalize — flipped by EC2-ATTR-FORMALIZE once the syrairc DM lands (PMS-12). Rejects any
///                 surviving TODO(syrairc-*) sentinel.
/// </summary>
[Collection(nameof(AttributionGateSequential))]
public class AttributionSentinelGateTests
{
    // Test bin is `tests/POE2Radar.Tests/bin/x64/Release/net10.0-windows/` — six hops up to repo root.
    // We probe upward instead of hard-coding the depth so a solution-file layout change (or a
    // debug/release-only build stripping the `x64` intermediate) doesn't silently break the tests.
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string GatePath = Path.Combine(
        RepoRoot, "scripts", "attribution-sentinel-gate.ps1");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "POE2Radar.slnx")) ||
                File.Exists(Path.Combine(dir.FullName, "POE2Radar.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate repo root from " + AppContext.BaseDirectory);
    }

    // The tests target net10.0-windows, so `powershell.exe` (Windows PowerShell 5.1) is always present
    // at a stable absolute path. CI's `windows-latest` runner exposes both pwsh + powershell; we pick
    // powershell.exe to avoid PATH ambiguity under `dotnet test`'s test-host isolation (a PATH-relative
    // "pwsh" probe raced under xunit parallelism and returned a phantom 0xFFFD_0000 child exit).
    private static readonly string PwshExe = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell", "v1.0", "powershell.exe");

    private static (int exit, string stdout, string stderr) RunGate(string root, string mode = "Draft")
    {
        var psi = new ProcessStartInfo(PwshExe,
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{GatePath}\" -Root \"{root}\" -Mode {mode}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput  = true, // detach from test-host stdin so powershell doesn't inherit a broken handle
            UseShellExecute = false,
            CreateNoWindow  = true,
            WorkingDirectory = root,
        };
        using var p = Process.Start(psi)!;
        p.StandardInput.Close(); // signal EOF immediately; script never reads stdin
        // Drain both streams asynchronously to sidestep the classic pipe-buffer deadlock.
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        var exited = p.WaitForExit(60_000);
        if (!exited) { try { p.Kill(true); } catch { } }
        return (exited ? p.ExitCode : -1,
                outTask.GetAwaiter().GetResult(),
                errTask.GetAwaiter().GetResult());
    }

    private static string MakeFixture()
    {
        var dir = Path.Combine(Path.GetTempPath(), "attr-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "src", "POE2Radar.Core", "Campaign", "Guide", "Data", "poe2"));
        Directory.CreateDirectory(Path.Combine(dir, "src", "POE2Radar.Core", "Campaign", "Guide"));
        Directory.CreateDirectory(Path.Combine(dir, "src", "POE2Radar.Overlay", "Web"));
        Directory.CreateDirectory(Path.Combine(dir, ".github", "ISSUE_TEMPLATE"));
        Directory.CreateDirectory(Path.Combine(dir, "scratchpad"));
        return dir;
    }

    // Writes a fixture where every sentinel-required file carries BOTH tokens and no public
    // surface trips the bare-token / superpowers guards. This is the "green" baseline; tests that
    // want a red state mutate one file after WriteGood(root) has laid down the healthy tree.
    private static void WriteGood(string root)
    {
        void Put(string rel, string body) =>
            File.WriteAllText(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)), body);

        Put("README.md",
            "# fixture\nLicense: TODO(syrairc-license), commit: TODO(syrairc-hash).\n");
        Put("CHANGELOG.md",
            "Special thanks: license TODO(syrairc-license), commit TODO(syrairc-hash).\n");
        Put("src/POE2Radar.Core/Campaign/Guide/Data/poe2/HEADER.md",
            "Upstream: https://github.com/syrairc/ExileCampaigns2\n" +
            "License: TODO(syrairc-license)\nCommit: TODO(syrairc-hash)\n");
        Put("src/POE2Radar.Core/Campaign/Guide/RouteModel.cs",
            "// Ported from ExileCampaigns2 under TODO(syrairc-license) — commit TODO(syrairc-hash)\n");
        Put("src/POE2Radar.Core/Campaign/Guide/AdvanceEngine.cs",
            "// Ported from ExileCampaigns2 under TODO(syrairc-license) — commit TODO(syrairc-hash)\n");
        Put("src/POE2Radar.Core/Campaign/Guide/StepMeta.cs",
            "// Ported from ExileCampaigns2 under TODO(syrairc-license) — commit TODO(syrairc-hash)\n");
        Put("src/POE2Radar.Core/Campaign/Guide/PatternMatcher.cs",
            "// Ported from ExileCampaigns2 under TODO(syrairc-license) — commit TODO(syrairc-hash)\n");
        Put("src/POE2Radar.Overlay/Web/DashboardHtml.cs",
            "// license TODO(syrairc-license) commit TODO(syrairc-hash)\n");
        Put("src/POE2Radar.Overlay/Web/ApiServer.cs",
            "// license TODO(syrairc-license) commit TODO(syrairc-hash)\n");
        Put("scratchpad/discord-v0.21.md",
            "License: TODO(syrairc-license), commit: TODO(syrairc-hash)\n");
    }

    [Fact]
    public void Draft_Passes_When_Sentinels_Present_And_No_Bare_Tokens()
    {
        var root = MakeFixture();
        WriteGood(root);
        try
        {
            var r = RunGate(root);
            Assert.True(r.exit == 0,
                $"expected pass, got exit={r.exit}\nOUT:\n{r.stdout}\nERR:\n{r.stderr}");
            Assert.Contains("PASS", r.stdout, StringComparison.OrdinalIgnoreCase);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void Draft_Fails_On_Bare_AngleBracket_License_Token()
    {
        var root = MakeFixture();
        WriteGood(root);
        try
        {
            File.AppendAllText(Path.Combine(root, "README.md"), "See license <license> below.\n");
            var r = RunGate(root);
            Assert.True(r.exit == 1, $"expected exit=1, got {r.exit}\nOUT:\n{r.stdout}\nERR:\n{r.stderr}");
            Assert.Contains("bare token", r.stdout, StringComparison.OrdinalIgnoreCase);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void Draft_Fails_On_Bare_AngleBracket_Hash_Token()
    {
        var root = MakeFixture();
        WriteGood(root);
        try
        {
            File.AppendAllText(Path.Combine(root, "CHANGELOG.md"), "Pinned at <hash>.\n");
            var r = RunGate(root);
            Assert.True(r.exit == 1, $"expected exit=1, got {r.exit}\nOUT:\n{r.stdout}\nERR:\n{r.stderr}");
            Assert.Contains("bare token", r.stdout, StringComparison.OrdinalIgnoreCase);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void Draft_Fails_On_Superpowers_Path_In_Readme()
    {
        var root = MakeFixture();
        WriteGood(root);
        try
        {
            File.AppendAllText(Path.Combine(root, "README.md"), "See docs/superpowers/plan.md\n");
            var r = RunGate(root);
            Assert.True(r.exit == 1, $"expected exit=1, got {r.exit}\nOUT:\n{r.stdout}\nERR:\n{r.stderr}");
            Assert.Contains("superpowers", r.stdout, StringComparison.OrdinalIgnoreCase);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void Draft_Fails_When_HEADER_Missing_Sentinel()
    {
        var root = MakeFixture();
        WriteGood(root);
        try
        {
            File.WriteAllText(
                Path.Combine(root, "src", "POE2Radar.Core", "Campaign", "Guide", "Data", "poe2", "HEADER.md"),
                "Upstream: https://github.com/syrairc/ExileCampaigns2\n"); // no sentinels
            var r = RunGate(root);
            Assert.True(r.exit == 1, $"expected exit=1, got {r.exit}\nOUT:\n{r.stdout}\nERR:\n{r.stderr}");
            Assert.Contains("missing sentinel", r.stdout, StringComparison.OrdinalIgnoreCase);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void Formalize_Fails_When_Sentinel_Still_Present()
    {
        // FORMALIZE-mode probe: EC2-ATTR-FORMALIZE flips the CI job to -Mode Formalize after the
        // sentinel swap. Guard against a stray unswapped TODO(syrairc-*) landing back in the tree.
        var root = MakeFixture();
        WriteGood(root);
        try
        {
            var r = RunGate(root, mode: "Formalize");
            Assert.True(r.exit == 1, $"expected exit=1, got {r.exit}\nOUT:\n{r.stdout}\nERR:\n{r.stderr}");
            Assert.Contains("surviving sentinel", r.stdout, StringComparison.OrdinalIgnoreCase);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
