using System;
using SkiaSharp;

namespace Klip.Engine;

/// <summary>
/// Traça uma máscara raster (canal alpha OU luminância) para um PATH vetorial editável — a ponte
/// roto→vetor. Usa SKRegion.GetBoundaryPath (contorno com BURACOS corretos por construção) + RDP
/// (via PathEdit.Simplify) para colapsar os degraus de pixel em poucos nós editáveis. Determinístico.
/// </summary>
public static class BitmapTrace
{
    private static bool Inside(SKBitmap b, int x, int y, byte thr, bool luma)
    {
        var c = b.GetPixel(x, y);
        if (luma) return (0.2126 * c.Red + 0.7152 * c.Green + 0.0722 * c.Blue) >= thr;
        return c.Alpha >= thr;
    }

    /// <summary>Máscara → "d" SVG (centrado em 0,0). threshold 0-255; simplify = tolerância RDP (px).</summary>
    public static string AlphaToPath(SKBitmap mask, byte threshold = 128, double simplify = 1.5, bool useLuma = false)
    {
        int W = mask.Width, H = mask.Height;
        using var region = new SKRegion();
        bool seeded = false;
        for (int y = 0; y < H; y++)
        {
            int x = 0;
            while (x < W)
            {
                if (!Inside(mask, x, y, threshold, useLuma)) { x++; continue; }
                int x0 = x;
                while (x < W && Inside(mask, x, y, threshold, useLuma)) x++;
                var r = new SKRectI(x0, y, x, y + 1);   // run [x0,x) na linha y
                if (!seeded) { region.SetRect(r); seeded = true; }
                else region.Op(r, SKRegionOperation.Union);
            }
        }
        if (!seeded) return "";

        using var path = region.GetBoundaryPath();      // contorno(s) com buracos corretos (winding da region)
        if (path is null || path.IsEmpty) return "";
        path.Transform(SKMatrix.CreateTranslation(-W / 2f, -H / 2f));   // convenção KLIP: centrado em 0,0
        var d = path.ToSvgPathData();
        try { return PathEdit.Parse(d).Simplify(simplify).ToSvgPathData(); }   // colapsa os degraus
        catch { return d; }
    }

    /// <summary>Nº de contornos do path traçado (exterior + buracos).</summary>
    public static int ContourCount(string d)
        => string.IsNullOrEmpty(d) ? 0 : PathEdit.Parse(d).Contours.Count;
}
