namespace POE2Radar.Core.Game;

/// <summary>
/// Reads PoE2's loaded-asset list (the "FileRoot" global) to support the Preload Alert feature.
/// Walk: FileRoot(AOB slot) → 16 buckets @0x38 (StdVector First/Last) → nodes @0x18 →
/// FilesPointer +0x08 → FileInfo { Name StdWString @+0x08, AreaChangeCount int @+0x40 }.
///
/// <para>✓ Validated live 2026-06-30 via POE2Radar.Research --preload (3 in-game runs).</para>
/// <para>Construct once; call <see cref="TryResolveRoot"/> at startup (caches the slot),
/// then call <see cref="ScanLoadedPaths"/> once per zone change (off the render thread).</para>
/// </summary>
public sealed class Poe2LoadedFiles
{
    private readonly ProcessHandle _proc;
    private readonly MemoryReader _reader;
    private nint _rootSlot;   // cached resolved global slot (0 = unresolved)

    public Poe2LoadedFiles(ProcessHandle proc, MemoryReader reader) { _proc = proc; _reader = reader; }

    /// <summary>Resolve + cache the FileRoot global slot via the AOB (once). Returns false if not found.</summary>
    public bool TryResolveRoot()
    {
        if (_rootSlot != 0) return true;
        foreach (var pat in AobPatterns.FileRootRefs)
        {
            var slots = AobScanner.ScanForResolvedAddresses(_proc, _reader, pat);
            if (slots.Count > 0) { _rootSlot = slots[0]; return true; }
        }
        return false;
    }

    /// <summary>Walk all 16 buckets → the set of currently-loaded asset paths (lowercased, '@'-split).
    /// HEAVY (~20k reads) — call ONCE per zone, off the render thread.</summary>
    public IReadOnlySet<string> ScanLoadedPaths()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (!TryResolveRoot()) return set;
        if (!_reader.TryReadStruct<nint>(_rootSlot, out var fileRoot) || fileRoot == 0) return set;
        for (var bi = 0; bi < Poe2.LoadedFiles.BucketCount; bi++)
        {
            var bucket = fileRoot + (nint)(bi * Poe2.LoadedFiles.BucketStride);
            if (!_reader.TryReadStruct<nint>(bucket, out var first)) continue;
            if (!_reader.TryReadStruct<nint>(bucket + 0x08, out var last)) continue;
            var fu = (ulong)first; var lu = (ulong)last;
            if (fu < 0x10000 || fu > 0x7FFFFFFFFFFF || lu < fu) continue;
            var range = (long)(lu - fu);
            if (range <= 0 || range > 16L * 1024 * 1024) continue;
            var count = range / Poe2.LoadedFiles.NodeStride;
            for (long i = 0; i < count; i++)
            {
                var node = first + (nint)(i * Poe2.LoadedFiles.NodeStride);
                if (!_reader.TryReadStruct<nint>(node + Poe2.LoadedFiles.FilesPointer, out var fp) || fp == 0) continue;
                var name = ReadStdWStringLocal(fp + Poe2.LoadedFiles.NameStr);
                if (name.Length < 4 || !(name.Contains('/') || name.Contains('.'))) continue;
                var p = name.Split('@')[0].ToLowerInvariant();
                set.Add(p);
            }
        }
        return set;
    }

    private string ReadStdWStringLocal(nint addr)
    {
        if (!_reader.TryReadStruct<int>(addr + 0x10, out var len) || len <= 0 || len > 1024) return string.Empty;
        if (len < 8) return _reader.ReadStringUtf16(addr, len);
        if (!_reader.TryReadStruct<nint>(addr, out var ptr) || ptr == 0) return string.Empty;
        return _reader.ReadStringUtf16(ptr, len);
    }
}
