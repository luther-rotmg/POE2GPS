using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace POE2Radar.Overlay.Web;

// v0.35 Stream-Safe Overlay Mode — options passed from ApiServer to AssetHost.ServeObs when
// ?mode=safe is on. Kept as a record so equality-by-value drives the transform ETag suffix.
public sealed record SafeModeOptions(int DelaySec, bool MaskZoneName, bool HideoutBlur, bool EntityNameFog);

public sealed class AssetHost
{
    const string ResourcePrefix = "POE2Radar.Overlay.Web.Assets.";

    static readonly Assembly Asm = typeof(AssetHost).Assembly;
    readonly ConcurrentDictionary<string, byte[]?> _cache = new();
    readonly ConcurrentDictionary<string, string> _etags = new();

    // --- Public serve API ---

    public void ServeMap(HttpListenerContext ctx)   => WriteAsset(ctx, "map.html", "text/html; charset=utf-8", null);
    public void ServeObs(HttpListenerContext ctx) => ServeObs(ctx, null);
    public void ServeObs(HttpListenerContext ctx, SafeModeOptions? safe)
    {
        Func<byte[], byte[]> transform = safe == null ? ObsTransform : SafeObsTransformFactory(safe);
        // Different transform must produce a different ETag suffix — WriteAsset keys ETag on (name + suffix).
        string suffix = safe == null ? "@obs" : $"@obs-safe-{safe.DelaySec}-{Bit(safe.MaskZoneName)}{Bit(safe.HideoutBlur)}{Bit(safe.EntityNameFog)}";
        WriteAssetWithEtagSuffix(ctx, "map.html", "text/html; charset=utf-8", transform, suffix);
    }

    static int Bit(bool b) => b ? 1 : 0;

    public void ServeAsset(HttpListenerContext ctx, string relativeName)
    {
        var (mime, transform) = MimeForAsset(relativeName);
        if (mime == null) { NotFound(ctx); return; }
        WriteAsset(ctx, relativeName, mime, transform);
    }

    // --- Internals ---

    static byte[] ObsTransform(byte[] original)
    {
        var text = Encoding.UTF8.GetString(original);
        var replaced = text.Replace("<body>", "<body class=\"obs\">");
        return Encoding.UTF8.GetBytes(replaced);
    }

    static Func<byte[], byte[]> SafeObsTransformFactory(SafeModeOptions o) => original =>
    {
        var text = Encoding.UTF8.GetString(original);
        var attrs = $"class=\"obs safe-mode\" data-safe-delay-sec=\"{o.DelaySec}\" data-safe-mask-zone=\"{Bit(o.MaskZoneName)}\" data-safe-hideout-blur=\"{Bit(o.HideoutBlur)}\" data-safe-entity-name-fog=\"{Bit(o.EntityNameFog)}\"";
        var replaced = text.Replace("<body>", $"<body {attrs}>");
        return Encoding.UTF8.GetBytes(replaced);
    };

    static (string? mime, Func<byte[], byte[]>? transform) MimeForAsset(string name) => name switch
    {
        "map.css"          => ("text/css; charset=utf-8", null),
        "map.js"           => ("application/javascript; charset=utf-8", null),
        "atlas-icons.json" => ("application/json; charset=utf-8", null),
        _                  => (null, null),
    };

    byte[]? Load(string name) => _cache.GetOrAdd(name, LoadFromAssembly)!;

    static byte[]? LoadFromAssembly(string name)
    {
        var resource = ResourcePrefix + name;
        using var s = Asm.GetManifestResourceStream(resource);
        if (s == null) return null;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    string EtagFor(string name, byte[] payload) => _etags.GetOrAdd(name, _ =>
    {
        using var sha = SHA1.Create();
        return "\"sha1-" + Convert.ToHexString(sha.ComputeHash(payload)) + "\"";
    });

    void WriteAssetWithEtagSuffix(HttpListenerContext ctx, string name, string mime, Func<byte[], byte[]>? transform, string etagSuffix)
    {
        var raw = Load(name);
        if (raw == null) { NotFound(ctx); return; }
        var payload = transform == null ? raw : transform(raw);
        var etag = EtagFor(name + etagSuffix, payload);

        if (ctx.Request.Headers["If-None-Match"] == etag)
        {
            ctx.Response.StatusCode = 304;
            ctx.Response.Close();
            return;
        }

        ctx.Response.ContentType = mime;
        ctx.Response.Headers["ETag"] = etag;
        ctx.Response.ContentLength64 = payload.Length;
        ctx.Response.OutputStream.Write(payload, 0, payload.Length);
        ctx.Response.OutputStream.Close();
    }

    void WriteAsset(HttpListenerContext ctx, string name, string mime, Func<byte[], byte[]>? transform)
        => WriteAssetWithEtagSuffix(ctx, name, mime, transform, transform == null ? "" : "@obs");

    static void NotFound(HttpListenerContext ctx)
    {
        ctx.Response.StatusCode = 404;
        ctx.Response.Close();
    }

    // Test hooks (internal — visible only to POE2Radar.Tests).
    internal byte[]? LoadForTest(string name) => Load(name);
    internal string  ObsHtmlForTest() => Encoding.UTF8.GetString(ObsTransform(Load("map.html")!));
}
