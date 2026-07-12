using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;

namespace Klip.Engine;

/// <summary>Extrai a paleta dominante de uma imagem (k-means em RGB, amostrado) — a base do
/// quadro de paleta auto-organizado (blocos desiguais + hex codes).</summary>
public static class PaletteExtractor
{
    public static List<uint> Extract(string imagePath, int count = 5)
    {
        using var bmp = SKBitmap.Decode(imagePath);
        if (bmp is null) return new List<uint>();

        var samples = new List<(float r, float g, float b)>(12000);
        int step = Math.Max(1, (int)Math.Sqrt((long)bmp.Width * bmp.Height / 10000.0));
        for (int y = 0; y < bmp.Height; y += step)
            for (int x = 0; x < bmp.Width; x += step)
            {
                var c = bmp.GetPixel(x, y);
                if (c.Alpha < 32) continue;
                samples.Add((c.Red, c.Green, c.Blue));
            }
        if (samples.Count == 0) return new List<uint>();

        // k-means++ leve
        var rnd = new Random(7);
        var centers = new List<(float r, float g, float b)> { samples[rnd.Next(samples.Count)] };
        while (centers.Count < count)
        {
            var far = samples.OrderByDescending(s => centers.Min(c => Dist(s, c))).First();
            centers.Add(far);
        }
        var assign = new int[samples.Count];
        for (int iter = 0; iter < 10; iter++)
        {
            for (int i = 0; i < samples.Count; i++)
            {
                int best = 0; double bd = double.MaxValue;
                for (int k = 0; k < centers.Count; k++)
                {
                    double d = Dist(samples[i], centers[k]);
                    if (d < bd) { bd = d; best = k; }
                }
                assign[i] = best;
            }
            for (int k = 0; k < centers.Count; k++)
            {
                var members = samples.Where((_, i) => assign[i] == k).ToList();
                if (members.Count > 0)
                    centers[k] = (members.Average(m => m.r), members.Average(m => m.g), members.Average(m => m.b));
            }
        }

        return centers
            .Select((c, k) => (c, size: assign.Count(a => a == k)))
            .OrderByDescending(t => t.size)
            .Select(t => 0xFF000000u
                | ((uint)Math.Clamp((int)Math.Round(t.c.r), 0, 255) << 16)
                | ((uint)Math.Clamp((int)Math.Round(t.c.g), 0, 255) << 8)
                | (uint)Math.Clamp((int)Math.Round(t.c.b), 0, 255))
            .ToList();
    }

    private static double Dist((float r, float g, float b) a, (float r, float g, float b) c)
    { double dr = a.r - c.r, dg = a.g - c.g, db = a.b - c.b; return dr * dr + dg * dg + db * db; }

    /// <summary>Luminância — decide texto branco vs escuro sobre o swatch.</summary>
    public static bool IsDark(uint argb)
    {
        double r = (argb >> 16) & 0xFF, g = (argb >> 8) & 0xFF, b = argb & 0xFF;
        return 0.2126 * r + 0.7152 * g + 0.0722 * b < 140;
    }
}
