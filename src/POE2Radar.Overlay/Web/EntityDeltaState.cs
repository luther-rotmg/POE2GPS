using System.Collections.Generic;

namespace POE2Radar.Overlay.Web;

/// <summary>Per-subscriber tracking for entity delta encoding on /stream.
/// <para>Owned by <see cref="SseChannel"/>'s inner Subscriber; mutated on the world-tick
/// thread inside <c>BuildFrame</c>. Not thread-safe on its own — safe here only because
/// Publish is single-writer.</para>
/// <para>On first Publish (or after a zone change, detected via <see cref="LastAreaHash"/>),
/// the subscriber receives a full snapshot and <see cref="SeededFullSnapshot"/> flips true;
/// subsequent Publishes emit {add, upd, del} against <see cref="LastSent"/>.</para></summary>
sealed class EntityDeltaState
{
    internal bool SeededFullSnapshot;
    internal readonly Dictionary<int, (float x, float y)> LastSent = new();
    internal uint LastAreaHash;
}
