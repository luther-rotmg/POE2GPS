using System;
using System.Collections.Generic;
using POE2Radar.Core.Game;
using POE2Radar.Core.Session;
using Poe2Live = POE2Radar.Core.Game.Poe2Live;

namespace POE2Radar.Core.Codex;

/// <summary>
/// C3 — alive→dead edge detector for unique-rarity monsters, gated by the
/// BossEncounterCatalog. Only fires a BossKillEvent when BOTH Rarity.Unique AND
/// a catalog hit (metadata-first, zone-fallback) are satisfied. Non-hits silently
/// drop — no debug-bucket events.
/// </summary>
public sealed class CodexBossObserver
{
    private readonly Func<string, BossEncounterCatalog.EncounterEntry?> _byMetadata;
    private readonly Func<string, BossEncounterCatalog.EncounterEntry?> _byZoneCode;
    private readonly Action<CodexEvent> _sink;
    private readonly Dictionary<uint, bool> _sawAlive = new();
    private readonly object _gate = new();

    // Primary ctor for production wiring — uses the singleton catalog.
    public CodexBossObserver(BossEncounterCatalog catalog, Action<CodexEvent> sink)
        : this(catalog.ByMetadata, catalog.ByZoneCode, sink) { }

    // Delegate ctor for unit tests (no JSON asset dependency).
    public CodexBossObserver(
        Func<string, BossEncounterCatalog.EncounterEntry?> byMetadata,
        Func<string, BossEncounterCatalog.EncounterEntry?> byZoneCode,
        Action<CodexEvent> sink)
    {
        _byMetadata = byMetadata ?? throw new ArgumentNullException(nameof(byMetadata));
        _byZoneCode = byZoneCode ?? throw new ArgumentNullException(nameof(byZoneCode));
        _sink       = sink       ?? throw new ArgumentNullException(nameof(sink));
    }

    /// <summary>
    /// Called from the WorldTick per-entity foreach. Full EntityDot is in scope there
    /// and it is a readonly record struct → cheap to pass by value.
    /// </summary>
    public void ObserveEntityTick(Poe2Live.EntityDot e, string currentAreaCode, long tsUnixSeconds)
    {
        // Fast path: only unique-rarity, monster-category entities matter.
        if (e.Rarity != Poe2Live.Rarity.Unique) return;

        var alive = e.IsAlive;
        var id    = e.Id;

        lock (_gate)
        {
            if (alive)
            {
                _sawAlive[id] = true;
                return;
            }
            // Dead this tick. Only fire the edge if we saw it alive first (mirrors KillTracker).
            if (!_sawAlive.TryGetValue(id, out var wasAlive) || !wasAlive) return;
            _sawAlive.Remove(id); // consume the edge → guards double-emit
        }

        // Catalog gate: metadata first (more specific), zone second. Non-hit → silent drop.
        var entry = _byMetadata(e.Metadata ?? string.Empty)
                 ?? _byZoneCode(currentAreaCode ?? string.Empty);
        if (entry == null) return;

        _sink(new BossKillEvent(
            Ts:        tsUnixSeconds,
            AreaHash:  0,  // not available at this call site; filled by future downstream
            Zone:      currentAreaCode ?? string.Empty,
            BossKey:   entry.Key,
            BossLabel: entry.Label));
    }
}