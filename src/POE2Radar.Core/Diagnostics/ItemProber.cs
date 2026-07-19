using POE2Radar.Core.Game;

namespace POE2Radar.Core.Diagnostics;

/// <summary>
/// Diagnostic prober for the item leaf-triangle: sweeps candidate offsets for WorldItemComponent.ItemEntity,
/// ModsComponent.Rarity, RenderItemComponent.ResourcePath, and BaseComponent.NameRow.
/// Probe-only (no auto-heal, no HealedOffsetCache.Resolve). B6b will add auto-heal on top.
/// </summary>
public static class ItemProber
{
    /// <summary>
    /// Sweep WorldItemComponent.ItemEntity pointer at candidate offsets [0x10..0x40] step 8 (7 candidates).
    /// Signature-pass when the pointer chases through EntityDetails and reads a Metadata string
    /// beginning with "Metadata/Items/".
    /// </summary>
    /// <param name="worldItemComponent">WorldItem component address on the WorldItem container entity.</param>
    /// <param name="r">MemoryReader instance.</param>
    /// <returns>Array of 7 ProbeSample&lt;nint&gt;.</returns>
    public static ProbeSample<nint>[] SweepWorldItemItemEntity(nint worldItemComponent, MemoryReader r)
    {
        var result = new List<ProbeSample<nint>>(7);
        if (worldItemComponent == 0)
        {
            for (var off = 0x10; off <= 0x40; off += 8)
                result.Add(new ProbeSample<nint>($"0x{off:X}", $"0x{off:X}", 0, "component-null", false));
            return result.ToArray();
        }

        for (var off = 0x10; off <= 0x40; off += 8)
        {
            var target = worldItemComponent + off;
            try
            {
                if (!r.TryReadStruct<nint>(target, out var ptr))
                {
                    result.Add(new ProbeSample<nint>($"0x{off:X}", $"0x{target:X}", 0, "read-fail", false));
                    continue;
                }
                if (ptr == 0)
                {
                    result.Add(new ProbeSample<nint>($"0x{off:X}", $"0x{target:X}", 0, "null-pointer", false));
                    continue;
                }

                // Chase to EntityDetails (+0x08)
                var details = r.ReadPointer(ptr + Poe2.Entity.EntityDetailsPtr);
                if (details == 0)
                {
                    result.Add(new ProbeSample<nint>($"0x{off:X}", $"0x{target:X}", ptr, "no-details", false));
                    continue;
                }

                // Read name from EntityDetails
                var name = r.ReadStringUtf16(details + Poe2.EntityDetails.Name, 64);
                var passes = name.StartsWith("Metadata/Items/", StringComparison.Ordinal);
                result.Add(new ProbeSample<nint>($"0x{off:X}", $"0x{target:X}", ptr, null, passes));
            }
            catch
            {
                result.Add(new ProbeSample<nint>($"0x{off:X}", $"0x{target:X}", 0, "exception", false));
            }
        }
        return result.ToArray();
    }

    /// <summary>
    /// Sweep ModsComponent.Rarity integer at candidate offsets [0x80..0xB0] step 4 (13 candidates).
    /// Signature-pass when the value is in [0..3] (Normal/Magic/Rare/Unique).
    /// </summary>
    /// <param name="modsComponent">Mods component address on the inner item entity.</param>
    /// <param name="r">MemoryReader instance.</param>
    /// <returns>Array of 13 ProbeSample&lt;int&gt;.</returns>
    public static ProbeSample<int>[] SweepModsRarity(nint modsComponent, MemoryReader r)
    {
        var result = new List<ProbeSample<int>>(13);
        if (modsComponent == 0)
        {
            for (var off = 0x80; off <= 0xB0; off += 4)
                result.Add(new ProbeSample<int>($"0x{off:X}", $"0x{off:X}", 0, "component-null", false));
            return result.ToArray();
        }

        for (var off = 0x80; off <= 0xB0; off += 4)
        {
            var target = modsComponent + off;
            try
            {
                if (!r.TryReadStruct<int>(target, out var value))
                {
                    result.Add(new ProbeSample<int>($"0x{off:X}", $"0x{target:X}", 0, "read-fail", false));
                    continue;
                }

                var passes = value >= 0 && value <= 3;
                result.Add(new ProbeSample<int>($"0x{off:X}", $"0x{target:X}", value, null, passes));
            }
            catch
            {
                result.Add(new ProbeSample<int>($"0x{off:X}", $"0x{target:X}", 0, "exception", false));
            }
        }
        return result.ToArray();
    }

    /// <summary>
    /// Sweep RenderItemComponent.ResourcePath pointer at candidate offsets [0x20..0x40] step 8 (5 candidates).
    /// Reads the pointer and dereferences it as a UTF-16 string.
    /// Signature-pass when the resulting string starts with "Art/2DItems/".
    /// </summary>
    /// <param name="renderItemComponent">RenderItem component address on the inner item entity.</param>
    /// <param name="r">MemoryReader instance.</param>
    /// <returns>Array of 5 ProbeSample&lt;string&gt;.</returns>
    public static ProbeSample<string>[] SweepRenderItemResourcePath(nint renderItemComponent, MemoryReader r)
    {
        var result = new List<ProbeSample<string>>(5);
        if (renderItemComponent == 0)
        {
            for (var off = 0x20; off <= 0x40; off += 8)
                result.Add(new ProbeSample<string>($"0x{off:X}", $"0x{off:X}", null, "component-null", false));
            return result.ToArray();
        }

        for (var off = 0x20; off <= 0x40; off += 8)
        {
            var target = renderItemComponent + off;
            try
            {
                var ptr = r.ReadPointer(target);
                if (ptr == 0)
                {
                    result.Add(new ProbeSample<string>($"0x{off:X}", $"0x{target:X}", null, "null-pointer", false));
                    continue;
                }

                var path = r.ReadStringUtf16(ptr, 128);
                var passes = path.StartsWith("Art/2DItems/", StringComparison.Ordinal);
                result.Add(new ProbeSample<string>($"0x{off:X}", $"0x{target:X}", path, null, passes));
            }
            catch
            {
                result.Add(new ProbeSample<string>($"0x{off:X}", $"0x{target:X}", null, "exception", false));
            }
        }
        return result.ToArray();
    }

    /// <summary>
    /// Sweep BaseComponent.NameRow at candidate offsets [0x30..0x50] step 8 (5 candidates).
    /// Two-hop chase: candidate → hop1 → hop1+0x00 → hop2 → UTF-16 string.
    /// Signature-pass when the resulting string length is in [3..64] AND majority alphabetic-or-space.
    /// </summary>
    /// <param name="baseComponent">Base component address on the inner item entity.</param>
    /// <param name="r">MemoryReader instance.</param>
    /// <returns>Array of 5 ProbeSample&lt;string&gt;.</returns>
    public static ProbeSample<string>[] SweepBaseNameRow(nint baseComponent, MemoryReader r)
    {
        var result = new List<ProbeSample<string>>(5);
        if (baseComponent == 0)
        {
            for (var off = 0x30; off <= 0x50; off += 8)
                result.Add(new ProbeSample<string>($"0x{off:X}", $"0x{off:X}", null, "component-null", false));
            return result.ToArray();
        }

        for (var off = 0x30; off <= 0x50; off += 8)
        {
            var target = baseComponent + off;
            try
            {
                // Hop 1: read pointer at candidate offset
                var hop1 = r.ReadPointer(target);
                if (hop1 == 0)
                {
                    result.Add(new ProbeSample<string>($"0x{off:X}", $"0x{target:X}", null, "hop1-null", false));
                    continue;
                }

                // Hop 2: read pointer at hop1 + 0x00
                var hop2 = r.ReadPointer(hop1 + 0x00);
                if (hop2 == 0)
                {
                    result.Add(new ProbeSample<string>($"0x{off:X}", $"0x{target:X}", null, "hop2-null", false));
                    continue;
                }

                // Read UTF-16 string at hop2
                var name = r.ReadStringUtf16(hop2, 64);
                if (string.IsNullOrEmpty(name))
                {
                    result.Add(new ProbeSample<string>($"0x{off:X}", $"0x{target:X}", name, "empty-string", false));
                    continue;
                }

                // Signature: length in [3..64] AND majority alphabetic-or-space
                var lenOk = name.Length >= 3 && name.Length <= 64;
                var alphaSpaceCount = 0;
                foreach (var ch in name)
                    if (char.IsLetter(ch) || ch == ' ') alphaSpaceCount++;
                var majority = alphaSpaceCount > name.Length / 2;
                var passes = lenOk && majority;

                result.Add(new ProbeSample<string>($"0x{off:X}", $"0x{target:X}", name, null, passes));
            }
            catch
            {
                result.Add(new ProbeSample<string>($"0x{off:X}", $"0x{target:X}", null, "exception", false));
            }
        }
        return result.ToArray();
    }
}