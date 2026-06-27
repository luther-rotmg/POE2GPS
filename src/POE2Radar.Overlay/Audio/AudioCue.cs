using System;
using System.IO;
using System.Media;

namespace POE2Radar.Overlay.Audio;

/// <summary>One pre-loaded tone played fire-and-forget. Wraps SoundPlayer over an in-memory WAV.
/// Construct on the main thread; Play() is called only from the world thread (single caller).</summary>
public sealed class AudioCue
{
    private readonly SoundPlayer _player;
    public AudioCue(byte[] wav) { _player = new SoundPlayer(new MemoryStream(wav)); try { _player.Load(); } catch { } }
    public void Play() { try { _player.Play(); } catch { } }   // audio must never crash the overlay
}
