using System;
using Klip.Engine;
using Klip.Model;
using Klip.Tests.Framework;
using SkiaSharp;

namespace Klip.Tests.Phase9_Roto;

/// <summary>Fase 9 — trace raster→vetor (SKRegion boundary + RDP). Círculo, buraco (multi-contorno),
/// monotonia do threshold, determinismo. PURO (sem ONNX).</summary>
public static class BitmapTraceTests
{
    private static readonly RenderHarness H = new();

    private static SKBitmap DiscMask(int size, float cx, float cy, float r)
    {
        var bmp = new SKBitmap(size, size, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var cv = new SKCanvas(bmp);
        cv.Clear(new SKColor(0, 0, 0, 0));
        using var pt = new SKPaint { Color = new SKColor(255, 255, 255, 255), IsAntialias = false };
        cv.DrawCircle(cx, cy, r, pt);
        return bmp;
    }

    [KlipTest(9, "trace: disco alpha → path ~circular que renderiza preenchido")]
    public static void TraceCirclePure()
    {
        using var mask = DiscMask(400, 200, 200, 120);
        var d = BitmapTrace.AlphaToPath(mask, 128, 1.5);
        Assert.True(!string.IsNullOrEmpty(d), "traçou o contorno");
        using var p = SKPath.ParseSvgPathData(d);
        Assert.Near(240, p.Bounds.Width, 14, "largura ≈ 2r=240");
        Assert.Near(240, p.Bounds.Height, 14, "altura ≈ 240");

        var comp = new Comp(400, 400, 30, 0.1, 0xFFFFFFFFu, new[] { new Layer("c", MorphTrack.Static(d), 0xFF101010u) });
        using var f = H.Render(comp, 0);
        Assert.Greater(f.AverageAround(200, 200, 3).RgbDistance(Rgba.White), 150, "centro preenchido");
        Assert.Less(f.AverageAround(20, 20, 3).RgbDistance(Rgba.White), 20, "canto = fundo");
        long area = f.ContentPixelCount(Rgba.White);
        Assert.InRange(Math.PI * 120 * 120 * 0.85, Math.PI * 120 * 120 * 1.15, area, $"área ≈ π·120² (obtive {area})");
    }

    [KlipTest(9, "trace: anel (disco com furo) → multi-contorno, buraco preservado no render")]
    public static void TraceSquareAndHole()
    {
        var bmp = new SKBitmap(300, 300, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using (var cv = new SKCanvas(bmp))
        {
            cv.Clear(new SKColor(0, 0, 0, 0));
            using var pt = new SKPaint { Color = new SKColor(255, 255, 255, 255), IsAntialias = false };
            cv.DrawCircle(150, 150, 100, pt);
            using var erase = new SKPaint { Color = new SKColor(0, 0, 0, 0), BlendMode = SKBlendMode.Src, IsAntialias = false };
            cv.DrawCircle(150, 150, 45, erase);   // fura o centro (alpha→0)
        }
        var d = BitmapTrace.AlphaToPath(bmp, 128, 1.5);
        bmp.Dispose();
        Assert.Greater(BitmapTrace.ContourCount(d), 1, "anel → ≥2 contornos (buraco)");

        var comp = new Comp(300, 300, 30, 0.1, 0xFFFFFFFFu, new[] { new Layer("ring", MorphTrack.Static(d), 0xFF101010u) });
        using var f = H.Render(comp, 0);
        Assert.Less(f.AverageAround(150, 150, 3).RgbDistance(Rgba.White), 20, "centro = FUNDO (buraco preservado)");
        Assert.Greater(f.AverageAround(150, 60, 3).RgbDistance(Rgba.White), 150, "anel preenchido");
    }

    [KlipTest(9, "trace: threshold menor → área traçada maior (iso-nível monotónico)")]
    public static void TraceThresholdMonotone()
    {
        var bmp = new SKBitmap(200, 200, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using (var cv = new SKCanvas(bmp))
        {
            cv.Clear(new SKColor(0, 0, 0, 0));
            using var sh = SKShader.CreateRadialGradient(new SKPoint(100, 100), 90,
                new[] { new SKColor(255, 255, 255, 255), new SKColor(255, 255, 255, 0) }, null, SKShaderTileMode.Clamp);
            using var pt = new SKPaint { Shader = sh };
            cv.DrawRect(new SKRect(0, 0, 200, 200), pt);
        }
        var wLow = SKPath.ParseSvgPathData(BitmapTrace.AlphaToPath(bmp, 80, 1.0)).Bounds.Width;
        var wHigh = SKPath.ParseSvgPathData(BitmapTrace.AlphaToPath(bmp, 180, 1.0)).Bounds.Width;
        bmp.Dispose();
        Assert.Greater(wLow, wHigh - 0.5f, $"threshold 80 → contorno mais largo que 180 (low={wLow}, high={wHigh})");
    }

    [KlipTest(9, "trace: determinístico (mesma máscara → mesma 'd')")]
    public static void TraceDeterministic()
    {
        using var m = DiscMask(200, 100, 100, 60);
        Assert.True(BitmapTrace.AlphaToPath(m, 128, 1.5) == BitmapTrace.AlphaToPath(m, 128, 1.5), "traço reprodutível");
    }
}
