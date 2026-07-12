using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace Klip.Engine;

/// <summary>
/// Remoção de fundo LEVE: u2netp (4.6MB, embutido no exe) via ONNX Runtime CPU — o substituto
/// do BiRefNet (~900MB, lento, RAM-pesado). Máscara 320×320 → alpha na resolução original.
/// </summary>
public static class BgRemover
{
    private static InferenceSession? _sess;
    private static readonly object Gate = new();

    private static string ModelPath()
    {
        var path = Path.Combine(Path.GetTempPath(), "klip_u2netp.onnx");
        if (!File.Exists(path))
        {
            var asm = typeof(BgRemover).Assembly;
            var name = asm.GetManifestResourceNames().First(n => n.EndsWith("u2netp.onnx"));
            using var s = asm.GetManifestResourceStream(name)!;
            using var f = File.Create(path);
            s.CopyTo(f);
        }
        return path;
    }

    /// <summary>Remove o fundo; devolve (caminho do PNG com alpha, ms decorridos).</summary>
    public static (string path, long ms) Remove(string imagePath)
    {
        var sw = Stopwatch.StartNew();
        using var orig = SKBitmap.Decode(imagePath)
            ?? throw new InvalidOperationException("imagem ilegível");

        const int S = 320;
        using var small = orig.Resize(new SKImageInfo(S, S), new SKSamplingOptions(SKFilterMode.Linear));
        if (small is null) throw new InvalidOperationException("resize falhou");

        // CHW normalizado (mean/std ImageNet) — via SPAN raw (GetPixel é lento demais)
        using var small32 = small.Copy(SKColorType.Rgba8888) ?? small;
        var spx = small32.GetPixelSpan();
        int sRow = small32.RowBytes;                              // respeitar o stride!
        var input = new DenseTensor<float>(new[] { 1, 3, S, S });
        float[] mean = { 0.485f, 0.456f, 0.406f }, std = { 0.229f, 0.224f, 0.225f };
        for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                int o = y * sRow + x * 4;
                input[0, 0, y, x] = (spx[o] / 255f - mean[0]) / std[0];
                input[0, 1, y, x] = (spx[o + 1] / 255f - mean[1]) / std[1];
                input[0, 2, y, x] = (spx[o + 2] / 255f - mean[2]) / std[2];
            }

        lock (Gate) _sess ??= new InferenceSession(ModelPath());
        var inputName = _sess!.InputMetadata.Keys.First();
        using var results = _sess.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, input) });
        var mask = results.First().AsEnumerable<float>().ToArray();   // 1x1x320x320

        float mn = mask.Min(), mx = mask.Max();
        float range = Math.Max(1e-6f, mx - mn);

        // máscara 320 → gray8 (span) → resize à resolução original
        using var mbmp = new SKBitmap(S, S, SKColorType.Gray8, SKAlphaType.Opaque);
        unsafe
        {
            var mspan = (byte*)mbmp.GetPixels();
            int mRow = mbmp.RowBytes;
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                    mspan[y * mRow + x] = (byte)(255f * (mask[y * S + x] - mn) / range);
        }
        using var maskFull = mbmp.Resize(new SKImageInfo(orig.Width, orig.Height),
            new SKSamplingOptions(SKFilterMode.Linear));

        // compor RGBA + alpha — tudo por spans
        using var orig32 = orig.Copy(SKColorType.Rgba8888) ?? orig;
        var oSpan = orig32.GetPixelSpan();
        var kSpan = maskFull!.GetPixelSpan();
        int oRow = orig32.RowBytes, kRow = maskFull.RowBytes;
        int kBpp = maskFull.BytesPerPixel;                        // Resize pode converter Gray8→BGRA!
        int wPix = orig.Width, hPix = orig.Height;
        var outBytes = new byte[wPix * hPix * 4];
        for (int y = 0; y < hPix; y++)
            for (int x = 0; x < wPix; x++)
            {
                int src = y * oRow + x * 4;
                int dst = (y * wPix + x) * 4;
                outBytes[dst] = oSpan[src];
                outBytes[dst + 1] = oSpan[src + 1];
                outBytes[dst + 2] = oSpan[src + 2];
                outBytes[dst + 3] = kSpan[y * kRow + x * kBpp];
            }

        var dir = Path.Combine(Path.GetTempPath(), "Klip");
        Directory.CreateDirectory(dir);
        var outPath = Path.Combine(dir,
            Path.GetFileNameWithoutExtension(imagePath) + "_nobg_" + Environment.TickCount64 + ".png");
        var info = new SKImageInfo(orig.Width, orig.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        unsafe
        {
            fixed (byte* p = outBytes)
            using (var img = SKImage.FromPixelCopy(info, (IntPtr)p, orig.Width * 4))
            using (var data = img.Encode(SKEncodedImageFormat.Png, 100))
            using (var f = File.Create(outPath))
                data.SaveTo(f);
        }

        sw.Stop();
        return (outPath, sw.ElapsedMilliseconds);
    }
}
