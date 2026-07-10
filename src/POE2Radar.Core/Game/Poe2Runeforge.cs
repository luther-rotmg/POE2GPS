using NumVec2 = System.Numerics.Vector2;

namespace POE2Radar.Core.Game;

/// <summary>
/// Reads the in-game "Runeshape Combinations" reward panel (the rune-crafting league mechanic) so the
/// overlay can price each reward. Ported from upstream reference Atlas plugin and validated live
/// 2026-06-14 (Research <c>--runeforge</c>). Read-only.
///
/// <para>The panel is located by a UI-FLAGS-FINGERPRINT walk with BACKTRACKING from GameUi
/// (<c>Ptr(InGameState + <see cref="Poe2Offsets.InGameState.UiRoot"/>)</c> — the UiRootStruct the game
/// treats as a UiElement): child indices drift across patches/restarts, but each element's Flags "role"
/// bits are stable, so we match <c>(flags &amp; ~visibleBit) == fingerprint</c>, trying visible siblings
/// first and keeping whichever branch bottoms out at a real recipes-container. The gate step
/// (<see cref="Poe2Offsets.Runeforge.GateStep"/>) only accepts a VISIBLE match, so the whole walk fails
/// when the panel is closed. See <see cref="Poe2Offsets.Runeforge"/> for the fingerprints + offsets.</para>
///
/// <para>Each visible row's <c>kid[0]</c> holds an inline std::wstring "&lt;count&gt;x &lt;name&gt;" at
/// <see cref="Poe2Offsets.Runeforge.NameWString"/>. Screen rects are computed via the upstream reference
/// UiElementBase math (parent-chain unscaled position × resolution scale).</para>
/// </summary>
public sealed class Poe2Runeforge
{
    private readonly MemoryReader _reader;

    private nint _panel;                 // cached recipes-container (0 = not resolved / panel closed)
    private nint _viewport;              // cached scrollable viewport element (holds the scroll offset)
    private DateTime _nextResolveUtc = DateTime.MinValue;
    private const int ResolveThrottleMs = 120; // the fingerprint walk is cheap, but no need every frame

    public Poe2Runeforge(MemoryReader reader) => _reader = reader;

    /// <summary>One visible reward row: parsed count + name, and its SCREEN-space rect (already scaled
    /// for the given window size). Price lookup + drawing happen overlay-side.</summary>
    public readonly record struct RuneReward(int Count, string Name, float X, float Y, float W, float H);

    /// <summary>True on the last <see cref="ReadRewards"/> call the panel was open + resolved.</summary>
    public bool PanelOpen { get; private set; }

    /// <summary>Resolve the panel (cached, throttled) and read the visible reward rows with screen rects
    /// for a window of <paramref name="winW"/>×<paramref name="winH"/> pixels. Empty when the panel is
    /// closed / not in the rune-crafting UI. Cheap when closed (the walk bails at the visible-gate step).</summary>
    public List<RuneReward> ReadRewards(nint inGameState, float winW, float winH)
    {
        PanelOpen = false;
        var result = new List<RuneReward>();
        var gameUi = Ptr(inGameState + Poe2.InGameState.UiRoot);
        if (gameUi == 0) { _panel = 0; return result; }

        // Re-resolve on a throttle, or immediately when we have no cached panel (so it appears promptly
        // on open). Resolution returns 0 when the gate element is hidden — i.e. the panel is closed —
        // and the panel UiElement is recreated on close/reopen, so a periodic re-walk self-heals a stale
        // cache. Between walks the cached recipes-container is reused (only the rows are re-read).
        var now = DateTime.UtcNow;
        if (_panel == 0 || now >= _nextResolveUtc)
        {
            _nextResolveUtc = now.AddMilliseconds(ResolveThrottleMs);
            _viewport = 0;
            _panel = Walk(gameUi, 0);
        }
        if (_panel == 0) return result;

        if (!Children(_panel, out var first, out var n)) { _panel = 0; return result; }
        PanelOpen = true;

        var scroll = ReadScroll(_viewport);
        for (long i = 0; i < n; i++)
        {
            var row = Ptr(first + (nint)(i * 8));
            if (row == 0 || !Visible(row)) continue;          // only the enabled/visible rows are real
            var label = Child(row, 0);
            if (label == 0) continue;
            var raw = ReadStdWString(label + Poe2.Runeforge.NameWString);
            if (string.IsNullOrEmpty(raw)) continue;
            ParseNameCount(raw, out var count, out var name);
            if (!TryScreenRect(row, scroll, winW, winH, out var pos, out var size)) continue;
            result.Add(new RuneReward(count, name, pos.X, pos.Y, size.X, size.Y));
        }
        return result;
    }

    // ── panel resolution (flag-fingerprint walk with backtracking) ─────────────────────────────────

    private nint Walk(nint parent, int step)
    {
        var fps = Poe2.Runeforge.PanelFlagFingerprints;
        const uint visibleMask = 1u << Poe2.UiElement.FlagVisibleBit;
        if (step == fps.Length) return IsRecipesContainer(parent) ? parent : 0;
        if (!Children(parent, out var first, out var n)) return 0;
        var target = fps[step] & ~visibleMask;
        for (var pass = 0; pass < 2; pass++)              // visible siblings first, then invisible
        {
            var wantVisible = pass == 0;
            for (long i = 0; i < n; i++)
            {
                var child = Ptr(first + (nint)(i * 8));
                if (child == 0) continue;
                if (!_reader.TryReadStruct<uint>(child + Poe2.UiElement.Flags, out var flags)) continue;
                if ((flags & ~visibleMask) != target) continue;
                var visible = (flags & visibleMask) != 0;
                if (visible != wantVisible) continue;
                if (step == Poe2.Runeforge.GateStep && !visible) continue;  // panel-open gate
                var deeper = Walk(child, step + 1);
                if (deeper != 0)
                {
                    if (step == Poe2.Runeforge.ViewportStep) _viewport = child; // for the scroll offset
                    return deeper;
                }
            }
        }
        return 0;
    }

    private bool IsRecipesContainer(nint addr)
    {
        if (!Children(addr, out var first, out var n)) return false;
        for (long i = 0; i < n; i++)
        {
            var row = Ptr(first + (nint)(i * 8));
            if (row == 0) continue;
            var label = Child(row, 0);
            if (label != 0 && !string.IsNullOrEmpty(ReadStdWString(label + Poe2.Runeforge.NameWString))) return true;
        }
        return false;
    }

    // ── geometry (upstream reference UiElementBaseFuncs port) ──────────────────────────────────────────────

    private bool TryScreenRect(nint row, NumVec2 scroll, float winW, float winH, out NumVec2 pos, out NumVec2 size)
    {
        pos = default; size = default;
        if (!_reader.TryReadStruct<byte>(row + Poe2.UiElement.ScaleIndex, out var idx)) return false;
        _reader.TryReadStruct<float>(row + Poe2.UiElement.LocalScaleMul, out var mul);
        _reader.TryReadStruct<float>(row + Poe2.UiElement.SizeW, out var uw);
        _reader.TryReadStruct<float>(row + Poe2.UiElement.SizeH, out var uh);
        var (sw, sh) = ScaleValue(idx, mul, winW, winH);
        if (sw <= 0f || sh <= 0f) return false;
        var p = UnscaledPos(row, 0, scroll, winW, winH);
        if (!float.IsFinite(p.X) || !float.IsFinite(p.Y)) return false;
        pos = new NumVec2(p.X * sw, p.Y * sh);
        size = new NumVec2(uw * sw, uh * sh);
        return size.X > 1f && size.Y > 1f;
    }

    /// <summary>v1 = winW/2560, v2 = winH/1600; ScaleIndex selects which axis scale(s) apply (1→(v1,v1),
    /// 2→(v2,v2), 3→(v1,v2), else uniform <paramref name="mul"/>). Mirrors upstream reference ScaleValue.</summary>
    private static (float w, float h) ScaleValue(byte idx, float mul, float winW, float winH)
    {
        if (mul == 0f) mul = 1f;
        var v1 = winW / (float)Poe2.UiElement.BaseResW;
        var v2 = winH / (float)Poe2.UiElement.BaseResH;
        float w = mul, h = mul;
        switch (idx)
        {
            case 1: w *= v1; h *= v1; break;
            case 2: w *= v2; h *= v2; break;
            case 3: w *= v1; h *= v2; break;
        }
        return (w, h);
    }

    private NumVec2 UnscaledPos(nint el, int depth, NumVec2 scroll, float winW, float winH)
    {
        _reader.TryReadStruct<float>(el + Poe2.UiElement.RelativePos, out var lx);
        _reader.TryReadStruct<float>(el + Poe2.UiElement.RelativePos + 4, out var ly);
        var local = new NumVec2(lx, ly);
        var parent = Ptr(el + Poe2.UiElement.Parent);
        if (parent == 0 || depth >= 64) return local;

        var parentPos = UnscaledPos(parent, depth + 1, scroll, winW, winH);

        if (_reader.TryReadStruct<uint>(el + Poe2.UiElement.Flags, out var flags)
            && (flags & (1u << Poe2.UiElement.FlagModifyPosBit)) != 0)
        {
            _reader.TryReadStruct<float>(el + Poe2.UiElement.PositionModifier, out var mx);
            _reader.TryReadStruct<float>(el + Poe2.UiElement.PositionModifier + 4, out var my);
            parentPos += new NumVec2(mx, my);
        }
        if (parent == _viewport) parentPos += scroll;

        // If the element shares its parent's scale, positions just add; otherwise rescale the parent
        // position into this element's scale space (upstream reference TryGetUnscaledPosition).
        _reader.TryReadStruct<byte>(el + Poe2.UiElement.ScaleIndex, out var elIdx);
        _reader.TryReadStruct<float>(el + Poe2.UiElement.LocalScaleMul, out var elMul);
        _reader.TryReadStruct<byte>(parent + Poe2.UiElement.ScaleIndex, out var pIdx);
        _reader.TryReadStruct<float>(parent + Poe2.UiElement.LocalScaleMul, out var pMul);
        if (pIdx == elIdx && pMul == elMul) return parentPos + local;

        var (psw, psh) = ScaleValue(pIdx, pMul, winW, winH);
        var (msw, msh) = ScaleValue(elIdx, elMul, winW, winH);
        if (msw == 0f || msh == 0f) return parentPos + local;
        return new NumVec2(parentPos.X * psw / msw + local.X, parentPos.Y * psh / msh + local.Y);
    }

    private NumVec2 ReadScroll(nint viewport)
    {
        if (viewport == 0) return NumVec2.Zero;
        _reader.TryReadStruct<float>(viewport + Poe2.Runeforge.ScrollOffset, out var x);
        _reader.TryReadStruct<float>(viewport + Poe2.Runeforge.ScrollOffset + 4, out var y);
        return new NumVec2(x, y);
    }

    // ── primitives ────────────────────────────────────────────────────────────────────────────────

    private static void ParseNameCount(string raw, out int count, out string name)
    {
        count = 1; name = raw?.Trim() ?? "";
        if (name.Length == 0) return;
        var i = 0;
        while (i < name.Length && char.IsDigit(name[i])) i++;
        if (i > 0 && i < name.Length && (name[i] == 'x' || name[i] == 'X')
            && int.TryParse(name.AsSpan(0, i), out var c) && c > 0)
        { count = c; name = name[(i + 1)..].TrimStart(); }
    }

    private bool Visible(nint el)
        => _reader.TryReadStruct<uint>(el + Poe2.UiElement.Flags, out var f)
           && (f & (1u << Poe2.UiElement.FlagVisibleBit)) != 0;

    private bool Children(nint el, out nint first, out long n)
    {
        first = Ptr(el + Poe2.UiElement.Children); n = 0;
        if (first == 0) return false;
        if (!_reader.TryReadStruct<nint>(el + Poe2.UiElement.ChildrenEnd, out var last)) return false;
        n = ((long)last - (long)first) / 8;
        return n is > 0 and <= 4000;
    }

    private nint Child(nint el, int index)
        => Children(el, out var first, out var n) && index >= 0 && index < n
            ? Ptr(first + (nint)(index * 8)) : 0;

    private string ReadStdWString(nint addr)
    {
        if (!_reader.TryReadStruct<int>(addr + 0x10, out var len) || len <= 0 || len > 1024) return "";
        if (len < 8) return _reader.ReadStringUtf16(addr, len);
        var ptr = Ptr(addr);
        return ptr == 0 ? "" : _reader.ReadStringUtf16(ptr, len);
    }

    private nint Ptr(nint addr)
    {
        if (!_reader.TryReadStruct<nint>(addr, out var p)) return 0;
        var u = (ulong)p;
        return (u < 0x10000 || u > 0x7FFFFFFFFFFF) ? 0 : p;
    }
}
