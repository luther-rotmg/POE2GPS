using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Xunit;

namespace POE2Radar.Tests.Overlay;

public class PaletteCodecTests
{
    private static readonly string[] KEYS = new[] {
        "gold","goldBright","goldDeep","ink","inkDim","inkFaint",
        "panel","panel2","bg","bgAlt","line","lineSoft","good"
    };

    private static string ToB64Url(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static byte[] FromB64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        while (s.Length % 4 != 0) s += "=";
        return Convert.FromBase64String(s);
    }

    private static uint Fnv1a32(string ascii)
    {
        uint h = 0x811c9dc5;
        foreach (var c in ascii) { h ^= (byte)c; h *= 0x01000193; }
        return h;
    }

    private static string Checksum(string body)
    {
        var h = Fnv1a32(body);
        // Use first 6 chars of hex encoding to avoid hyphens in wire format
        return h.ToString("x8").Substring(0, 6);
    }

    private static string Encode(string name, string[] colors)
    {
        Assert.Equal(13, colors.Length);
        var payload = new Dictionary<string, object> { ["n"] = name, ["v"] = colors };
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var body = ToB64Url(Encoding.UTF8.GetBytes(json));
        return "RUNE1-" + body + "-" + Checksum(body);
    }

    private static (string name, string[] colors)? Decode(string code)
    {
        if (code == null) return null;
        var parts = code.Trim().Split('-');
        if (parts.Length != 3 || parts[0] != "RUNE1") return null;
        if (parts[2] != Checksum(parts[1])) return null;
        try
        {
            var json = Encoding.UTF8.GetString(FromB64Url(parts[1]));
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var n = root.GetProperty("n").GetString() ?? "";
            var vEl = root.GetProperty("v");
            if (vEl.GetArrayLength() != 13) return null;
            var arr = vEl.EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
            foreach (var c in arr)
                if (!System.Text.RegularExpressions.Regex.IsMatch(c, "^#[0-9a-f]{6}$")) return null;
            return (n, arr);
        }
        catch { return null; }
    }

    private static readonly string[] SAMPLE = new[] {
        "#f5c94f","#ffe680","#8a6a1a","#f0e6cf","#b0a888","#807860",
        "#141c30","#1a2540","#0d1220","#0a0f1c","#3a4260","#242a40","#66d97a"
    };

    [Fact]
    public void RoundTrip_Sample_Identity()
    {
        var code = Encode("My Forge Palette", SAMPLE);
        var back = Decode(code);
        Assert.NotNull(back);
        Assert.Equal("My Forge Palette", back!.Value.name);
        Assert.Equal(SAMPLE, back.Value.colors);
    }

    [Fact]
    public void Encode_ProducesExpectedShape()
    {
        var code = Encode("X", SAMPLE);
        var parts = code.Split('-');
        Assert.Equal(3, parts.Length);
        Assert.Equal("RUNE1", parts[0]);
        Assert.Equal(6, parts[2].Length);
        Assert.Matches("^[A-Za-z0-9_-]+$", parts[1]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("RUNE1-abc")]
    [InlineData("RUNE0-abc-def123")]
    [InlineData("RUNE1-!!not-b64!!-aaaaaa")]
    public void Decode_Malformed_ReturnsNull(string bad) => Assert.Null(Decode(bad));

    [Fact]
    public void Decode_TamperedChecksum_ReturnsNull()
    {
        var code = Encode("T", SAMPLE);
        var parts = code.Split('-');
        var tampered = parts[0] + "-" + parts[1] + "-" + "AAAAAA";
        Assert.Null(Decode(tampered));
    }

    [Fact]
    public void Decode_TamperedBody_ChecksumCatchesIt()
    {
        var code = Encode("T", SAMPLE);
        var parts = code.Split('-');
        var flipped = parts[1].Substring(0, parts[1].Length - 1) + (parts[1][^1] == 'A' ? 'B' : 'A');
        Assert.Null(Decode(parts[0] + "-" + flipped + "-" + parts[2]));
    }

    [Fact]
    public void Fuzz_100_Random_RoundTrip()
    {
        var rng = new Random(1234);
        for (int i = 0; i < 100; i++)
        {
            var arr = Enumerable.Range(0, 13).Select(_ => "#" + rng.Next(0, 0xFFFFFF).ToString("x6")).ToArray();
            var name = "P" + i;
            var back = Decode(Encode(name, arr));
            Assert.NotNull(back);
            Assert.Equal(name, back!.Value.name);
            Assert.Equal(arr, back.Value.colors);
        }
    }

    [Fact]
    public void PaletteCodecJs_FileExists_AndDeclaresMatchingInvariants()
    {
        var repoRoot = RepoRoot();
        var path = Path.Combine(repoRoot, "src", "POE2Radar.Overlay", "Web", "Assets", "paletteCodec.js");
        Assert.True(File.Exists(path), $"paletteCodec.js not found at {path}");
        var src = File.ReadAllText(path);
        Assert.Contains("'RUNE1'", src);
        Assert.Contains("0x811c9dc5", src);
        Assert.Contains("0x01000193", src);
        foreach (var k in KEYS) Assert.Contains("'" + k + "'", src);
        Assert.Contains("global.__paletteCodec", src);
        Assert.Contains("function encode", src);
        Assert.Contains("function decode", src);
        Assert.Contains("h.toString(16)", src); // hex encoding for checksum
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "POE2Radar.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
