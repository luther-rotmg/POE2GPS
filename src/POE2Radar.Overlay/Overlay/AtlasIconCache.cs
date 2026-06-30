using System.Reflection;
using System.Runtime.InteropServices;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.WIC;

namespace POE2Radar.Overlay;

/// <summary>
/// Decodes the embedded Atlas content-icon PNGs (the in-game content art — Breach/Boss/Essence/…) once
/// into device-independent premultiplied-BGRA pixels (via WIC), and lazily materializes an
/// <see cref="ID2D1Bitmap"/> per icon on first draw, cached for the renderer's lifetime. Used by
/// <see cref="OverlayRenderer"/> to stamp content icons above tracked / fogged atlas nodes (#5).
///
/// <para>The overlay's render target is a stable <c>ID2D1DCRenderTarget</c> (it binds a DC per frame
/// rather than living on a swapchain), so bitmaps created from it stay valid across window resizes —
/// no device-loss recreation dance needed, same as <see cref="TerrainBitmap"/>.</para>
/// </summary>
public sealed class AtlasIconCache : IDisposable
{
    private readonly record struct Decoded(byte[] Bgra, int W, int H);

    // basename (e.g. "AtlasIconContentBreach") → decoded pixels (device-independent) and → D2D bitmap (lazy).
    private readonly Dictionary<string, Decoded> _decoded = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ID2D1Bitmap?> _bitmaps = new(StringComparer.OrdinalIgnoreCase);

    public AtlasIconCache() => LoadEmbedded();

    /// <summary>Number of icons decoded (0 ⇒ none embedded / WIC unavailable).</summary>
    public int Count => _decoded.Count;

    private void LoadEmbedded()
    {
        try
        {
            using var factory = new IWICImagingFactory();
            var asm = Assembly.GetExecutingAssembly();
            const string marker = ".AtlasIcons.";
            foreach (var res in asm.GetManifestResourceNames())
            {
                var idx = res.IndexOf(marker, StringComparison.Ordinal);
                if (idx < 0 || !res.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) continue;
                var basename = res[(idx + marker.Length)..^4]; // strip the trailing ".png"
                using var s = asm.GetManifestResourceStream(res);
                if (s == null) continue;
                if (DecodePng(factory, s) is { } d) _decoded[basename] = d;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"AtlasIconCache load failed: {ex.Message}");
        }
    }

    private static Decoded? DecodePng(IWICImagingFactory factory, Stream s)
    {
        try
        {
            using var decoder = factory.CreateDecoderFromStream(s, DecodeOptions.CacheOnLoad);
            using var frame = decoder.GetFrame(0);
            using var conv = factory.CreateFormatConverter();
            // 32bpp premultiplied BGRA matches the render target's premultiplied alpha mode below.
            conv.Initialize(frame, Vortice.WIC.PixelFormat.Format32bppPBGRA);
            conv.GetSize(out var uw, out var uh);
            int w = (int)uw, h = (int)uh;
            if (w <= 0 || h <= 0 || w > 4096 || h > 4096) return null;
            var stride = (uint)(w * 4);
            var buf = new byte[stride * h];
            var pinned = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try { conv.CopyPixels(new RectI(0, 0, w, h), stride, (uint)buf.Length, pinned.AddrOfPinnedObject()); }
            finally { pinned.Free(); }
            return new Decoded(buf, w, h);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The icon bitmap for a content asset basename, materialized on the given render target and
    /// cached (incl. a negative cache for missing/failed icons). Null when the basename has no PNG.</summary>
    public ID2D1Bitmap? Get(ID2D1RenderTarget rt, string? basename)
    {
        if (string.IsNullOrEmpty(basename)) return null;
        if (_bitmaps.TryGetValue(basename, out var cached)) return cached;
        if (!_decoded.TryGetValue(basename, out var d)) { _bitmaps[basename] = null; return null; }

        ID2D1Bitmap? bmp = null;
        var props = new BitmapProperties(new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied));
        var pinned = GCHandle.Alloc(d.Bgra, GCHandleType.Pinned);
        try { bmp = rt.CreateBitmap(new SizeI(d.W, d.H), pinned.AddrOfPinnedObject(), (uint)(d.W * 4), props); }
        catch { bmp = null; }
        finally { pinned.Free(); }
        _bitmaps[basename] = bmp;
        return bmp;
    }

    public void Dispose()
    {
        foreach (var b in _bitmaps.Values) b?.Dispose();
        _bitmaps.Clear();
    }
}
