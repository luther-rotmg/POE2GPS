using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace POE2Radar.Overlay.Web;

public sealed class AssetHost
{
    const string ResourcePrefix = "POE2Radar.Overlay.Web.Assets.";

    static readonly Assembly Asm = typeof(AssetHost).Assembly;
    readonly ConcurrentDictionary<string, byte[]?> _cache = new();
    readonly ConcurrentDictionary<string, string> _etags = new();

    // --- Public serve API ---

    public void ServeMap(HttpListenerContext ctx)   => WriteAsset(ctx, "map.html", "text/html; charset=utf-8", null);
    public void ServeObs(HttpListenerContext ctx)   => WriteAsset(ctx, "map.html", "text/html; charset=utf-8", ObsTransform);

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

    void WriteAsset(HttpListenerContext ctx, string name, string mime, Func<byte[], byte[]>? transform)
    {
        var raw = Load(name);
        if (raw == null) { NotFound(ctx); return; }
        var payload = transform == null ? raw : transform(raw);
        var etag = EtagFor(name + (transform == null ? "" : "@obs"), payload);

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

    static void NotFound(HttpListenerContext ctx)
    {
        ctx.Response.StatusCode = 404;
        ctx.Response.Close();
    }

    // Test hooks (internal — visible only to POE2Radar.Tests).
    internal byte[]? LoadForTest(string name) => Load(name);
    internal string  ObsHtmlForTest() => Encoding.UTF8.GetString(ObsTransform(Load("map.html")!));
}
