using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using POE2Radar.Core.Icons;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.WIC;

namespace POE2Radar.Overlay.Overlay;

/// <summary>
/// Render-thread-owned cache: name -> ID2D1Bitmap, decoded on first Get() from
/// IconRegistry.Current.Icons via WIC. Invalidates and rebuilds when the registry
/// snapshot's Version bumps (file hot-reload path).
/// </summary>
public sealed class EntityIconCache : IDisposable
{
    private readonly IconRegistry _registry;
    private readonly IWICImagingFactory _wic;
    private readonly Dictionary<string, ID2D1Bitmap?> _bitmaps = new(StringComparer.OrdinalIgnoreCase);
    private int _cachedVersion = -1;
    private bool _disposed;

    public EntityIconCache(IconRegistry registry, IWICImagingFactory wic)
    {
        _registry = registry;
        _wic = wic;
    }

    public ID2D1Bitmap? Get(string name, ID2D1RenderTarget rt)
    {
        if (_disposed) return null;
        var snap = _registry.Current;
        if (snap.Version != _cachedVersion)
        {
            foreach (var b in _bitmaps.Values) b?.Dispose();
            _bitmaps.Clear();
            _cachedVersion = snap.Version;
        }
        if (_bitmaps.TryGetValue(name, out var existing))
            return existing;
        if (!snap.Icons.TryGetValue(name, out var entry))
        {
            _bitmaps[name] = null;
            return null;
        }
        try
        {
            var bmp = Decode(entry.PngBytes, rt);
            _bitmaps[name] = bmp;
            return bmp;
        }
        catch
        {
            _bitmaps[name] = null;
            return null;
        }
    }

    private ID2D1Bitmap Decode(byte[] png, ID2D1RenderTarget rt)
    {
        using var ms = new MemoryStream(png, writable: false);
        using var decoder = _wic.CreateDecoderFromStream(ms, DecodeOptions.CacheOnDemand);
        using var frame = decoder.GetFrame(0);
        using var converter = _wic.CreateFormatConverter();
        converter.Initialize(frame, Vortice.WIC.PixelFormat.Format32bppPBGRA, BitmapDitherType.None, null, 0, BitmapPaletteType.MedianCut);
        var w = frame.Size.Width;
        var h = frame.Size.Height;
        var stride = w * 4;
        var buf = new byte[stride * h];
        var pinned = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try
        {
            converter.CopyPixels(new RectI(0, 0, w, h), (uint)stride, (uint)buf.Length, pinned.AddrOfPinnedObject());
            var props = new BitmapProperties(new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied));
            return rt.CreateBitmap(new SizeI(w, h), pinned.AddrOfPinnedObject(), (uint)stride, props);
        }
        finally { pinned.Free(); }
    }

    /// <summary>
    /// Screen-space destination rect for an entity icon centered on <paramref name="center"/>
    /// with half-width <paramref name="radius"/> (matches DrawIcon's r convention). Clamped so
    /// icons never collapse to zero size when rule.Size == 0.
    /// </summary>
    public static Rect ComputeEntityIconDestRect(Vector2 center, float radius)
    {
        var r = MathF.Max(radius, 1f);
        return new Rect(center.X - r, center.Y - r, 2 * r, 2 * r);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var b in _bitmaps.Values) b?.Dispose();
        _bitmaps.Clear();
    }
}