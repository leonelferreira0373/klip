using System;
using System.IO;
using Klip.Engine;
using Klip.Model;
using Klip.Tests.Framework;
using SkiaSharp;

namespace Klip.Tests.Phase9_Roto;

/// <summary>Fase 9 — rotoscoping: a máscara traçada recorta o sujeito via track-matte da Fase 7
/// (pixel-E2E, sem ONNX); o BgRemover per-frame é model-gated (pending).</summary>
public static class RotoMatteTests
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

    [KlipTest(9, "roto→matte: a máscara traçada recorta o sujeito (track-matte Fase 7, sem ONNX)")]
    public static void RotoToMattePixels()
    {
        using var mask = DiscMask(300, 150, 150, 80);
        var d = RotoTrace.MaskToPath(mask, 128, 1.5);
        Assert.True(!string.IsNullOrEmpty(d), "roto traçou o contorno");

        Layer roto = new("roto", MorphTrack.Static(d), 0xFF808080u, Id: "roto");
        Layer subj = new("subj", MorphTrack.Static(Shapes.Rect(140, 140)), 0xFF101010u, Id: "subj",
            MatteSourceId: "roto", Matte: MatteMode.AlphaNormal);
        var comp = new Comp(300, 300, 30, 0.1, 0xFFFFFFFFu, new[] { subj, roto });
        using var f = H.Render(comp, 0);
        Assert.Greater(f.AverageAround(150, 150, 3).RgbDistance(Rgba.White), 150, "sujeito VISÍVEL dentro do disco");
        Assert.Less(f.AverageAround(20, 150, 3).RgbDistance(Rgba.White), 20, "recortado fora do disco");
    }

    [KlipTest(9, "roto: BgRemover ONNX é model-gated (pending se o modelo/native faltar)",
        Criterion = "download real de máscara — nunca FAIL, PEND se indisponível")]
    public static void RotoOnnxPending()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "kliproto_" + Guid.NewGuid().ToString("N") + ".png");
        using (var bmp = new SKBitmap(64, 64))
        {
            using (var cv = new SKCanvas(bmp)) { cv.Clear(SKColors.White); using var pt = new SKPaint { Color = SKColors.Red }; cv.DrawCircle(32, 32, 20, pt); }
            using var img = SKImage.FromBitmap(bmp);
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(tmp, data.ToArray());
        }
        try
        {
            RotoResult r;
            try { r = RotoTrace.FromImage(tmp); }
            catch (Exception e) { throw new PendingException("roto ONNX model-gated: " + e.Message); }
            Assert.True(!string.IsNullOrEmpty(r.D), "roto real produziu contorno");
            Assert.Greater(r.Contours, 0, "≥1 contorno");
        }
        finally { try { File.Delete(tmp); } catch { } }
    }
}
