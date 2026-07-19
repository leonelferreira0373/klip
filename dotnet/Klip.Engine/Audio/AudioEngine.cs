using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Klip.Model;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Klip.Engine.Audio;

/// <summary>Timeline de áudio já misturada: float intercalado (L,R,L,R…), pronta a tocar ou a escrever.</summary>
public sealed class MixBuffer
{
    public float[] Samples = Array.Empty<float>();
    public int Rate = 48000;
    public int Channels = 2;
    public double Duration => Samples.Length / (double)(Rate * Channels);
}

/// <summary>
/// Mistura o DAW. Estratégia: PRÉ-MISTURAR a timeline inteira para um buffer e depois só ler dele.
/// Assim não se aloca nada no callback de áudio (sem cliques do GC), o sync com a animação é
/// exato, e o MESMO buffer serve para o mux no MP4.
/// </summary>
public static class AudioMixer
{
    public const int Rate = 48000;
    public const int Channels = 2;

    /// <summary>Descodifica um ficheiro para float estéreo @Rate (mp3/wav/flac/m4a via NAudio).</summary>
    public static float[] Decode(string path, out int rate)
    {
        rate = Rate;
        using var reader = new AudioFileReader(path);
        ISampleProvider src = reader;
        if (src.WaveFormat.Channels == 1) src = new MonoToStereoSampleProvider(src);
        else if (src.WaveFormat.Channels > 2) src = new StereoToMonoSampleProvider(src).ToStereo();
        if (src.WaveFormat.SampleRate != Rate) src = new WdlResamplingSampleProvider(src, Rate);

        var outBuf = new List<float>(1 << 20);
        var tmp = new float[Rate * Channels];          // 1s de cada vez
        int n;
        while ((n = src.Read(tmp, 0, tmp.Length)) > 0)
            for (int i = 0; i < n; i++) outBuf.Add(tmp[i]);
        return outBuf.ToArray();
    }

    /// <summary>Duração de um ficheiro em segundos (sem descodificar tudo).</summary>
    public static double DurationOf(string path)
    {
        try { using var r = new AudioFileReader(path); return r.TotalTime.TotalSeconds; }
        catch { return 0; }
    }

    /// <summary>Mistura faixas+clips (trim, ganho, fades, pan, volume, mute/solo) num só buffer.</summary>
    public static MixBuffer Mix(IReadOnlyList<AudioTrack>? tracks, double duration)
    {
        int total = Math.Max(1, (int)Math.Ceiling(duration * Rate)) * Channels;
        var mix = new MixBuffer { Samples = new float[total], Rate = Rate, Channels = Channels };
        if (tracks is null || tracks.Count == 0) return mix;

        bool anySolo = tracks.Any(t => t.Solo);
        var cache = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);

        foreach (var tr in tracks)
        {
            if (tr.Mute || (anySolo && !tr.Solo)) continue;
            // pan constante-potência
            double pan = Math.Clamp(tr.Pan, -1, 1);
            float gL = (float)(tr.Volume * Math.Cos((pan + 1) * Math.PI / 4));
            float gR = (float)(tr.Volume * Math.Sin((pan + 1) * Math.PI / 4));

            foreach (var clip in tr.Clips)
            {
                if (string.IsNullOrWhiteSpace(clip.Path) || !File.Exists(clip.Path)) continue;
                if (!cache.TryGetValue(clip.Path, out var src))
                {
                    try { src = Decode(clip.Path, out _); } catch { continue; }
                    cache[clip.Path] = src;
                }
                int srcFrames = src.Length / Channels;
                int fromFrame = (int)Math.Round(Math.Max(0, clip.TrimStart) * Rate);
                int endFrame = clip.TrimEnd > 0 ? srcFrames - (int)Math.Round(clip.TrimEnd * Rate) : srcFrames;
                endFrame = Math.Clamp(endFrame, fromFrame, srcFrames);
                int len = endFrame - fromFrame;
                if (len <= 0) continue;

                int dstFrame0 = (int)Math.Round(Math.Max(0, clip.Start) * Rate);
                int fadeInF = (int)Math.Round(Math.Max(0, clip.FadeIn) * Rate);
                int fadeOutF = (int)Math.Round(Math.Max(0, clip.FadeOut) * Rate);
                float clipGain = (float)clip.Gain;

                for (int f = 0; f < len; f++)
                {
                    int dst = dstFrame0 + f;
                    if (dst < 0) continue;
                    int di = dst * Channels;
                    if (di + 1 >= total) break;

                    float env = clipGain;
                    if (fadeInF > 0 && f < fadeInF) env *= f / (float)fadeInF;
                    if (fadeOutF > 0 && f > len - fadeOutF) env *= Math.Max(0, (len - f) / (float)fadeOutF);

                    int si = (fromFrame + f) * Channels;
                    mix.Samples[di] += src[si] * env * gL;
                    mix.Samples[di + 1] += src[si + 1] * env * gR;
                }
            }
        }

        // limitador suave: só actua se a soma passar de 0 dBFS (evita clipping duro na soma das faixas)
        float peak = 0f;
        for (int i = 0; i < mix.Samples.Length; i++) { float a = Math.Abs(mix.Samples[i]); if (a > peak) peak = a; }
        if (peak > 1f)
        {
            float k = 1f / peak;
            for (int i = 0; i < mix.Samples.Length; i++) mix.Samples[i] *= k;
        }
        return mix;
    }

    /// <summary>Picos min/max por balde — é isto que desenha a waveform na timeline.</summary>
    public static (float[] min, float[] max) Peaks(MixBuffer mix, int buckets)
    {
        buckets = Math.Max(1, buckets);
        var lo = new float[buckets];
        var hi = new float[buckets];
        int frames = mix.Samples.Length / mix.Channels;
        if (frames == 0) return (lo, hi);
        for (int b = 0; b < buckets; b++)
        {
            int f0 = (int)((long)b * frames / buckets);
            int f1 = (int)((long)(b + 1) * frames / buckets);
            if (f1 <= f0) f1 = Math.Min(frames, f0 + 1);
            float mn = 0, mx = 0;
            for (int f = f0; f < f1; f++)
            {
                float v = mix.Samples[f * mix.Channels];          // canal esquerdo basta p/ desenho
                if (v < mn) mn = v;
                if (v > mx) mx = v;
            }
            lo[b] = mn; hi[b] = mx;
        }
        return (lo, hi);
    }

    /// <summary>Escreve o mix para WAV 16-bit (é o que o ffmpeg recebe para muxar no MP4).</summary>
    public static void WriteWav(MixBuffer mix, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var fmt = new WaveFormat(mix.Rate, 16, mix.Channels);
        using var w = new WaveFileWriter(path, fmt);
        var pcm = new byte[mix.Samples.Length * 2];
        for (int i = 0; i < mix.Samples.Length; i++)
        {
            short s = (short)(Math.Clamp(mix.Samples[i], -1f, 1f) * 32767f);
            pcm[i * 2] = (byte)(s & 0xFF);
            pcm[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }
        w.Write(pcm, 0, pcm.Length);
    }
}

/// <summary>Lê um float[] já misturado — zero alocações no caminho quente.</summary>
internal sealed class BufferSampleProvider : ISampleProvider
{
    private readonly float[] _buf;
    public int Position;
    public WaveFormat WaveFormat { get; }

    public BufferSampleProvider(float[] buf, int rate, int channels)
    { _buf = buf; WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, channels); }

    public int Read(float[] buffer, int offset, int count)
    {
        int n = Math.Min(count, _buf.Length - Position);
        if (n <= 0) return 0;
        Array.Copy(_buf, Position, buffer, offset, n);
        Position += n;
        return n;
    }
}

/// <summary>Transporte: toca o mix a partir de um instante da timeline e diz onde vai.</summary>
public sealed class AudioPlayer : IDisposable
{
    private WaveOutEvent? _out;
    private BufferSampleProvider? _src;
    private MixBuffer? _mix;

    public bool IsPlaying => _out?.PlaybackState == PlaybackState.Playing;

    /// <summary>Posição actual em segundos na timeline (para o playhead seguir o som).</summary>
    public double Position => _src is null || _mix is null ? 0
        : _src.Position / (double)(_mix.Rate * _mix.Channels);

    public void Play(MixBuffer mix, double fromSeconds)
    {
        Stop();
        if (mix.Samples.Length == 0) return;
        _mix = mix;
        _src = new BufferSampleProvider(mix.Samples, mix.Rate, mix.Channels)
        {
            Position = Math.Clamp((int)Math.Round(fromSeconds * mix.Rate) * mix.Channels, 0, mix.Samples.Length),
        };
        _out = new WaveOutEvent { DesiredLatency = 120 };
        _out.Init(_src);
        _out.Play();
    }

    public void Stop()
    {
        try { _out?.Stop(); _out?.Dispose(); } catch { }
        _out = null; _src = null;
    }

    public void Dispose() => Stop();
}
