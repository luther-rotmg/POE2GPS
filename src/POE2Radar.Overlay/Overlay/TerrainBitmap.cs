using System.Runtime.InteropServices;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace POE2Radar.Overlay;

/// <summary>
/// Direct2D bitmap of the walkable terrain mask, built once per area. One pixel per grid
/// cell — alpha = walkability. Cache key is (width, height, areaHash) — two maps can
/// share dimensions, so dimension-only keying would silently keep the previous map's
/// terrain after a transition.
/// </summary>
public sealed class TerrainBitmap : IDisposable
{
    /// <summary>Resolved interior + edge colors (BGRA bytes) the bitmap was baked with. A value-equality
    /// record so a live color/opacity tweak invalidates the cached bitmap and forces a rebuild.</summary>
    public readonly record struct TerrainStyle(byte IB, byte IG, byte IR, byte IA, byte EB, byte EG, byte ER, byte EA);

    private readonly ID2D1RenderTarget _renderTarget;
    private ID2D1Bitmap? _bitmap;
    private int _builtForWidth;
    private int _builtForHeight;
    private uint _builtForAreaHash;
    private TerrainStyle _builtForStyle;

    public TerrainBitmap(ID2D1RenderTarget renderTarget)
    {
        _renderTarget = renderTarget;
    }

    public ID2D1Bitmap? Bitmap => _bitmap;
    public int Width  => _builtForWidth;
    public int Height => _builtForHeight;
    public uint AreaHash => _builtForAreaHash;

    /// <summary>
    /// Build (or rebuild) from a flat 0/1 walkable array. Cheap when dimensions +
    /// <paramref name="areaHash"/> match the cached bitmap. <paramref name="inTransition"/> forces
    /// an immediate drop (the area's hash may briefly persist while a zone is loading).
    /// </summary>
    public void EnsureBuiltRaw(byte[] walkable, int width, int height, uint areaHash, bool inTransition, TerrainStyle style)
    {
        if (_bitmap is not null && (inTransition || areaHash != _builtForAreaHash || !style.Equals(_builtForStyle)))
        {
            _bitmap.Dispose(); _bitmap = null; _builtForAreaHash = 0;
        }
        if (inTransition || width <= 0 || height <= 0) return;
        if (_bitmap is not null && width == _builtForWidth && height == _builtForHeight
            && areaHash == _builtForAreaHash && style.Equals(_builtForStyle)) return;
        BuildFrom(walkable, width, height, areaHash, style);
    }

    private void BuildFrom(byte[] walkable, int w, int h, uint areaHash, TerrainStyle style)
    {
        var pixels = new byte[w * h * 4]; // BGRA

        // Render style with per-pixel alpha (colors/alpha are config-driven; defaults preserve the old look):
        //   • Walkable interior → faint wash. Reads as "you can walk here" without occluding what's behind.
        //   • Wall edge (walkable cell adjacent to an unwalkable cell or grid boundary) → brighter outline.
        //   • Walls themselves stay alpha 0 so PoE's actual map shows through.

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var v = walkable[y * w + x];
                var idx = (y * w + x) * 4;
                if (v == 0) continue;

                var isEdge = false;
                for (var dy = -1; dy <= 1 && !isEdge; dy++)
                {
                    var ny = y + dy;
                    if (ny < 0 || ny >= h) { isEdge = true; break; }
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        var nx = x + dx;
                        if (nx < 0 || nx >= w) { isEdge = true; break; }
                        if (walkable[ny * w + nx] == 0) { isEdge = true; break; }
                    }
                }

                if (isEdge)
                {
                    pixels[idx + 0] = style.EB;   // B
                    pixels[idx + 1] = style.EG;   // G
                    pixels[idx + 2] = style.ER;   // R
                    pixels[idx + 3] = style.EA;
                }
                else
                {
                    pixels[idx + 0] = style.IB;   // B
                    pixels[idx + 1] = style.IG;   // G
                    pixels[idx + 2] = style.IR;   // R
                    pixels[idx + 3] = style.IA;
                }
            }
        }

        _bitmap?.Dispose();
        var props = new BitmapProperties(new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied));
        // Premultiply alpha so D2D blends correctly.
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var a = pixels[i + 3];
            if (a == 255) continue;
            var af = a / 255f;
            pixels[i + 0] = (byte)(pixels[i + 0] * af);
            pixels[i + 1] = (byte)(pixels[i + 1] * af);
            pixels[i + 2] = (byte)(pixels[i + 2] * af);
        }

        var size = new SizeI(w, h);
        var pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            _bitmap = _renderTarget.CreateBitmap(size, pinned.AddrOfPinnedObject(), (uint)(w * 4), props);
        }
        finally
        {
            pinned.Free();
        }
        _builtForWidth     = w;
        _builtForHeight    = h;
        _builtForAreaHash  = areaHash;
        _builtForStyle     = style;
    }

    public void Dispose()
    {
        _bitmap?.Dispose();
        _bitmap = null;
    }
}
