using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Klip.Model;
using SkiaSharp;

namespace Klip.Engine;

/// <summary>Frame-samples a comp and pipes raw RGBA frames into ffmpeg's stdin → H.264 MP4.
/// Pass a <see cref="GpuSession"/> to render on the GPU (faster under effect load).</summary>
public static class Mp4Exporter
{
    public static void Export(Comp comp, string outPath, string ffmpeg = "ffmpeg",
                              GpuSession? gpu = null, Action<double>? onProgress = null, double scale = 1.0)
    {
        var cpu = new Renderer();   // scaled path is CPU (vetores nítidos à resolução final)
        int frames = (int)Math.Round(comp.Duration * comp.Fps);
        string fps = comp.Fps.ToString(CultureInfo.InvariantCulture);
        int outW = Math.Max(2, (int)Math.Round(comp.Width * scale) & ~1);
        int outH = Math.Max(2, (int)Math.Round(comp.Height * scale) & ~1);

        string args =
            $"-y -f rawvideo -pixel_format rgba -video_size {outW}x{outH} " +
            $"-framerate {fps} -i - -c:v libx264 -pix_fmt yuv420p -crf 17 -movflags +faststart \"{outPath}\"";

        var psi = new ProcessStartInfo(ffmpeg, args)
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("could not start ffmpeg");
        proc.ErrorDataReceived += (_, __) => { };
        proc.BeginErrorReadLine();

        var stdin = proc.StandardInput.BaseStream;
        for (int f = 0; f < frames; f++)
        {
            double t = f / comp.Fps;
            using var img = scale == 1.0 && gpu is not null ? gpu.RenderFrame(comp, t) : cpu.RenderFrame(comp, t, scale);
            using var pm = img.PeekPixels();
            stdin.Write(pm.GetPixelSpan());
            onProgress?.Invoke((f + 1.0) / frames);
        }
        stdin.Flush();
        stdin.Close();
        proc.WaitForExit();
    }

    /// <summary>Generic export: pull each frame from a render function → H.264 MP4.</summary>
    public static void ExportFrames(int w, int h, double fps, int frames,
                                    Func<int, SKImage> render, string outPath, string ffmpeg = "ffmpeg")
    {
        string f = fps.ToString(CultureInfo.InvariantCulture);
        string args =
            $"-y -f rawvideo -pixel_format rgba -video_size {w}x{h} -framerate {f} -i - " +
            $"-c:v libx264 -pix_fmt yuv420p -crf 18 -movflags +faststart \"{outPath}\"";
        var psi = new ProcessStartInfo(ffmpeg, args)
        { RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("could not start ffmpeg");
        proc.ErrorDataReceived += (_, __) => { };
        proc.BeginErrorReadLine();
        var stdin = proc.StandardInput.BaseStream;
        for (int i = 0; i < frames; i++)
        {
            using var img = render(i);
            using var pm = img.PeekPixels();
            stdin.Write(pm.GetPixelSpan());
        }
        stdin.Flush(); stdin.Close();
        proc.WaitForExit();
    }

    /// <summary>Exporta a timeline para GIF animado (paleta gerada em one-pass, 15 fps).</summary>
    public static void ExportGif(Comp comp, string outPath, string ffmpeg = "ffmpeg")
    {
        var renderer = new Renderer();
        double gifFps = Math.Min(15, comp.Fps);
        int frames = (int)Math.Round(comp.Duration * gifFps);
        string f = gifFps.ToString(CultureInfo.InvariantCulture);
        string args =
            $"-y -f rawvideo -pixel_format rgba -video_size {comp.Width}x{comp.Height} -framerate {f} -i - " +
            "-filter_complex \"[0:v]scale=540:-1:flags=lanczos,split[s0][s1];[s0]palettegen=stats_mode=diff[p];[s1][p]paletteuse=dither=bayer\" " +
            $"-f gif \"{outPath}\"";
        var psi = new ProcessStartInfo(ffmpeg, args)
        { RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("could not start ffmpeg");
        proc.ErrorDataReceived += (_, __) => { };
        proc.BeginErrorReadLine();
        var stdin = proc.StandardInput.BaseStream;
        for (int i = 0; i < frames; i++)
        {
            using var img = renderer.RenderFrame(comp, i / gifFps);
            using var pm = img.PeekPixels();
            stdin.Write(pm.GetPixelSpan());
        }
        stdin.Flush(); stdin.Close();
        proc.WaitForExit();
    }

    /// <summary>Render a single frame to a PNG (for probing/verification).</summary>
    public static void ProbePng(Comp comp, double t, string pngPath, GpuSession? gpu = null)
    {
        using var img = gpu is not null ? gpu.RenderFrame(comp, t) : new Renderer().RenderFrame(comp, t);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(pngPath, data.ToArray());
    }
}
