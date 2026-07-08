using System;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace POE2Radar.Tests.CommunityPipeline;

/// <summary>Task 15 CF-DEPRECATE-ATLAS -- verifies that <c>merge_atlas_packs.py</c> now emits a
/// stderr deprecation banner routing maintainers at <c>merge_community.py --catalog names</c>, and
/// that <c>docs/CONTRIBUTING-atlas.md</c> has been rewritten as a redirect stub. Path B of SL #16:
/// the old script is kept in-tree so historical muscle memory does not silently break, but the
/// single merge rail is <c>merge_community.py</c>.</summary>
public class MergeAtlasDeprecationTests
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
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
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
    public void MergeAtlasPacks_PrintsDeprecationBannerToStderr()
    {
        var repo = RepoRoot();
        var script = Path.Combine(repo, "resources", "poe2-data", "merge_atlas_packs.py");
        Assert.True(File.Exists(script), $"missing {script}");

        var psi = new ProcessStartInfo(PickPython(), $"\"{script}\" --help")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WorkingDirectory = repo,
        };
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(10_000);

        Assert.Contains("DEPRECATED", stderr);
        Assert.Contains("merge_community.py", stderr);
        Assert.Contains("--catalog names", stderr);
    }

    [Fact]
    public void ContributingAtlasDoc_IsRedirectStub_NoInternalToolingPaths()
    {
        var doc = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "CONTRIBUTING-atlas.md"));
        Assert.Contains("Deprecated", doc);
        Assert.Contains("CONTRIBUTING.md", doc);
        Assert.Contains("merge_community.py", doc);
        // Internal-tooling leak audit (Spec Section 2 non-negotiable).
        Assert.DoesNotContain("superpowers/", doc);
        Assert.DoesNotContain(".superpowers/", doc);
        Assert.DoesNotContain("docs/superpowers/", doc);
    }
}
