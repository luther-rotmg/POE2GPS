using System.Text;
using System.Text.RegularExpressions;

namespace POE2Radar.Overlay;

/// <summary>One library icon: a name, its SVG viewBox, and one-or-more path <c>d</c> strings
/// (multiple paths render as one shape; the default Alternate fill makes e.g. the Ring a donut).</summary>
public sealed record IconDef(string Name, float VbX, float VbY, float VbW, float VbH, IReadOnlyList<string> Paths)
{
    public string ViewBox => $"{Fmt(VbX)} {Fmt(VbY)} {Fmt(VbW)} {Fmt(VbH)}";
    private static string Fmt(float f) => f.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// The radar icon library. Ships a set of built-in icons (the original six radar shapes reproduced
/// exactly, plus extras), materialized to an <c>icons/</c> folder next to the executable on first run
/// — the same convention <see cref="Config.RadarSettings"/> uses for <c>config/</c>. Any <c>*.svg</c>
/// file in that folder is loaded too; a file whose name matches a built-in overrides it, so users can
/// restyle existing icons or add brand-new ones by dropping in standard single-or-multi-<c>&lt;path&gt;</c>
/// SVGs. Lookups are case-insensitive. Loaded once on first access.
/// </summary>
public static class IconLibrary
{
    /// <summary>Resource directory: an "icons" folder next to the executable (sibling of "config").</summary>
    public static string Directory { get; } = Path.Combine(AppContext.BaseDirectory, "icons");

    private static readonly Lazy<(IReadOnlyList<IconDef> Ordered, IReadOnlyDictionary<string, IconDef> Map)> _lib =
        new(Load);

    /// <summary>All icons in display order (built-ins first as authored, then user-only icons by name).</summary>
    public static IReadOnlyList<IconDef> Ordered => _lib.Value.Ordered;

    /// <summary>Case-insensitive name → icon map.</summary>
    public static IReadOnlyDictionary<string, IconDef> Map => _lib.Value.Map;

    /// <summary>True if an icon with this name exists (case-insensitive).</summary>
    public static bool Contains(string? name) => !string.IsNullOrEmpty(name) && Map.ContainsKey(name);

    /// <summary>Canonical (stored-case) name for a case-insensitive lookup, or null if unknown.</summary>
    public static string? Canonical(string? name)
        => !string.IsNullOrEmpty(name) && Map.TryGetValue(name, out var d) ? d.Name : null;

    private static (IReadOnlyList<IconDef>, IReadOnlyDictionary<string, IconDef>) Load()
    {
        var builtins = BuiltIns();

        // First run: materialize built-ins to disk so they're discoverable/editable (like config/).
        try
        {
            if (!System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.CreateDirectory(Directory);
                foreach (var b in builtins)
                    File.WriteAllText(Path.Combine(Directory, b.Name + ".svg"), ToSvg(b));
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"Icon library materialize failed: {ex.Message}"); }

        // Load any on-disk svgs (these override built-ins of the same name and add new ones).
        var disk = new Dictionary<string, IconDef>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (System.IO.Directory.Exists(Directory))
                foreach (var path in System.IO.Directory.EnumerateFiles(Directory, "*.svg"))
                {
                    var def = TryParseSvgFile(path);
                    if (def is not null) disk[def.Name] = def;
                }
        }
        catch (Exception ex) { Console.Error.WriteLine($"Icon library load failed: {ex.Message}"); }

        // Merge: built-ins in authored order (disk content wins), then remaining disk-only icons by name.
        var ordered = new List<IconDef>();
        var map = new Dictionary<string, IconDef>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in builtins)
        {
            var def = disk.TryGetValue(b.Name, out var d) ? d : b;
            disk.Remove(b.Name);
            ordered.Add(def); map[def.Name] = def;
        }
        foreach (var d in disk.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            ordered.Add(d); map[d.Name] = d;
        }
        return (ordered, map);
    }

    private static readonly Regex RxViewBox = new("viewBox\\s*=\\s*\"([^\"]*)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex RxPathD = new("<path\\b[^>]*?\\bd\\s*=\\s*\"([^\"]*)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    /// <summary>Parse a single .svg file into an <see cref="IconDef"/> (name = filename). Returns null
    /// for files with no &lt;path d="…"&gt; (e.g. icons drawn with &lt;circle&gt;/&lt;polygon&gt;, unsupported).</summary>
    private static IconDef? TryParseSvgFile(string path)
    {
        try
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrWhiteSpace(name)) return null;
            var text = File.ReadAllText(path);

            float vx = 0, vy = 0, vw = 24, vh = 24;
            var vb = RxViewBox.Match(text);
            if (vb.Success)
            {
                var p = vb.Groups[1].Value.Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length == 4 &&
                    float.TryParse(p[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var a) &&
                    float.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var b) &&
                    float.TryParse(p[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var c) &&
                    float.TryParse(p[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dd) &&
                    c > 0 && dd > 0)
                { vx = a; vy = b; vw = c; vh = dd; }
            }

            var paths = new List<string>();
            foreach (Match m in RxPathD.Matches(text))
                if (!string.IsNullOrWhiteSpace(m.Groups[1].Value)) paths.Add(m.Groups[1].Value.Trim());
            return paths.Count == 0 ? null : new IconDef(name, vx, vy, vw, vh, paths);
        }
        catch { return null; }
    }

    private static string ToSvg(IconDef d)
    {
        var sb = new StringBuilder();
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"").Append(d.ViewBox).Append("\">\n");
        foreach (var p in d.Paths) sb.Append("  <path d=\"").Append(p).Append("\" />\n");
        sb.Append("</svg>\n");
        return sb.ToString();
    }

    /// <summary>
    /// Built-in icons in a 0 0 24 24 viewBox. The first six (Circle/Square/Triangle/Diamond/Plus/Star)
    /// are authored to reproduce the formerly hardcoded radar geometries pixel-for-pixel after the
    /// renderer's viewBox→unit normalization (Square at 0.9 extent, the rest at full extent), so
    /// existing configs render identically. The rest are new additions.
    /// </summary>
    private static List<IconDef> BuiltIns()
    {
        static IconDef One(string name, string d) => new(name, 0, 0, 24, 24, new[] { d });
        return new List<IconDef>
        {
            One("Circle",       "M12 0 C18.627 0 24 5.373 24 12 C24 18.627 18.627 24 12 24 C5.373 24 0 18.627 0 12 C0 5.373 5.373 0 12 0 Z"),
            One("Square",       "M1.2 1.2 L22.8 1.2 L22.8 22.8 L1.2 22.8 Z"),
            One("Triangle",     "M12 0 L22.392 18 L1.608 18 Z"),
            One("Diamond",      "M12 0 L24 12 L12 24 L0 12 Z"),
            One("Plus",         "M7.68 0 L16.32 0 L16.32 7.68 L24 7.68 L24 16.32 L16.32 16.32 L16.32 24 L7.68 24 L7.68 16.32 L0 16.32 L0 7.68 L7.68 7.68 Z"),
            One("Star",         "M12 0 L14.962 7.922 L23.413 8.292 L16.793 13.557 L19.053 21.708 L12 17.04 L4.947 21.708 L7.207 13.557 L0.587 8.292 L9.038 7.922 Z"),
            One("Hexagon",      "M12 0 L22.392 6 L22.392 18 L12 24 L1.608 18 L1.608 6 Z"),
            One("Pentagon",     "M12 0 L23.413 8.292 L19.053 21.708 L4.947 21.708 L0.587 8.292 Z"),
            One("TriangleDown", "M12 24 L1.608 6 L22.392 6 Z"),
            One("ArrowUp",      "M12 1 L22 13 L16 13 L16 23 L8 23 L8 13 L2 13 Z"),
            One("Cross",        "M6 2.5 L12 8.5 L18 2.5 L21.5 6 L15.5 12 L21.5 18 L18 21.5 L12 15.5 L6 21.5 L2.5 18 L8.5 12 L2.5 6 Z"),
            One("Heart",        "M12 21 C12 21 2.5 14.5 2.5 8 C2.5 4.9 4.9 2.5 7.8 2.5 C9.7 2.5 11.2 3.6 12 5 C12.8 3.6 14.3 2.5 16.2 2.5 C19.1 2.5 21.5 4.9 21.5 8 C21.5 14.5 12 21 12 21 Z"),
            One("Droplet",      "M12 1.5 C12 1.5 4.5 11 4.5 16 C4.5 20.1 7.9 23 12 23 C16.1 23 19.5 20.1 19.5 16 C19.5 11 12 1.5 12 1.5 Z"),
            One("Gem",          "M12 1.5 L20.5 9 L12 22.5 L3.5 9 Z"),
            One("Ring",         "M12 1 C18.075 1 23 5.925 23 12 C23 18.075 18.075 23 12 23 C5.925 23 1 18.075 1 12 C1 5.925 5.925 1 12 1 Z M12 6 C15.314 6 18 8.686 18 12 C18 15.314 15.314 18 12 18 C8.686 18 6 15.314 6 12 C6 8.686 8.686 6 12 6 Z"),
            One("Shield",       "M12 1.5 L20.5 4.5 L20.5 11 C20.5 16.3 16.7 21 12 22.5 C7.3 21 3.5 16.3 3.5 11 L3.5 4.5 Z"),
            One("Exclamation",  "M10.4 2.5 L13.6 2.5 L13 14.5 L11 14.5 Z M12 17.5 C13.105 17.5 14 18.395 14 19.5 C14 20.605 13.105 21.5 12 21.5 C10.895 21.5 10 20.605 10 19.5 C10 18.395 10.895 17.5 12 17.5 Z"),
        };
    }
}
