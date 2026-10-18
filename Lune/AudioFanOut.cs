using System;
using NAudio.Wave;

namespace Lune;

// Decode once, with independent cursors for the device clocks. A stalled device cannot
// block the other device or accumulate unbounded audio. Retain only two seconds of PCM.
internal sealed class AudioFanOut
{
    private readonly ISampleProvider source;
    private readonly object gate = new();
    private readonly float[] ring, scratch;
    private long written;
    private bool ended;
    internal int Capacity => ring.Length;
    internal AudioFanOut(ISampleProvider source)
    {
        this.source = source;
        ring = new float[source.WaveFormat.SampleRate * source.WaveFormat.Channels * 2];
        scratch = new float[Math.Min(ring.Length, 4096 * source.WaveFormat.Channels)];
    }
    internal ISampleProvider Subscribe() => new Branch(this);
    private int Read(ref long cursor, float[] buffer, int offset, int count)
    {
        lock (gate)
        {
            var channels = source.WaveFormat.Channels;
            count -= count % channels;
            // Recover a stalled consumer near the live stream, rather than replaying old audio.
            if (cursor < written - ring.Length) cursor = written - Math.Min(ring.Length, source.WaveFormat.SampleRate / 5 * channels);
            var copied = 0;
            while (copied < count)
            {
                if (cursor == written && !ended)
                {
                    var read = source.Read(scratch, 0, Math.Min(count - copied, scratch.Length));
                    if (read == 0) ended = true;
                    else
                    {
                        var start = (int)(written % ring.Length); var first = Math.Min(read, ring.Length - start);
                        Array.Copy(scratch, 0, ring, start, first);
                        if (read > first) Array.Copy(scratch, first, ring, 0, read - first);
                        written += read;
                    }
                }
                var available = (int)Math.Min(count - copied, written - cursor);
                if (available == 0) break;
                var from = (int)(cursor % ring.Length); var take = Math.Min(available, ring.Length - from);
                // NAudio's WaveBuffer can expose a byte[] as float[] via explicit layout.
                // BlockCopy supports that shared storage; Array.Copy rejects its runtime type.
                Buffer.BlockCopy(ring, from * sizeof(float), buffer, (offset + copied) * sizeof(float), take * sizeof(float)); cursor += take; copied += take;
            }
            return copied;
        }
    }
    private sealed class Branch(AudioFanOut owner) : ISampleProvider
    {
        private long cursor;
        public WaveFormat WaveFormat => owner.source.WaveFormat;
        public int Read(float[] buffer, int offset, int count) => owner.Read(ref cursor, buffer, offset, count);
    }
}
