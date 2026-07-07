using System;
using System.Collections.Concurrent;
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
    }

    readonly ConcurrentDictionary<Guid, Subscriber> _subs = new();
    readonly TimeProvider _time;  // reserved for T3 heartbeat timer; not read in T2
    byte[]? _latest;
    readonly object _latestLock = new();
    volatile bool _disposed;

    public SseChannel(TimeProvider? time = null) { _time = time ?? TimeProvider.System; }
    public bool IsEmpty => _subs.IsEmpty;

    /// <summary>
    /// Fan out one snapshot to every subscriber. Non-atomic backpressure check-then-increment
    /// is safe here because <see cref="Publish"/> is called only from the world tick thread
    /// (single writer); the per-subscriber write task itself runs on the ThreadPool but each
    /// subscriber's queue is serialised by its own <see cref="Subscriber.WriteLock"/>.
    /// </summary>
    public void Publish(RadarState state)
    {
        if (_disposed || _subs.IsEmpty) return;
        var frame = SerializeFrame(state);
        lock (_latestLock) _latest = frame;
        foreach (var kv in _subs) FanOutTo(kv.Value, frame);
    }

    static byte[] SerializeFrame(RadarState state)
    {
        var payload = SnapshotForBrowser(state);
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts);
        // SSE frame: "data: <json>\n\n"
        var frame = new byte[6 + json.Length + 2];
        Encoding.ASCII.GetBytes("data: ", 0, 6, frame, 0);
        Buffer.BlockCopy(json, 0, frame, 6, json.Length);
        frame[6 + json.Length] = (byte)'\n';
        frame[6 + json.Length + 1] = (byte)'\n';
        return frame;
    }

    static object SnapshotForBrowser(RadarState s) => new
    {
        t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        area = s.AreaHash.ToString("x"),
        player = new {
            x = s.Player.X, y = s.Player.Y,
            hp = s.HpPct, hpMax = 100f,
            es = s.EsPct, esMax = 100f,
            mana = s.ManaPct, manaMax = 100f,
        },
        entities = SelectEntities(s),
        monoliths = SelectMonoliths(s),
    };

    static System.Collections.Generic.IEnumerable<object> SelectEntities(RadarState s)
    {
        // Nearest-to-player Euclidean, cap 800; dead-with-hpMax skip.
        var px = s.Player.X; var py = s.Player.Y;
        var alive = new System.Collections.Generic.List<POE2Radar.Core.Game.Poe2Live.EntityDot>(s.Entities.Count);
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
        var take = System.Math.Min(alive.Count, 800);
        var result = new System.Collections.Generic.List<object>(take);
        for (var i = 0; i < take; i++)
        {
            var e = alive[i];
            result.Add(new { id = e.Id, x = e.Grid.X, y = e.Grid.Y, cat = e.Category.ToString().ToLowerInvariant(), rar = e.Rarity.ToString().ToLowerInvariant(), hp = e.HpCur, hpMax = e.HpMax });
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

    internal Guid AddSubscriber(ISseSink sink)
    {
        var sub = new Subscriber { Id = Guid.NewGuid(), Sink = sink };
        _subs[sub.Id] = sub;
        return sub.Id;
    }

    internal bool RemoveSubscriber(Guid id) => _subs.TryRemove(id, out _);

    void RemoveSubscriberInternal(Subscriber sub) => _subs.TryRemove(sub.Id, out _);

    internal byte[]? LatestSnapshot()
    {
        lock (_latestLock) return _latest;
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var kv in _subs) kv.Value.Sink.Close();
        _subs.Clear();
    }
}
