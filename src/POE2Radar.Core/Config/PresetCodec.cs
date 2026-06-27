using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace POE2Radar.Core.Config;

/// <summary>Encodes a preset JSON string to a copy-paste share-code and back. Pure + unit-tested.</summary>
public static class PresetCodec
{
    public const string Prefix = "POE2GPS-";

    public static string Encode(string json)
    {
        var raw = Encoding.UTF8.GetBytes(json);
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true)) gz.Write(raw, 0, raw.Length);
        return Prefix + Base64Url(ms.ToArray());
    }

    public static bool TryDecode(string code, out string json)
    {
        json = "";
        if (string.IsNullOrWhiteSpace(code)) return false;
        var body = code.Trim();
        if (body.StartsWith(Prefix, StringComparison.Ordinal)) body = body[Prefix.Length..];
        try
        {
            using var inMs = new MemoryStream(FromBase64Url(body));
            using var gz = new GZipStream(inMs, CompressionMode.Decompress);
            using var outMs = new MemoryStream();
            gz.CopyTo(outMs);
            json = Encoding.UTF8.GetString(outMs.ToArray());
            return json.Length > 0;
        }
        catch { return false; }
    }

    static string Base64Url(byte[] b) => Convert.ToBase64String(b).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    static byte[] FromBase64Url(string s)
    {
        var t = s.Replace('-', '+').Replace('_', '/');
        t += (t.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        return Convert.FromBase64String(t);
    }
}
