using System;
using NAudio.Dsp;
using NAudio.Wave;

namespace Lune;

public sealed class SpectrumAnalyzer
{
    public const int BandCount = 24;
    private const int Size = 512;
    private readonly Complex[] fft = new Complex[Size];
    private readonly float[] window = new float[Size];
    private readonly float[] levels = new float[BandCount];
    private readonly object gate = new();
    private volatile bool enabled;
    private int filled, skip;
    public bool Enabled { get => enabled; set { enabled = value; if (!value) Clear(); } }
    public SpectrumAnalyzer() { for (var i = 0; i < Size; i++) window[i] = (float)(.5 * (1 - Math.Cos(2 * Math.PI * i / (Size - 1)))); }
    public void CopyLevels(float[] destination) { lock (gate) Array.Copy(levels, destination, Math.Min(destination.Length, BandCount)); }
    public void Clear() { lock (gate) Array.Clear(levels); }
    internal void Reset() { filled = skip = 0; Clear(); }
    internal void Analyze(float[] buffer, int offset, int count, int channels, int sampleRate)
    {
        if (!enabled) return;
        for (var i = offset; i + channels <= offset + count; i += channels)
        {
            if (skip > 0) { skip--; continue; }
            float sample = 0; for (var channel = 0; channel < channels; channel++) sample += buffer[i + channel]; sample /= channels;
            fft[filled].X = sample * window[filled]; fft[filled].Y = 0;
            if (++filled != Size) continue;
            filled = 0; skip = Math.Max(0, sampleRate / 18 - Size); FastFourierTransform.FFT(true, 9, fft);
            lock (gate)
            {
                if (!enabled) return;
                var upper = Math.Min(16000, sampleRate / 2d);
                for (var band = 0; band < BandCount; band++)
                {
                    var first = Math.Clamp((int)(60 * Math.Pow(upper / 60, band / (double)BandCount) * Size / sampleRate), 1, Size / 2 - 1);
                    var last = Math.Clamp((int)(60 * Math.Pow(upper / 60, (band + 1) / (double)BandCount) * Size / sampleRate), first + 1, Size / 2);
                    double peak = 0;
                    for (var bin = first; bin < last; bin++) peak = Math.Max(peak, Math.Sqrt(fft[bin].X * fft[bin].X + fft[bin].Y * fft[bin].Y) * 2);
                    levels[band] = (float)Math.Clamp((20 * Math.Log10(peak + 1e-9) + 65) / 65, 0, 1);
                }
            }
        }
    }
}
internal sealed class SpectrumSampleProvider(ISampleProvider source, SpectrumAnalyzer analyzer) : ISampleProvider
{
    public WaveFormat WaveFormat => source.WaveFormat;
    public int Read(float[] buffer, int offset, int count)
    {
        var read = source.Read(buffer, offset, count);
        analyzer.Analyze(buffer, offset, read, WaveFormat.Channels, WaveFormat.SampleRate); return read;
    }
}
