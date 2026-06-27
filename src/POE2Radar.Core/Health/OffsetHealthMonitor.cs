namespace POE2Radar.Core.Health;

/// <summary>How far the GameState → InGameState → AreaInstance → LocalPlayer chain resolved on one tick.</summary>
public enum ResolveStage
{
    None         = 0,  // GameState slot deref failed (slot 0 / process gone)
    GameState    = 1,  // GameState readable
    InGameState  = 2,  // a state-object pointer is present
    AreaInstance = 3,  // AreaInstance pointer non-null
    InZone       = 4,  // S4: AreaHash != 0 && AreaLevel in [0..100] — provably in a zone
    Full         = 5,  // S5: LocalPlayer present + metadata "Metadata/" — full success
}

/// <summary>Coarse health the overlay banner + dashboard surface.</summary>
public enum HealthState { Waiting, Searching, Ok, Loading, NotInGame, Broken }

/// <summary>One world-tick of observations. Pure data — no memory handles.</summary>
public readonly record struct ChainProbe(
    bool Attached,            // PoE2 process is alive (resolver)
    bool SlotResolved,        // resolver has published an in-zone-validated slot this attach
    int AobCandidateCount,    // raw AOB candidate count from the last scan (0 = pattern matched nothing)
    bool AobScanned,          // the resolver has completed at least one AOB scan
    ResolveStage Stage,       // how far Probe() got this tick on the resolved slot
    bool TerrainPresent,      // terrain grid read OK this tick (only meaningful at Full)
    bool UpdateAvailable,     // UpdateChecker: a newer release exists
    bool UpdateChecked,       // UpdateChecker: the check completed (we know current-vs-newer)
    string? UpdateUrl);       // UpdateChecker: download / releases URL

/// <summary>The monitor's verdict. Message is null when nothing should show (healthy / benign).</summary>
public readonly record struct HealthVerdict(HealthState State, string? Message);

/// <summary>
/// Pure, clock-injected health state machine answering "is POE2GPS reading the game, or did a patch break
/// the offsets?". Fed one <see cref="ChainProbe"/> per world tick via <see cref="Evaluate"/>; holds every
/// latch/timer internally so it is fully unit-testable with a fake clock (same idiom as HoldRepeat /
/// ObjectiveClassifier). Read-only: it only interprets observations, never touches game memory or input.
/// </summary>
public sealed class OffsetHealthMonitor
{
    private static readonly TimeSpan PatternBrokeHint = TimeSpan.FromSeconds(15);

    private readonly TimeSpan _holdOff;        // continuous "in zone, no player" before declaring Broken
    private readonly TimeSpan _postOkOffline;  // continuous NotInGame after an Ok before a soft warning
    private readonly TimeSpan _searchHint;     // continuous Searching before the soft update hint
    private readonly int _s4StableTicks;       // consecutive InZone ticks before "in a zone" is trusted
    private readonly int _radarEmptyTicks;     // consecutive Ok-but-no-terrain ticks before the soft warning

    private bool _everResolved;                // an Ok has occurred this session (the latch)
    private int _s4Stable;                     // consecutive ticks Stage >= InZone
    private int _okEmptyTicks;                 // consecutive Ok ticks with no terrain
    private DateTime? _loadingSince;           // start of the current continuous in-zone-no-player window
    private DateTime? _notInGameSince;         // start of the current continuous NotInGame window
    private DateTime? _searchingSince;         // start of the current continuous Searching window

    public OffsetHealthMonitor(TimeSpan holdOff, TimeSpan postOkOffline, TimeSpan searchHint,
                               int s4StableTicks, int radarEmptyTicks)
    {
        _holdOff = holdOff;
        _postOkOffline = postOkOffline;
        _searchHint = searchHint;
        _s4StableTicks = s4StableTicks;
        _radarEmptyTicks = radarEmptyTicks;
    }

    /// <summary>The shipping configuration: 25 s hold-off, 5 min post-Ok offline, 90 s search hint,
    /// 3-tick S4 stability, 10-tick radar-empty (≈330 ms at the 30 Hz world loop).</summary>
    public static OffsetHealthMonitor CreateDefault() =>
        new(TimeSpan.FromSeconds(25), TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(90), 3, 10);

    public HealthVerdict Evaluate(ChainProbe p, DateTime now)
    {
        if (!p.Attached)
        {
            _s4Stable = 0; _okEmptyTicks = 0;
            _loadingSince = null; _notInGameSince = null; _searchingSince = null;
            _everResolved = false;  // process exited → fresh session: clear the Ok latch too
            return new HealthVerdict(HealthState.Waiting, "Path of Exile 2 is not running.");
        }

        if (p.SlotResolved)
        {
            // S4 stability: only trust "in a zone" after N consecutive InZone(+) ticks (filters ghost reads).
            var inZone = p.Stage >= ResolveStage.InZone;
            _s4Stable = inZone ? _s4Stable + 1 : 0;

            if (p.Stage == ResolveStage.Full)
            {
                _everResolved = true;
                _searchingSince = null; _notInGameSince = null; _loadingSince = null;
                _okEmptyTicks = p.TerrainPresent ? 0 : _okEmptyTicks + 1;
                return _okEmptyTicks >= _radarEmptyTicks
                    ? new HealthVerdict(HealthState.Ok,
                        "Reading your character but no map data — deep reads may be stale after a patch.")
                    : new HealthVerdict(HealthState.Ok, null);
            }

            _okEmptyTicks = 0;

            if (inZone && _s4Stable >= _s4StableTicks)
            {
                _searchingSince = null; _notInGameSince = null;
                _loadingSince ??= now;
                return now - _loadingSince >= _holdOff
                    ? new HealthVerdict(HealthState.Broken, BrokenMessage(p))
                    : new HealthVerdict(HealthState.Loading, null);
            }

            _loadingSince = null;

            if (_everResolved)
            {
                _searchingSince = null;
                _notInGameSince ??= now;
                return now - _notInGameSince >= _postOkOffline
                    ? new HealthVerdict(HealthState.NotInGame, OfflineMessage(p))
                    : new HealthVerdict(HealthState.NotInGame, null);
            }

            // Slot resolved but never reached Full this session and not currently in a stable zone.
            _searchingSince ??= now;
            return new HealthVerdict(HealthState.Searching, SearchingMessage(p, now));
        }

        // No in-zone-validated slot yet this attach.
        _notInGameSince = null; _loadingSince = null;
        _searchingSince ??= now;
        return new HealthVerdict(HealthState.Searching, SearchingMessage(p, now));
    }

    private static string BrokenMessage(ChainProbe p) =>
        p.UpdateAvailable ? $"Update available — download: {p.UpdateUrl}"
        : p.UpdateChecked ? "POE2GPS is up to date but can't read the game — try restarting in a loaded zone."
        : "POE2GPS can't read Path of Exile 2 — it likely just updated; a fix is coming.";

    private static string OfflineMessage(ChainProbe p) =>
        p.UpdateAvailable ? $"Radar's been offline a while. Update available — download: {p.UpdateUrl}"
        : "Radar's been offline a while — if you're in a zone, a patch may have shifted offsets. Check for a new release.";

    private string SearchingMessage(ChainProbe p, DateTime now)
    {
        var age = now - _searchingSince!.Value;   // callers guarantee _searchingSince is set (??= now) before this call
        var patternBroke = p.AobScanned && p.AobCandidateCount == 0;
        if ((patternBroke && age >= PatternBrokeHint) || age >= _searchHint)
        {
            if (p.UpdateAvailable) return $"Update available — download: {p.UpdateUrl}";
            return patternBroke
                ? "POE2GPS can't find Path of Exile 2's game state — it likely just updated. Check for a new release."
                : "Still can't read the game — if you're in a zone, POE2GPS may need an update. Check for a new release.";
        }
        return "Connecting to Path of Exile 2 — load into a zone.";
    }
}
