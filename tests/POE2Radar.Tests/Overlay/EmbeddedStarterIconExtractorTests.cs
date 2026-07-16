using System;
using System.IO;
using System.Linq;
using POE2Radar.Overlay.Icons;
using Xunit;

namespace POE2Radar.Tests.Overlay;

public class EmbeddedStarterIconExtractorTests : IDisposable
{
    private readonly string _dir;

    public EmbeddedStarterIconExtractorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "icons-extract-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void MissingDir_Extracts_All_Embedded_Files()
    {
        Assert.False(Directory.Exists(_dir));
        var result = EmbeddedStarterIconExtractor.EnsureExtracted(_dir);
        Assert.True(result.Extracted);
        Assert.Null(result.SkipReason);
        Assert.True(result.FileCount >= 41, $"expected >=41 files (40 PNGs + mapping.json), got {result.FileCount}");
        Assert.True(Directory.Exists(_dir));
        Assert.True(File.Exists(Path.Combine(_dir, "mapping.json")));
        Assert.True(File.Exists(Path.Combine(_dir, "boss@32.png")));
        Assert.True(File.Exists(Path.Combine(_dir, "monster-rare@64.png")));
        Assert.True(File.Exists(Path.Combine(_dir, "ATTRIBUTION.md")));
    }

    [Fact]
    public void EmptyDir_Extracts_All_Embedded_Files()
    {
        Directory.CreateDirectory(_dir);
        var result = EmbeddedStarterIconExtractor.EnsureExtracted(_dir);
        Assert.True(result.Extracted);
        Assert.True(File.Exists(Path.Combine(_dir, "mapping.json")));
    }

    [Fact]
    public void UserFilesPresent_DoesNothing()
    {
        Directory.CreateDirectory(_dir);
        var userFile = Path.Combine(_dir, "my-custom.png");
        File.WriteAllBytes(userFile, new byte[] { 0x89, 0x50, 0x4E, 0x47 });

        var result = EmbeddedStarterIconExtractor.EnsureExtracted(_dir);

        Assert.False(result.Extracted);
        Assert.Equal("user-files-present", result.SkipReason);
        Assert.True(File.Exists(userFile));
        // No embedded files should have been written alongside.
        Assert.False(File.Exists(Path.Combine(_dir, "boss@32.png")));
        Assert.False(File.Exists(Path.Combine(_dir, "mapping.json")));
    }

    [Fact]
    public void UserMappingJsonPresent_DoesNothing_EvenWithoutPngs()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "mapping.json"), "{}");

        var result = EmbeddedStarterIconExtractor.EnsureExtracted(_dir);

        Assert.False(result.Extracted);
        Assert.Equal("user-files-present", result.SkipReason);
    }

    [Fact]
    public void SecondCall_IsIdempotent_ReportsAlreadyExtracted()
    {
        EmbeddedStarterIconExtractor.EnsureExtracted(_dir);
        // Second call: pack is now on disk, treat as "user-files-present" (the pack is the user's now).
        var second = EmbeddedStarterIconExtractor.EnsureExtracted(_dir);
        Assert.False(second.Extracted);
        Assert.Equal("user-files-present", second.SkipReason);
    }
}