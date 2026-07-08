using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using POE2Radar.Core;
using POE2Radar.Overlay.Overlay;

namespace POE2Radar.Overlay.Web;

public sealed class SseChannel : IDisposable
{
    internal const int MaxQueued = 3;
    internal const int MaxConsecutiveDrops = 30; // 1 s at 30 Hz
    internal const int HeartbeatSeconds = 15;

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    sealed class Subscriber
    {
        public Guid Id;
        public ISseSink Sink = default!;
        public readonly SemaphoreSlim WriteLock = new(1, 1);
        public int Queued;
        public int ConsecutiveDrops;
        // T7: per-subscriber "last-seen" tracking for entity delta encoding on /stream.
        // Mutated only from Publish (single-writer via world-tick thread), so no lock needed.
        public readonly EntityDeltaState Delta = new();
    }

    readonly ConcurrentDictionary<Guid, Subscriber> _subs = new();
    readonly TimeProvider _time;  // reserved for T3 heartbeat timer; not read in T2
    byte[]? _latest;
    readonly object _latestLock = new();
    volatile bool _disposed;
    Timer? _heartbeat;

    public SseChannel(TimeProvider? time = null) { _time = time ?? TimeProvider.System; }
    public bool IsEmpty => _subs.IsEmpty;

    /// <summary>
    /// Fan out one snapshot to every subscriber. Non-atomic backpressure check-then-increment
    /// is safe here because <see cref="Publish"/> is called only from the world tick thread
    /// (single writer); the per-subscriber write task itself runs on the ThreadPool but each
    /// subscriber's queue is serialised by its own <see cref="Subscriber.WriteLock"/>.
    /// <para>T7: Frames are now serialised per-subscriber (N serializations per tick where N =
    /// subscriber count) so each client can receive a full snapshot or an entity delta based
    /// on its own <see cref="EntityDeltaState"/>. At 30 Hz with typical ≤4 subscribers the
    /// cost is negligible; documented so the reviewer doesn't flag it as a regression of the
    /// T2/T3 single-serialize optimization.</para>
    /// </summary>
    public void Publish(RadarState state)
    {
        if (_disposed || _subs.IsEmpty) return;
        byte[]? lastFrame = null;
        foreach (var kv in _subs)
        {
            var frame = BuildFrame(state, kv.Value.Delta);
            lastFrame = frame;
            FanOutTo(kv.Value, frame);
        }
        // Seed-on-connect uses _latest. When a fresh subscriber joins between ticks, its own
        // EntityDeltaState is empty so its FIRST BuildFrame will emit a canonical full snapshot;
        // the seed frame just paints something now instead of waiting up to ~33 ms.
        if (lastFrame != null) lock (_latestLock) _latest = lastFrame;
    }

    /// <summary>Test-only entry point: build one SSE frame for the given state against a
    /// caller-supplied <see cref="EntityDeltaState"/> (so the test can inspect full vs. delta
    /// behaviour without spinning up an actual subscriber).</summary>
    internal static byte[] BuildFrameForTest(RadarState state, EntityDeltaState delta)
        => BuildFrame(state, delta);

    static byte[] BuildFrame(RadarState state, EntityDeltaState delta)
    {
        // Zone-change reset: force a fresh full snapshot for this subscriber in the new area.
        if (delta.LastAreaHash != state.AreaHash)
        {
            delta.SeededFullSnapshot = false;
            delta.LastSent.Clear();
            delta.LastAreaHash = state.AreaHash;
        }
        var payload = SnapshotForBrowser(state, delta);
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts);
        // SSE frame: "data: <json>\n\n"
        var frame = new byte[6 + json.Length + 2];
        Encoding.ASCII.GetBytes("data: ", 0, 6, frame, 0);
        Buffer.BlockCopy(json, 0, frame, 6, json.Length);
        frame[6 + json.Length] = (byte)'\n';
        frame[6 + json.Length + 1] = (byte)'\n';
        return frame;
    }

    static object SnapshotForBrowser(RadarState s, EntityDeltaState delta)
    {
        var current = SelectEntitiesRaw(s);
        object entitiesField;
        object? entitiesDeltaField;
        bool full;

        if (!delta.SeededFullSnapshot)
        {
            full = true;
            var entList = new List<object>(current.Count);
            foreach (var e in current)
            {
                entList.Add(new { id = e.id, x = e.x, y = e.y, cat = e.cat, rar = e.rar, hp = e.hp, hpMax = e.hpMax });
            }
            entitiesField = entList;
            entitiesDeltaField = null;
            delta.LastSent.Clear();
            foreach (var e in current) delta.LastSent[e.id] = (e.x, e.y);
            delta.SeededFullSnapshot = true;
        }
        else
        {
            const float EPS = 0.01f;
            var add = new List<object>();
            var upd = new List<object>();
            var seen = new HashSet<int>(current.Count);
            foreach (var e in current)
            {
                seen.Add(e.id);
                if (!delta.LastSent.TryGetValue(e.id, out var prev))
                {
                    add.Add(new { id = e.id, x = e.x, y = e.y, cat = e.cat, rar = e.rar, hp = e.hp, hpMax = e.hpMax });
                    delta.LastSent[e.id] = (e.x, e.y);
                }
                else if (Math.Abs(prev.x - e.x) > EPS || Math.Abs(prev.y - e.y) > EPS)
                {
                    upd.Add(new { id = e.id, x = e.x, y = e.y });
                    delta.LastSent[e.id] = (e.x, e.y);
                }
            }
            var del = new List<int>();
            List<int>? toRemove = null;
            foreach (var kv in delta.LastSent)
            {
                if (!seen.Contains(kv.Key))
                {
                    del.Add(kv.Key);
                    (toRemove ??= new List<int>()).Add(kv.Key);
                }
            }
            if (toRemove != null)
                foreach (var id in toRemove) delta.LastSent.Remove(id);

            full = false;
            // Backward-compat: v0.20.0 clients read `snap.entities.length` unconditionally
            // and would NRE on null. Send an empty array on delta frames — new clients (T8)
            // gate on `full` and consume `entitiesDelta` when false.
            entitiesField = new List<object>();
            entitiesDeltaField = new { add, upd, del };
        }

        return new
        {
            t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            area = s.AreaHash.ToString("x"),
            player = new {
                x = s.Player.X, y = s.Player.Y,
                hp = s.HpPct, hpMax = 100f,
                es = s.EsPct, esMax = 100f,
                mana = s.ManaPct, manaMax = 100f,
            },
            full,
            entities = entitiesField,
            entitiesDelta = entitiesDeltaField,
            monoliths = SelectMonoliths(s),
        };
    }

    /// <summary>One entity projected to the wire shape, kept as a struct so both the full-snapshot
    /// and delta paths can iterate it without reboxing. Same nearest-to-player sort + 800 cap +
    /// dead-monster skip as the pre-T7 <c>SelectEntities</c>.</summary>
    readonly record struct EntityProjection(int id, float x, float y, string cat, string rar, int hp, int hpMax);

    static List<EntityProjection> SelectEntitiesRaw(RadarState s)
    {
        var px = s.Player.X; var py = s.Player.Y;
        var alive = new List<POE2Radar.Core.Game.Poe2Live.EntityDot>(s.Entities.Count);
        foreach (var e in s.Entities)
        {
            if (e.HpCur <= 0 && e.HpMax > 0) continue; // dead
            alive.Add(e);
        }
        alive.Sort((a, b) =>
        {
            var da = (a.Grid.X - px) * (a.Grid.X - px) + (a.Grid.Y - py) * (a.Grid.Y - py);
            var db = (b.Grid.X - px) * (b.Grid.X - px) + (b.Grid.Y - py) * (b.Grid.Y - py);
            var c = da.CompareTo(db);
            return c != 0 ? c : a.Id.CompareTo(b.Id);
        });
        var take = Math.Min(alive.Count, 800);
        var result = new List<EntityProjection>(take);
        for (var i = 0; i < take; i++)
        {
            var e = alive[i];
            result.Add(new EntityProjection(
                (int)e.Id, e.Grid.X, e.Grid.Y,
                e.Category.ToString().ToLowerInvariant(),
                e.Rarity.ToString().ToLowerInvariant(),
                e.HpCur, e.HpMax));
        }
        return result;
    }

    static System.Collections.Generic.IEnumerable<object> SelectMonoliths(RadarState s)
    {
        if (s.Monoliths == null) yield break;
        foreach (var m in s.Monoliths)
        {
            yield return new
            {
                x = m.Grid.X, y = m.Grid.Y,
                holes = m.Holes,
                unique = m.IsUnique,
                collected = m.Collected,
                anchor = m.AnchorName,
                bestEx = m.BestEx,
                bestName = (m.BestName ?? "").Length > 40 ? m.BestName!.Substring(0, 40) : m.BestName,
                color = m.Color,
            };
        }
    }

    void FanOutTo(Subscriber sub, byte[] frame)
    {
        if (sub.Sink.IsClosed) { RemoveSubscriberInternal(sub); return; }
        if (Interlocked.CompareExchange(ref sub.Queued, 0, 0) >= MaxQueued)
        {
            var drops = Interlocked.Increment(ref sub.ConsecutiveDrops);
            if (drops > MaxConsecutiveDrops)
            {
                sub.Sink.Close();
                RemoveSubscriberInternal(sub);
            }
            return;
        }
        Interlocked.Increment(ref sub.Queued);
        _ = Task.Run(async () =>
        {
            await sub.WriteLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await sub.Sink.WriteAsync(frame).ConfigureAwait(false);
                Interlocked.Exchange(ref sub.ConsecutiveDrops, 0);
            }
            catch
            {
                sub.Sink.Close();
                RemoveSubscriberInternal(sub);
            }
            finally
            {
                sub.WriteLock.Release();
                Interlocked.Decrement(ref sub.Queued);
            }
        });
    }

    internal Guid AddSubscriberWithSeed(ISseSink sink)
    {
        var sub = new Subscriber { Id = Guid.NewGuid(), Sink = sink };
        _subs[sub.Id] = sub;
        EnsureHeartbeat();

        // Fire the last-known snapshot immediately so the client's first paint
        // isn't delayed by up to ~33 ms until the next world tick.
        var seed = LatestSnapshot();
        if (seed != null) FanOutTo(sub, seed);
        return sub.Id;
    }

    // Kept for T2's tests; delegates to the seed variant now that seed is the
    // default AddSubscriber behavior. The old signature stays available for the
    // handful of tests that don't seed.
    internal Guid AddSubscriber(ISseSink sink) => AddSubscriberWithSeed(sink);

    // Peek for tests only.
    internal Timer? PeekHeartbeat() => _heartbeat;

    void EnsureHeartbeat()
    {
        lock (_latestLock)
        {
            if (_heartbeat != null) return;
            _heartbeat = new Timer(_ => Heartbeat(), null, HeartbeatSeconds * 1000, HeartbeatSeconds * 1000);
        }
    }

    void Heartbeat()
    {
        if (_disposed || _subs.IsEmpty) return;
        var ping = System.Text.Encoding.ASCII.GetBytes(": ping\n\n");
        foreach (var kv in _subs) FanOutTo(kv.Value, ping);
    }

    internal bool RemoveSubscriber(Guid id)
    {
        if (!_subs.TryRemove(id, out _)) return false;
        if (_subs.IsEmpty) TeardownHeartbeat();
        return true;
    }

    void TeardownHeartbeat()
    {
        lock (_latestLock)
        {
            var t = _heartbeat;
            _heartbeat = null;
            t?.Dispose();
        }
    }

    void RemoveSubscriberInternal(Subscriber sub)
    {
        _subs.TryRemove(sub.Id, out _);
        if (_subs.IsEmpty) TeardownHeartbeat();
    }

    internal byte[]? LatestSnapshot()
    {
        lock (_latestLock) return _latest;
    }

    public async Task HandleSubscribe(HttpListenerContext ctx)
    {
        var sink = new HttpListenerSseSink(ctx.Response);
        // Write an initial open comment so the browser fires `open` immediately.
        try
        {
            await sink.WriteAsync(System.Text.Encoding.ASCII.GetBytes(":\n\n")).ConfigureAwait(false);
        }
        catch
        {
            sink.Close();
            return;
        }

        var id = AddSubscriberWithSeed(sink);
        // Park until the client disconnects. There's no HttpListener primitive that
        // signals disconnect; we poll sink.IsClosed at 200 ms — long enough that idle
        // tabs don't burn cycles, short enough that Dispose() wakes us promptly.
        try
        {
            while (!sink.IsClosed)
            {
                // Sleep in short slices so Dispose() can wake us; there's no
                // built-in "wait for client disconnect" primitive on HttpListenerResponse.
                await Task.Delay(200).ConfigureAwait(false);
            }
        }
        finally
        {
            RemoveSubscriber(id);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        TeardownHeartbeat();
        foreach (var kv in _subs) kv.Value.Sink.Close();
        _subs.Clear();
    }
}
