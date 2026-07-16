using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace POE2Radar.Overlay.Icons;

/// <summary>Result of an EnsureExtracted call.</summary>
public sealed record ExtractResult(bool Extracted, int FileCount, string? SkipReason);

/// <summary>
/// v0.36.1: first-run extractor for the K2 bundled starter icon pack. Reads
/// <c>POE2Radar.Overlay.StarterIcons.*</c> embedded resources out of this
/// assembly into the runtime <c>config/icons/</c> directory so a fresh install
/// activates icons without the user copying files. Once the target directory
/// contains ANY user file, this extractor never touches it again — user config
/// always wins.
/// </summary>
public static class EmbeddedStarterIconExtractor
{
    private const string ResourcePrefix = "POE2Radar.Overlay.StarterIcons.";

    /// <summary>If <paramref name="configIconsDirectory"/> is missing or empty,
    /// extract every embedded starter-pack resource into it. If any user file is
    /// already present, do nothing and return <c>SkipReason="user-files-present"</c>.</summary>
    public static ExtractResult EnsureExtracted(string configIconsDirectory)
    {
        if (configIconsDirectory is null) throw new ArgumentNullException(nameof(configIconsDirectory));

        var dir = new DirectoryInfo(configIconsDirectory);
        if (dir.Exists && dir.EnumerateFileSystemInfos().Any())
            return new ExtractResult(Extracted: false, FileCount: 0, SkipReason: "user-files-present");

        if (!dir.Exists) dir.Create();

        var asm = typeof(EmbeddedStarterIconExtractor).Assembly;
        var count = 0;
        foreach (var resName in asm.GetManifestResourceNames())
        {
            if (!resName.StartsWith(ResourcePrefix, StringComparison.Ordinal)) continue;
            var basename = resName.Substring(ResourcePrefix.Length);
            var destPath = Path.Combine(dir.FullName, basename);
            using (var src = asm.GetManifestResourceStream(resName))
            using (var dst = File.Create(destPath))
            {
                if (src is null) continue;
                src.CopyTo(dst);
            }
            count++;
        }

        return new ExtractResult(Extracted: true, FileCount: count, SkipReason: null);
    }
}