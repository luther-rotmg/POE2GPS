using POE2Radar.Core.Game;
using POE2Radar.Core.Native;

namespace POE2Radar.Core.Cheats;

public sealed class CheatManager
{
    private readonly ProcessHandle _process;
    private readonly MemoryReader _reader;
    private readonly Dictionary<string, CheatState> _cheats = new();

    private sealed class CheatState
    {
        public CheatDefinition Definition { get; init; } = null!;
        public nint Address { get; set; }
        public nint ConstantAddress { get; set; }
        public byte[]? OriginalBytes { get; set; }
        public bool Active { get; set; }
        public float CurrentValue { get; set; }
        public bool Found => Address != 0;
    }

    public CheatManager(ProcessHandle process, MemoryReader reader)
    {
        _process = process;
        _reader = reader;
    }

    public void ScanAndResolve()
    {
        var sections = AobScanner.ReadExecutableSections(_process, _reader);
        var definitions = CheatDefinition.All();

        foreach (var def in definitions)
        {
            var state = new CheatState { Definition = def, CurrentValue = def.ConstantDefault };
            _cheats[def.Name] = state;

            foreach (var (sectionBase, bytes) in sections)
            {
                var matches = AobScanner.FindPattern(bytes, def.Pattern);
                if (matches.Count == 0) continue;

                state.Address = sectionBase + matches[0] + def.TargetOffset;

                if (def.Type == CheatType.PatchConstant)
                    ResolveConstantAddress(state, sectionBase, bytes, matches[0]);

                Console.WriteLine($"  cheat [{def.ShortName}] found at 0x{state.Address:X}");
                break;
            }

            if (!state.Found)
                Console.WriteLine($"  cheat [{def.ShortName}] pattern not found");
        }
    }

    private void ResolveConstantAddress(CheatState state, nint sectionBase, byte[] sectionBytes, int matchOffset)
    {
        var def = state.Definition;
        var instrAddr = sectionBase + matchOffset + def.TargetOffset;
        var dispPos = matchOffset + def.TargetOffset + def.RipDispOffset;

        if (dispPos + 4 > sectionBytes.Length) return;

        var ripOffset = BitConverter.ToInt32(sectionBytes, dispPos);
        var candidate = instrAddr + def.RipInstrLen + ripOffset;

        var probe = ReadBytes(candidate, 4);
        if (probe == null)
        {
            Console.WriteLine($"    constant at 0x{candidate:X} — UNREADABLE, skipping");
            return;
        }

        var origFloat = BitConverter.ToSingle(probe, 0);
        if (float.IsNaN(origFloat) || float.IsInfinity(origFloat) || origFloat < -1e9f || origFloat > 1e9f)
        {
            Console.WriteLine($"    constant at 0x{candidate:X} = {origFloat} — INVALID float, skipping");
            return;
        }

        state.ConstantAddress = candidate;
        Console.WriteLine($"    constant at 0x{candidate:X} = {origFloat} (RIP disp: 0x{ripOffset:X})");
    }

    public bool Toggle(string name)
    {
        if (!_cheats.TryGetValue(name, out var state) || !state.Found) return false;
        return state.Active ? Disable(state) : Enable(state);
    }

    public bool SetConstantValue(string name, float value)
    {
        if (!_cheats.TryGetValue(name, out var state) || !state.Found) return false;
        if (state.Definition.Type != CheatType.PatchConstant) return false;
        if (state.ConstantAddress == 0) return false;

        state.CurrentValue = Math.Clamp(value, state.Definition.ConstantMin, state.Definition.ConstantMax);

        if (!state.Active)
        {
            state.OriginalBytes ??= ReadBytes(state.ConstantAddress, 4);
            if (state.OriginalBytes == null) return false;
        }

        if (!WriteBytes(state.ConstantAddress, BitConverter.GetBytes(state.CurrentValue))) return false;
        state.Active = true;
        return true;
    }

    private bool Enable(CheatState state)
    {
        var def = state.Definition;

        if (def.Type == CheatType.PatchConstant)
            return SetConstantValue(def.Name, state.CurrentValue);

        state.OriginalBytes ??= ReadBytes(state.Address, def.PatchBytes.Length);
        if (state.OriginalBytes == null) return false;

        if (!WriteBytes(state.Address, def.PatchBytes)) return false;
        state.Active = true;
        Console.WriteLine($"  [{def.ShortName}] ON");
        return true;
    }

    private bool Disable(CheatState state)
    {
        if (state.OriginalBytes == null) return false;
        var target = state.Definition.Type == CheatType.PatchConstant
            ? state.ConstantAddress : state.Address;

        if (!WriteBytes(target, state.OriginalBytes)) return false;
        state.Active = false;
        Console.WriteLine($"  [{state.Definition.ShortName}] OFF");
        return true;
    }

    public void RestoreAll()
    {
        foreach (var state in _cheats.Values)
        {
            if (state.Active) Disable(state);
        }
    }

    public IReadOnlyDictionary<string, CheatInfo> GetStatus()
    {
        var result = new Dictionary<string, CheatInfo>();
        foreach (var (name, state) in _cheats)
            result[name] = new CheatInfo(
                state.Found, state.Active, state.Definition.ShortName,
                state.Definition.Type == CheatType.PatchConstant,
                state.CurrentValue, state.Definition.ConstantMin, state.Definition.ConstantMax);
        return result;
    }

    private byte[]? ReadBytes(nint address, int count)
    {
        var buf = new byte[count];
        return _reader.TryReadBytes(address, buf) == count ? buf : null;
    }

    private bool WriteBytes(nint address, byte[] bytes)
    {
        if (address == 0 || bytes.Length == 0) return false;
        var handle = _process.Handle;

        if (!NativeMethods.VirtualProtectEx(handle, address, (nuint)bytes.Length,
                NativeMethods.PAGE_EXECUTE_READWRITE, out var oldProtect))
        {
            Console.WriteLine($"  VirtualProtectEx failed at 0x{address:X}");
            return false;
        }

        bool ok;
        unsafe
        {
            fixed (byte* p = bytes)
            {
                ok = NativeMethods.WriteProcessMemory(handle, address, p, (nuint)bytes.Length, out var written);
                if (ok && (int)written != bytes.Length) ok = false;
            }
        }

        NativeMethods.VirtualProtectEx(handle, address, (nuint)bytes.Length, oldProtect, out _);

        if (!ok) Console.WriteLine($"  WriteProcessMemory failed at 0x{address:X}");
        return ok;
    }
}

public readonly record struct CheatInfo(
    bool Found, bool Active, string ShortName,
    bool HasSlider, float Value, float Min, float Max);
