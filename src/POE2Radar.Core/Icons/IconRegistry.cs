using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using POE2Radar.Core.Game;

namespace POE2Radar.Core.Icons;

// v0.36 I1: IconRegistry references the canonical Poe2Live enums directly — Icons namespace
// intentionally does NOT re-declare EntityCategory or Rarity. There is exactly one enum for
// each in the codebase; duplicating them would create silent drift when Poe2Live adds a member.

public sealed record IconEntry(string Name, string FilePath, byte[] PngBytes);

public sealed record MappingRule(string Icon, string? MetadataGlob, Regex? CompiledGlob, Poe2Live.EntityCategory? Category, Poe2Live.Rarity? Rarity);

public sealed class IconRegistry : IDisposable
{
    public sealed record Snapshot(
        IReadOnlyDictionary<string, IconEntry> Icons,
        IReadOnlyList<MappingRule> Rules,
        string? Default,
        IReadOnlyDictionary<Poe2Live.EntityCategory, string> Categories,
        IReadOnlyDictionary<string, string> CategoryRarity,
        int Version)
    {
        public static readonly Snapshot Empty = new(
            new Dictionary<string, IconEntry>(),
            Array.Empty<MappingRule>(),
            null,
            new Dictionary<Poe2Live.EntityCategory, string>(),
            new Dictionary<string, string>(),
            0);
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private volatile Snapshot _snap = Snapshot.Empty;
    private int _version;
    private FileSystemWatcher? _watcher;
    private string? _directory;
    private CancellationTokenSource? _debounceCts;
    private readonly object _reloadGate = new();
    private bool _disposed;

    public Snapshot Current => _snap;

    public void LoadFrom(string directory)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(IconRegistry));
        _directory = directory;
        ReloadCore();
        StartWatcher(directory);
    }

    private void StartWatcher(string directory)
    {
        if (!Directory.Exists(directory)) return;
        _watcher?.Dispose();
        var w = new FileSystemWatcher(directory)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            Filter = "*.*",
            IncludeSubdirectories = false,
            EnableRaisingEvents = true,
        };
        w.Changed += OnChanged;
        w.Created += OnChanged;
        w.Deleted += OnChanged;
        w.Renamed += OnChanged;
        _watcher = w;
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        var cts = new CancellationTokenSource();
        var old = Interlocked.Exchange(ref _debounceCts, cts);
        old?.Cancel();
        old?.Dispose();
        _ = Task.Delay(250, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            try { ReloadCore(); } catch { /* debounce swallows IO races */ }
        }, TaskScheduler.Default);
    }

    private void ReloadCore()
    {
        lock (_reloadGate)
        {
            if (_directory is null || !Directory.Exists(_directory))
            {
                var v0 = Interlocked.Increment(ref _version);
                _snap = Snapshot.Empty with { Version = v0 };
                return;
            }

            var icons = new Dictionary<string, IconEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.EnumerateFiles(_directory, "*.png", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                try
                {
                    var bytes = File.ReadAllBytes(path);
                    icons[name] = new IconEntry(name, path, bytes);
                }
                catch { /* skip unreadable */ }
            }

            string? defaultIcon = null;
            var cats = new Dictionary<Poe2Live.EntityCategory, string>();
            var catRar = new Dictionary<string, string>(StringComparer.Ordinal);
            var rules = new List<MappingRule>();

            var mapPath = Path.Combine(_directory, "mapping.json");
            if (File.Exists(mapPath))
            {
                try
                {
                    using var s = File.OpenRead(mapPath);
                    using var doc = JsonDocument.Parse(s, new JsonDocumentOptions
                    {
                        AllowTrailingCommas = true,
                        CommentHandling = JsonCommentHandling.Skip,
                    });
                    var root = doc.RootElement;
                    if (root.TryGetProperty("default", out var d) && d.ValueKind == JsonValueKind.String)
                        defaultIcon = d.GetString();
                    // v0.36 locked schema: `categories: { "Monster": { "Rare": "...", "default": "..." } }` (nested dict).
                    // Legacy flat form `categories: { "Monster": "icon-name" }` still parsed for BYO configs so users
                    // don't get their existing mapping.json bricked by the schema tightening.
                    if (root.TryGetProperty("categories", out var cObj) && cObj.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var p in cObj.EnumerateObject())
                        {
                            if (!Enum.TryParse<Poe2Live.EntityCategory>(p.Name, ignoreCase: true, out var ec)) continue;
                            if (p.Value.ValueKind == JsonValueKind.String)
                            {
                                cats[ec] = p.Value.GetString()!;
                            }
                            else if (p.Value.ValueKind == JsonValueKind.Object)
                            {
                                foreach (var rp in p.Value.EnumerateObject())
                                {
                                    if (rp.Value.ValueKind != JsonValueKind.String) continue;
                                    var iconName = rp.Value.GetString()!;
                                    if (string.Equals(rp.Name, "default", StringComparison.OrdinalIgnoreCase))
                                        cats[ec] = iconName;
                                    else if (Enum.TryParse<Poe2Live.Rarity>(rp.Name, ignoreCase: true, out var rr))
                                        catRar[ec + "." + rr] = iconName;
                                }
                            }
                        }
                    }
                    // Legacy `categoryRarity` also honored so BYO configs authored against the pre-lock schema keep working.
                    if (root.TryGetProperty("categoryRarity", out var crObj) && crObj.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var p in crObj.EnumerateObject())
                            if (p.Value.ValueKind == JsonValueKind.String)
                                catRar[p.Name] = p.Value.GetString()!;
                    }
                    // v0.36 locked schema: field is `metadataGlobs`. Legacy `metadata` also honored.
                    var globArr = default(JsonElement);
                    var haveGlobs = root.TryGetProperty("metadataGlobs", out globArr)
                                 || root.TryGetProperty("metadata", out globArr);
                    if (haveGlobs && globArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in globArr.EnumerateArray())
                        {
                            if (el.ValueKind != JsonValueKind.Object) continue;
                            var glob = el.TryGetProperty("glob", out var g) ? g.GetString() : null;
                            var icon = el.TryGetProperty("icon", out var i) ? i.GetString() : null;
                            if (glob is null || icon is null) continue;
                            var pattern = "^" + Regex.Escape(glob).Replace("\\*", ".*") + "$";
                            rules.Add(new MappingRule(icon, glob, new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.Compiled), null, null));
                        }
                    }
                }
                catch
                {
                    // malformed json -> keep icons, empty rules/mappings
                    defaultIcon = null;
                    cats.Clear();
                    catRar.Clear();
                    rules.Clear();
                }
            }

            var v = Interlocked.Increment(ref _version);
            _snap = new Snapshot(icons, rules, defaultIcon, cats, catRar, v);
        }
    }

    public IconEntry? Resolve(Poe2Live.EntityCategory category, Poe2Live.Rarity rarity, string? metadata)
    {
        var s = _snap;
        if (!string.IsNullOrEmpty(metadata))
        {
            for (int i = 0; i < s.Rules.Count; i++)
            {
                var r = s.Rules[i];
                if (r.CompiledGlob is not null && r.CompiledGlob.IsMatch(metadata))
                    return LookupOrNull(s, r.Icon);
            }
        }
        var key = category + "." + rarity;
        if (s.CategoryRarity.TryGetValue(key, out var cr))
        {
            var hit = LookupOrNull(s, cr);
            if (hit is not null) return hit;
        }
        if (s.Categories.TryGetValue(category, out var c))
        {
            var hit = LookupOrNull(s, c);
            if (hit is not null) return hit;
        }
        if (s.Default is not null)
            return LookupOrNull(s, s.Default);
        return null;
    }

    private static IconEntry? LookupOrNull(Snapshot s, string name)
        => s.Icons.TryGetValue(name, out var e) ? e : null;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _watcher?.Dispose();
        var cts = Interlocked.Exchange(ref _debounceCts, null);
        cts?.Cancel();
        cts?.Dispose();
    }
}