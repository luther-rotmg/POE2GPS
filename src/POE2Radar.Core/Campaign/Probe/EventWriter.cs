// v0.22 campaign-probe — spec §4 (world-thread safety) + §11 (opt-off = zero cost).
// JSONL sink for the campaign probe. One file per boot at
//   <baseDirectory>/<install_uuid>_<boot_epoch_ms>.jsonl.
// Baseline discipline:
//   * Enqueue() never blocks and never touches I/O — it publishes to an unbounded
//     Channel<EventRecord> via TryWrite. World-thread safe.
//   * A background pump drains the channel and writes lines to a StreamWriter with
//     AutoFlush = false. A Flush is coalesced against a shared lock: every 32 events
//     (batch trigger) OR every flushInterval tick (timer trigger), whichever first.
//   * File-open failure at ctor → become a permanent no-op, log once via Console.Error,
//     IsDisabled = true. PROBE-UI reads IsDisabled to hide the Contribute button.
//   * Any WriteLine that throws marks the writer permanently broken and subsequent
//     Enqueue calls no-op.
//   * FlushAsync / FlushSync wait until every currently-enqueued record has been
//     pulled + written by the pump, then flush the underlying StreamWriter to disk.
//     Used by PROBE-CONTRIBUTE before it zips the JSONL for /api/contribute-trace.
//   * MostRecentCompletePath() scans the base directory for prior boot files of the
//     same install and returns the highest-bootEpochMs one that isn't the current
//     boot — this is what /submit-trace ships when the user contributes.
using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace POE2Radar.Core.Campaign.Probe;

/// <summary>
/// JSONL sink for probe events. Consumed by PROBE-CORE (world-thread <c>Enqueue</c> per
/// event), PROBE-CONTRIBUTE (<c>MostRecentCompletePath</c> for /submit-trace packing +
/// <c>FlushSync</c> to land pending bytes before zipping), PROBE-UI (<c>IsDisabled</c>
/// hides the Contribute button), and PROBE-TESTS (spy asserts against <c>EventsWritten</c>).
/// </summary>
public sealed class EventWriter : IAsyncDisposable
{
    private const int FlushBatchSize = 32;
    private static readonly TimeSpan DefaultFlushInterval = TimeSpan.FromSeconds(1);

    // "Log once" latch — the writer degrades silently after the first Console.Error
    // line so a broken install doesn't spam the console on every event tick.
    private static int s_logOnceFlag;

    private readonly string _installUuid;
    private readonly string _baseDirectory;
    private readonly StreamWriter? _writer;
    private readonly Channel<EventRecord>? _channel;
    private readonly Task _pump;
    private readonly Timer? _flushTimer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    // Enqueue-side monotonic counter — used by FlushAsync/FlushSync to wait for the
    // pump to catch up. Increments only when TryWrite succeeds (never when disabled).
    private long _enqueuedCount;
    // Pump-side monotonic counter — increments for every record the pump has consumed
    // from the channel, whether or not the underlying WriteLine succeeded. FlushAsync
    // waits on this so a mid-run write failure still releases the flusher.
    private long _processedCount;
    // Successful-WriteLine counter exposed to consumers. Spec: "flushed writes count;
    // queued-but-unwritten do NOT count". Read via Interlocked.Read for cross-thread
    // visibility.
    private long _eventsWritten;

    // Non-flushed WriteLine count since the last Flush call. Only mutated under
    // _writeLock so pump + timer never race on the counter.
    private int _pendingSinceFlush;

    private int _disposed;
    private volatile bool _permanentlyBroken;

    /// <summary>Absolute path of the JSONL file this writer appends to.
    /// Shape: <c>&lt;baseDirectory&gt;/&lt;installUuid&gt;_&lt;bootEpochMs&gt;.jsonl</c>.</summary>
    public string CurrentFilePath { get; }

    /// <summary>Stable string form of the current boot identity (the <c>bootEpochMs</c>
    /// ctor arg). Not the file path; not the install UUID. Handed to callers that want
    /// to correlate live UI state with the currently-open trace.</summary>
    public string CurrentBootId { get; }

    /// <summary>Successful <c>WriteLine</c>s across the writer's lifetime. Queued-but-
    /// unwritten events do NOT count. Read via <see cref="Interlocked.Read(ref long)"/>
    /// for cross-thread visibility.</summary>
    public long EventsWritten => Interlocked.Read(ref _eventsWritten);

    /// <summary><c>true</c> after a failed file open at ctor OR after any write failure
    /// that renders the writer permanently broken. Subsequent <see cref="Enqueue"/>
    /// calls no-op. PROBE-UI reads this to hide the Contribute button.</summary>
    public bool IsDisabled => _permanentlyBroken;

    /// <summary>Construct a per-boot JSONL sink at
    /// <c>&lt;baseDirectory&gt;/&lt;installUuid&gt;_&lt;bootEpochMs&gt;.jsonl</c>. The base
    /// directory is used verbatim; the call site is responsible for resolving <c>%APPDATA%</c>
    /// (or any other roaming/portable location) before instantiation. A background pump
    /// starts on ctor and drains the channel until <see cref="DisposeAsync"/>. The optional
    /// <paramref name="flushInterval"/> controls the timer-coalesced flush cadence; it
    /// defaults to 1 s per spec §4.1 and is exposed so tests can shorten it.</summary>
    public EventWriter(string installUuid, long bootEpochMs, string baseDirectory, TimeSpan? flushInterval = null)
    {
        _installUuid = installUuid ?? throw new ArgumentNullException(nameof(installUuid));
        _baseDirectory = baseDirectory ?? throw new ArgumentNullException(nameof(baseDirectory));
        CurrentBootId = bootEpochMs.ToString("D", CultureInfo.InvariantCulture);
        CurrentFilePath = Path.Combine(baseDirectory, $"{installUuid}_{bootEpochMs}.jsonl");

        try
        {
            Directory.CreateDirectory(baseDirectory);
            // FileShare.Read so a concurrent /submit-trace pack can copy the file
            // while the app is still writing to it — the pack path calls FlushSync
            // first to make sure everything up to that instant is on disk.
            var fs = new FileStream(CurrentFilePath, FileMode.Append, FileAccess.Write, FileShare.Read);
            _writer = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = false,
            };
        }
        catch (Exception ex)
        {
            _permanentlyBroken = true;
            LogOnce($"[EventWriter] disabled — could not open {CurrentFilePath}: {ex.GetType().Name}: {ex.Message}");
        }

        if (_permanentlyBroken)
        {
            _pump = Task.CompletedTask;
            return;
        }

        _channel = Channel.CreateUnbounded<EventRecord>(new UnboundedChannelOptions
        {
            SingleReader = true,
            // The world thread is the only producer today, but leave the option room
            // so PROBE-CONTRIBUTE / a future test harness can enqueue from a helper.
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

        var interval = flushInterval ?? DefaultFlushInterval;
        // Clamp to a positive integer millisecond value — Timer refuses TimeSpan.Zero
        // as its period, and any sub-millisecond flushInterval would be indistinguishable
        // from spinning anyway.
        var intervalMs = (int)Math.Max(1, interval.TotalMilliseconds);
        _flushTimer = new Timer(_ => TimerFlush(), state: null, dueTime: intervalMs, period: intervalMs);
        _pump = Task.Run(PumpAsync);
    }

    /// <summary>Publish a record for background write. Non-blocking. No file I/O runs
    /// on the calling thread. No-op when the writer is disabled or disposed.</summary>
    public void Enqueue(EventRecord record)
    {
        if (_permanentlyBroken) return;
        if (Volatile.Read(ref _disposed) != 0) return;
        if (_channel!.Writer.TryWrite(record))
            Interlocked.Increment(ref _enqueuedCount);
    }

    /// <summary>Wait for the pump to catch up to every currently-enqueued record, then
    /// flush the underlying <see cref="StreamWriter"/> to disk. Safe to call from any
    /// thread. No-op when disabled or disposed.</summary>
    public async Task FlushAsync()
    {
        if (_permanentlyBroken || Volatile.Read(ref _disposed) != 0) return;
        var target = Interlocked.Read(ref _enqueuedCount);
        while (Interlocked.Read(ref _processedCount) < target)
        {
            if (_permanentlyBroken || Volatile.Read(ref _disposed) != 0 || _pump.IsCompleted) break;
            await Task.Delay(5).ConfigureAwait(false);
        }
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_writer is not null)
            {
                try
                {
                    await _writer.FlushAsync().ConfigureAwait(false);
                    _pendingSinceFlush = 0;
                }
                catch (Exception ex) { LogOnce($"[EventWriter] flush error: {ex}"); }
            }
        }
        finally { _writeLock.Release(); }
    }

    /// <summary>Synchronous variant of <see cref="FlushAsync"/> used by
    /// <c>/api/contribute-trace</c> before it packs the current file into a zip.</summary>
    public void FlushSync()
    {
        if (_permanentlyBroken || Volatile.Read(ref _disposed) != 0) return;
        var target = Interlocked.Read(ref _enqueuedCount);
        while (Interlocked.Read(ref _processedCount) < target)
        {
            if (_permanentlyBroken || Volatile.Read(ref _disposed) != 0 || _pump.IsCompleted) break;
            Thread.Sleep(5);
        }
        _writeLock.Wait();
        try
        {
            if (_writer is not null)
            {
                try
                {
                    _writer.Flush();
                    _pendingSinceFlush = 0;
                }
                catch (Exception ex) { LogOnce($"[EventWriter] flush error: {ex}"); }
            }
        }
        finally { _writeLock.Release(); }
    }

    /// <summary>Highest-boot JSONL file in <c>baseDirectory</c> for this install that
    /// ISN'T the currently-open one. Returns <c>null</c> when there are no prior boots.
    /// PROBE-CONTRIBUTE ships this path when the user opts in to /submit-trace so the
    /// live boot's file stays open for continued writes.</summary>
    public string? MostRecentCompletePath()
    {
        try
        {
            if (!Directory.Exists(_baseDirectory)) return null;
            var pattern = $"{_installUuid}_*.jsonl";
            long bestBoot = long.MinValue;
            string? bestPath = null;
            foreach (var path in Directory.EnumerateFiles(_baseDirectory, pattern))
            {
                if (string.Equals(path, CurrentFilePath, StringComparison.OrdinalIgnoreCase)) continue;
                var stem = Path.GetFileNameWithoutExtension(path);
                var underscore = stem.LastIndexOf('_');
                if (underscore < 0 || underscore == stem.Length - 1) continue;
                var suffix = stem.AsSpan(underscore + 1);
                if (!long.TryParse(suffix, NumberStyles.Integer, CultureInfo.InvariantCulture, out var boot)) continue;
                if (boot > bestBoot)
                {
                    bestBoot = boot;
                    bestPath = path;
                }
            }
            return bestPath;
        }
        catch
        {
            // Enumeration can throw on transient AV lock, network share hiccup, or a
            // dropped directory. A null return is exactly the "no prior boots" signal
            // the caller already handles.
            return null;
        }
    }

    /// <summary>Complete the channel, wait for the pump to drain, flush + close the
    /// StreamWriter. Idempotent — additional calls after the first no-op.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        if (_flushTimer is not null)
        {
            try { await _flushTimer.DisposeAsync().ConfigureAwait(false); }
            catch { /* dispose is best-effort */ }
        }

        try { _channel?.Writer.TryComplete(); } catch { /* already completed */ }

        try { await _pump.ConfigureAwait(false); }
        catch { /* pump exceptions are already surfaced via LogOnce */ }

        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            try { _writer?.Flush(); } catch { /* file may already be broken */ }
            try { _writer?.Dispose(); } catch { /* dispose is best-effort */ }
        }
        finally { _writeLock.Release(); }

        _writeLock.Dispose();
    }

    // ── Pump ──────────────────────────────────────────────────────────────────

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var record in _channel!.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                string line;
                try { line = EventRecordJson.Serialize(record); }
                catch (Exception ex)
                {
                    LogOnce($"[EventWriter] serialize error: {ex.GetType().Name}: {ex.Message}");
                    Interlocked.Increment(ref _processedCount);
                    continue;
                }

                _writeLock.Wait();
                try
                {
                    try
                    {
                        _writer!.WriteLine(line);
                        Interlocked.Increment(ref _eventsWritten);
                        _pendingSinceFlush++;
                        if (_pendingSinceFlush >= FlushBatchSize)
                        {
                            _writer.Flush();
                            _pendingSinceFlush = 0;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogOnce($"[EventWriter] write error: {ex.GetType().Name}: {ex.Message}");
                        // A mid-run write failure poisons the writer — the underlying
                        // stream may be in an inconsistent state and further writes are
                        // unsafe. Subsequent Enqueue calls short-circuit on IsDisabled.
                        _permanentlyBroken = true;
                    }
                }
                finally
                {
                    _writeLock.Release();
                    Interlocked.Increment(ref _processedCount);
                }

                if (_permanentlyBroken) break;
            }
        }
        catch (Exception ex)
        {
            LogOnce($"[EventWriter] pump crashed: {ex.GetType().Name}: {ex.Message}");
            _permanentlyBroken = true;
        }
    }

    // ── Timer flush ───────────────────────────────────────────────────────────

    private void TimerFlush()
    {
        if (_permanentlyBroken || Volatile.Read(ref _disposed) != 0) return;
        // Non-blocking acquire: if the pump is holding the lock, the timer bails and
        // the pump itself will trip the batch-flush threshold (or the next tick fires).
        if (!_writeLock.Wait(0)) return;
        try
        {
            if (_pendingSinceFlush > 0 && _writer is not null)
            {
                try
                {
                    _writer.Flush();
                    _pendingSinceFlush = 0;
                }
                catch (Exception ex) { LogOnce($"[EventWriter] timer flush error: {ex.GetType().Name}: {ex.Message}"); }
            }
        }
        finally { _writeLock.Release(); }
    }

    private static void LogOnce(string message)
    {
        if (Interlocked.Exchange(ref s_logOnceFlag, 1) != 0) return;
        try { Console.Error.WriteLine(message); } catch { /* console redirection failed — nothing to do */ }
    }
}
