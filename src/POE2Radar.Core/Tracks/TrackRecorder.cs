using System;
using System.Diagnostics;
using System.Numerics;

namespace POE2Radar.Core.Tracks;

/// <summary>
/// v0.40 Cartographer: 1-Hz position recorder called from the render thread
/// every world tick (~30 Hz). Downsamples to one sample per second per
/// (character, zone) using a Stopwatch gate. Applies a 30-tick stability
/// gate on the player name before recording begins (defeats login/swap
/// flicker where PlayerName reads empty or stale). All exceptions are
/// silently swallowed — the recorder runs on the render hot path and must
/// never throw.
/// </summary>
public sealed class TrackRecorder
{
    /// <summary>~1 second at 30 Hz world-tick cadence.</summary>
    private const int NameStabilityTicks = 30;

    private readonly string _configDir;

    // Stability-gate state.
    private string? _lastPlayerName;
    private int _stableTickCount;

    // Current recording context (set after stability gate passes).
    private string? _currentCharacter;
    private string? _currentZone;

    // Timers.
    private readonly Stopwatch _zoneEntryStopwatch = new();
    private readonly Stopwatch _sinceLastSample = new();

    public TrackRecorder(string configDir)
    {
        _configDir = configDir;
    }

    /// <summary>
    /// Called every world tick (~30 Hz) from the render thread. Samples the
    /// position at most once per second per (character, zone). Swallows all
    /// exceptions.
    /// </summary>
    public void ObserveTick(string? playerName, string? zoneCode, Vector2? playerGrid)
    {
        try
        {
            // No-op if any required argument is missing or empty.
            if (string.IsNullOrEmpty(playerName) || string.IsNullOrEmpty(zoneCode) || playerGrid is null)
                return;

            // ── Stability gate: 30 consecutive unchanged ticks before recording. ──
            if (playerName == _lastPlayerName)
            {
                if (_stableTickCount < NameStabilityTicks)
                {
                    _stableTickCount++;
                    if (_stableTickCount < NameStabilityTicks)
                        return; // Not yet stable — skip recording.
                }
                // Falls through: stable and recording.
            }
            else
            {
                // Name changed (or first call). Reset stability counter.
                _lastPlayerName = playerName;
                _stableTickCount = 1;
                return; // First tick with this name — not stable yet.
            }

            // At this point the name is stable. Set current character if not already.
            if (_currentCharacter is null)
            {
                _currentCharacter = playerName;
                _currentZone = zoneCode;
                _zoneEntryStopwatch.Restart();
                _sinceLastSample.Restart();
                // Write the first sample immediately at t=0.
                TrackStore.Append(_configDir, _currentCharacter, _currentZone, new TrackSample(0, playerGrid.Value.X, playerGrid.Value.Y));
                _sinceLastSample.Restart();
                return;
            }

            // ── Zone change detection. ──
            if (zoneCode != _currentZone)
            {
                _currentZone = zoneCode;
                _currentCharacter = playerName;
                _zoneEntryStopwatch.Restart();
                _sinceLastSample.Restart();
                // Write first sample of the new zone immediately at t=0.
                TrackStore.Append(_configDir, _currentCharacter, _currentZone, new TrackSample(0, playerGrid.Value.X, playerGrid.Value.Y));
                _sinceLastSample.Restart();
                return;
            }

            // ── 1-Hz downsampling. ──
            if (_sinceLastSample.ElapsedMilliseconds >= 1000)
            {
                var t = _zoneEntryStopwatch.ElapsedMilliseconds;
                TrackStore.Append(_configDir, _currentCharacter, _currentZone, new TrackSample(t, playerGrid.Value.X, playerGrid.Value.Y));
                _sinceLastSample.Restart();
            }
        }
        catch
        {
            // Silent-failure invariant: never throw on the render hot path.
        }
    }
}