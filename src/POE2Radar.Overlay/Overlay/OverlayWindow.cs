using System.Runtime.InteropServices;
using POE2Radar.Overlay.Native;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace POE2Radar.Overlay;

/// <summary>
/// Transparent, click-through, always-on-top overlay window with per-pixel alpha. Renders
/// via Direct2D into a DIB-section-backed memory DC, then pushes the result to the layered
/// window via <c>UpdateLayeredWindow</c> with <c>ULW_ALPHA</c>. Every pixel can be drawn at
/// arbitrary alpha 0-255 and composites correctly with PoE underneath.
///
/// <para>Replaces the old chroma-key (LWA_COLORKEY) approach which couldn't represent
/// partial transparency — pixels were either 100% opaque or exactly-magenta-keyed-out, with
/// nothing in between, so any low-alpha draw produced "near magenta" pixels that rendered
/// as solid pink.</para>
/// </summary>
public sealed class OverlayWindow : IDisposable
{
    /// <summary>
    /// Compatibility shim for renderer code that used to clear with a chroma key. With
    /// per-pixel alpha we clear with fully-transparent black instead.
    /// </summary>
    public static readonly Color4 ChromaKey = new(0f, 0f, 0f, 0f);

    private nint _hwnd;
    private nint _hInstance;
    private OverlayNative.WndProc _wndProcDelegate = null!;

    // Click-through state. The window is created WS_EX_TRANSPARENT (clicks pass through to PoE).
    // RadarApp flips this only while the cursor is over a clickable overlay region (the legend).
    // WS_EX_NOACTIVATE is set permanently so a captured click never steals focus from PoE.
    private bool _clickThrough = true;

    /// <summary>
    /// Raised on WM_LBUTTONDOWN with the click's CLIENT coordinates (x, y). The window only
    /// receives this message while it is NOT click-through (see <see cref="SetClickThrough"/>),
    /// i.e. only while the cursor is over a clickable overlay region. Purely local UI — nothing
    /// is ever sent to the game.
    /// </summary>
    public Action<int, int>? OnClientClick;

    private const uint WmTrayCallback = OverlayNative.WM_APP + 1;
    private const uint MenuExitId = 1;
    private bool _trayAdded;

    private ID2D1Factory? _d2dFactory;
    private IDWriteFactory? _dwriteFactory;
    private ID2D1DCRenderTarget? _renderTarget;

    // GDI plumbing for the DIB-backed compositor.
    private nint _memDC;
    private nint _dibSection;
    private nint _dibSectionPrev; // previously-selected object in memDC

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int OriginX { get; private set; }
    public int OriginY { get; private set; }
    public bool IsValid => _hwnd != 0 && _renderTarget != null;

    /// <summary>The overlay's window handle (for client-coord conversion of the cursor, etc.).</summary>
    public nint Handle => _hwnd;

    /// <summary>
    /// The render target. Same shape as before (ID2D1RenderTarget) but backed by the DIB
    /// section instead of an HWND. Renderer code doesn't have to change.
    /// </summary>
    public ID2D1RenderTarget RenderTarget => _renderTarget!;
    public IDWriteFactory DWriteFactory => _dwriteFactory!;

    private OverlayWindow() { }

    public static OverlayWindow Create()
    {
        var ow = new OverlayWindow();
        ow.Initialize();
        return ow;
    }

    private string _className = "POE2RadarOverlay";

    private void Initialize()
    {
        // Opt-in (--stealth): randomize the window class + title so they carry no identifying tokens.
        if (Stealth.Enabled)
            _className = "wc_" + Path.GetRandomFileName().Replace(".", "");
        _hInstance = OverlayNative.GetModuleHandleW(null);
        _wndProcDelegate = WndProc;
        RegisterWindowClass();
        CreateOverlayHwnd();
        AddTrayIcon();
        InitializeDirect2D();
    }

    /// <summary>Add a system-tray icon so users have an obvious way to quit (right-click → Exit).</summary>
    private void AddTrayIcon()
    {
        var nid = new OverlayNative.NOTIFYICONDATAW
        {
            cbSize           = (uint)Marshal.SizeOf<OverlayNative.NOTIFYICONDATAW>(),
            hWnd             = _hwnd,
            uID              = 1,
            uFlags           = OverlayNative.NIF_MESSAGE | OverlayNative.NIF_ICON | OverlayNative.NIF_TIP,
            uCallbackMessage = WmTrayCallback,
            hIcon            = OverlayNative.LoadIconW(0, OverlayNative.IDI_APPLICATION),
            szTip            = "POE2Radar — right-click to Exit",
            szInfo           = "",
            szInfoTitle      = "",
        };
        _trayAdded = OverlayNative.Shell_NotifyIconW(OverlayNative.NIM_ADD, ref nid);
    }

    private void RemoveTrayIcon()
    {
        if (!_trayAdded) return;
        var nid = new OverlayNative.NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<OverlayNative.NOTIFYICONDATAW>(),
            hWnd = _hwnd, uID = 1, szTip = "", szInfo = "", szInfoTitle = "",
        };
        OverlayNative.Shell_NotifyIconW(OverlayNative.NIM_DELETE, ref nid);
        _trayAdded = false;
    }

    private void ShowTrayMenu()
    {
        var menu = OverlayNative.CreatePopupMenu();
        if (menu == 0) return;
        OverlayNative.AppendMenuW(menu, OverlayNative.MF_STRING, MenuExitId, "Exit POE2Radar");
        OverlayNative.GetCursorPos(out var pt);
        OverlayNative.SetForegroundWindow(_hwnd); // standard tray-menu idiom so it dismisses correctly
        var cmd = OverlayNative.TrackPopupMenu(menu,
            OverlayNative.TPM_RIGHTBUTTON | OverlayNative.TPM_RETURNCMD, pt.X, pt.Y, 0, _hwnd, 0);
        OverlayNative.DestroyMenu(menu);
        if (cmd == (int)MenuExitId) OverlayNative.PostQuitMessage(0);
    }

    private unsafe void RegisterWindowClass()
    {
        var className = _className + "\0";
        fixed (char* pName = className)
        {
            var wc = new OverlayNative.WNDCLASSEXW
            {
                cbSize        = (uint)Marshal.SizeOf<OverlayNative.WNDCLASSEXW>(),
                style         = OverlayNative.CS_HREDRAW | OverlayNative.CS_VREDRAW,
                lpfnWndProc   = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
                hInstance     = _hInstance,
                lpszClassName = (nint)pName,
            };
            OverlayNative.RegisterClassExW(&wc);
        }
    }

    private void CreateOverlayHwnd()
    {
        var exStyle = OverlayNative.WS_EX_TOPMOST
                    | OverlayNative.WS_EX_TRANSPARENT
                    | OverlayNative.WS_EX_LAYERED
                    | OverlayNative.WS_EX_NOACTIVATE
                    | OverlayNative.WS_EX_TOOLWINDOW;

        var wndTitle = Stealth.Enabled ? "wt_" + Path.GetRandomFileName().Replace(".", "") : "POE2RadarOverlay";
        _hwnd = OverlayNative.CreateWindowExW(
            exStyle,
            _className,
            wndTitle,
            OverlayNative.WS_POPUP | OverlayNative.WS_VISIBLE,
            0, 0, 800, 600,
            0, 0, _hInstance, 0);

        if (_hwnd == 0)
            throw new InvalidOperationException("CreateWindowExW failed");

        // No SetLayeredWindowAttributes — we compose via UpdateLayeredWindow each frame.
        OverlayNative.ShowWindow(_hwnd, OverlayNative.SW_SHOW);
    }

    private void InitializeDirect2D()
    {
        _d2dFactory    = D2D1.D2D1CreateFactory<ID2D1Factory>(Vortice.Direct2D1.FactoryType.SingleThreaded);
        _dwriteFactory = DWrite.DWriteCreateFactory<IDWriteFactory>(Vortice.DirectWrite.FactoryType.Shared);

        // ID2D1DCRenderTarget renders into whatever DC we BindDC to each frame.
        var rtProps = new RenderTargetProperties(
            RenderTargetType.Default,
            new PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied),
            96, 96, RenderTargetUsage.None, FeatureLevel.Default);
        _renderTarget = _d2dFactory.CreateDCRenderTarget(rtProps);

        AllocateBackingBitmap(800, 600);
    }

    /// <summary>
    /// (Re)allocate the DIB section + memory DC at the given size and BindDC the render
    /// target. Called on first init and whenever the game window resizes.
    /// </summary>
    private void AllocateBackingBitmap(int width, int height)
    {
        FreeBackingBitmap();

        var screenDC = OverlayNative.GetDC(0);
        try
        {
            _memDC = OverlayNative.CreateCompatibleDC(screenDC);

            var bmi = new OverlayNative.BITMAPINFO
            {
                bmiHeader = new OverlayNative.BITMAPINFOHEADER
                {
                    biSize        = (uint)Marshal.SizeOf<OverlayNative.BITMAPINFOHEADER>(),
                    biWidth       = width,
                    biHeight      = -height,        // top-down DIB so D2D and Win32 agree on Y
                    biPlanes      = 1,
                    biBitCount    = 32,
                    biCompression = OverlayNative.BI_RGB,
                },
            };
            _dibSection = OverlayNative.CreateDIBSection(_memDC, ref bmi, OverlayNative.DIB_RGB_COLORS, out _, 0, 0);
            if (_dibSection == 0) throw new InvalidOperationException("CreateDIBSection failed");

            _dibSectionPrev = OverlayNative.SelectObject(_memDC, _dibSection);

            _renderTarget!.BindDC(_memDC, new Vortice.RawRect(0, 0, width, height));
        }
        finally
        {
            OverlayNative.ReleaseDC(0, screenDC);
        }

        Width  = width;
        Height = height;
    }

    private void FreeBackingBitmap()
    {
        if (_memDC != 0 && _dibSectionPrev != 0)
        {
            OverlayNative.SelectObject(_memDC, _dibSectionPrev);
            _dibSectionPrev = 0;
        }
        if (_dibSection != 0) { OverlayNative.DeleteObject(_dibSection); _dibSection = 0; }
        if (_memDC != 0)      { OverlayNative.DeleteDC(_memDC);          _memDC = 0; }
    }

    /// <summary>
    /// Push the latest D2D render to the screen via UpdateLayeredWindow with per-pixel alpha.
    /// Must be called after the renderer's <c>EndDraw</c> each frame, while the DIB is in
    /// a consistent state.
    /// </summary>
    public void Present()
    {
        if (_hwnd == 0 || _memDC == 0) return;

        OverlayNative.GdiFlush();
        var screenDC = OverlayNative.GetDC(0);
        try
        {
            var dstPos = new OverlayNative.POINT { X = OriginX, Y = OriginY };
            var size   = new OverlayNative.SIZE  { cx = Width,  cy = Height };
            var srcPos = new OverlayNative.POINT { X = 0,       Y = 0 };
            var blend  = new OverlayNative.BLENDFUNCTION
            {
                BlendOp             = OverlayNative.AC_SRC_OVER,
                BlendFlags          = 0,
                SourceConstantAlpha = 255,
                AlphaFormat         = OverlayNative.AC_SRC_ALPHA,
            };

            OverlayNative.UpdateLayeredWindow(
                _hwnd, screenDC, ref dstPos, ref size,
                _memDC, ref srcPos, 0, ref blend, OverlayNative.ULW_ALPHA);
        }
        finally
        {
            OverlayNative.ReleaseDC(0, screenDC);
        }
    }

    /// <summary>Track the PoE window's screen rect; resize backing bitmap if dimensions changed.</summary>
    public bool TrackGameWindow(nint gameHwnd)
    {
        if (gameHwnd == 0) return false;
        if (!OverlayNative.GetWindowRect(gameHwnd, out var rect)) return false;

        var w = rect.Right  - rect.Left;
        var h = rect.Bottom - rect.Top;
        if (w <= 0 || h <= 0) return false;

        if (rect.Left != OriginX || rect.Top != OriginY || w != Width || h != Height)
        {
            if (w != Width || h != Height) AllocateBackingBitmap(w, h);
            OriginX = rect.Left;
            OriginY = rect.Top;
            // Position is communicated to the OS via UpdateLayeredWindow's pptDst.
        }

        return true;
    }

    /// <summary>
    /// Toggle whether the overlay is click-through. When <paramref name="value"/> is true (default
    /// state) the window has <c>WS_EX_TRANSPARENT</c> and all clicks fall through to PoE; when false
    /// the window captures clicks (and receives <c>WM_LBUTTONDOWN</c>). <c>WS_EX_NOACTIVATE</c> stays
    /// set throughout, so capturing a click never steals keyboard focus from the game. Only touches
    /// this window's own ex-style — no input is ever sent to the game. No-ops when unchanged so we
    /// don't call SetWindowLongPtr every frame.
    /// </summary>
    public void SetClickThrough(bool value)
    {
        if (value == _clickThrough || _hwnd == 0) return;
        _clickThrough = value;

        var ex = (uint)OverlayNative.GetWindowLongPtrW(_hwnd, OverlayNative.GWL_EXSTYLE);
        ex = value
            ? ex | OverlayNative.WS_EX_TRANSPARENT
            : ex & ~OverlayNative.WS_EX_TRANSPARENT;
        OverlayNative.SetWindowLongPtrW(_hwnd, OverlayNative.GWL_EXSTYLE, (nint)ex);
    }

    public bool PumpMessages()
    {
        while (OverlayNative.PeekMessageW(out var msg, 0, 0, 0, OverlayNative.PM_REMOVE))
        {
            if (msg.message == OverlayNative.WM_QUIT) return false;
            OverlayNative.TranslateMessage(ref msg);
            OverlayNative.DispatchMessageW(ref msg);
        }
        return true;
    }

    private nint WndProc(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        if (msg == WmTrayCallback)
        {
            var ev = (uint)(lParam & 0xFFFF);
            if (ev is OverlayNative.WM_RBUTTONUP or OverlayNative.WM_LBUTTONUP) ShowTrayMenu();
            return 0;
        }
        if (msg == OverlayNative.WM_LBUTTONDOWN)
        {
            // Only delivered while NOT click-through (cursor is over a clickable overlay region).
            // lParam packs client coords: low word = x, high word = y (both signed 16-bit).
            var x = (short)(lParam & 0xFFFF);
            var y = (short)((lParam >> 16) & 0xFFFF);
            OnClientClick?.Invoke(x, y);
            return 0;
        }
        if (msg == OverlayNative.WM_DESTROY)
        {
            OverlayNative.PostQuitMessage(0);
            return 0;
        }
        return OverlayNative.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        RemoveTrayIcon();
        _renderTarget?.Dispose();
        _dwriteFactory?.Dispose();
        _d2dFactory?.Dispose();
        FreeBackingBitmap();
        if (_hwnd != 0)
        {
            OverlayNative.DestroyWindow(_hwnd);
            _hwnd = 0;
        }
    }
}
