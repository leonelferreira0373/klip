using System;
using SkiaSharp;

namespace Klip.Engine;

/// <summary>
/// v1 shape-morph: arc-length resample both paths to N points, pick the cyclic offset that
/// minimises drift, then point-lerp. Robust and swim-resistant; the growth path is true
/// bezier-correspondence + ARAP (kept as a pluggable step).
/// </summary>
public static class PathMorph
{
    public static SKPoint[] Sample(SKPath path, int n)
    {
        var pts = new SKPoint[n];
        using var m = new SKPathMeasure(path, forceClosed: true);
        float len = m.Length;
        if (len <= 0)
        {
            var b = path.Bounds;
            for (int i = 0; i < n; i++) pts[i] = new SKPoint(b.MidX, b.MidY);
            return pts;
        }
        for (int i = 0; i < n; i++)
        {
            float d = len * i / n;
            m.GetPosition(d, out var p);
            pts[i] = p;
        }
        return pts;
    }

    private static int BestOffset(SKPoint[] a, SKPoint[] b)
    {
        int n = a.Length, best = 0;
        double bestErr = double.MaxValue;
        for (int off = 0; off < n; off++)
        {
            double err = 0;
            for (int i = 0; i < n; i++)
            {
                var pa = a[i];
                var pb = b[(i + off) % n];
                double dx = pa.X - pb.X, dy = pa.Y - pb.Y;
                err += dx * dx + dy * dy;
                if (err >= bestErr) break;
            }
            if (err < bestErr) { bestErr = err; best = off; }
        }
        return best;
    }

    /// <summary>Interpolated path from <paramref name="a"/> to <paramref name="b"/> at t in [0,1].</summary>
    public static SKPath Interpolate(SKPath a, SKPath b, float t, int n = 192)
    {
        var pa = Sample(a, n);
        var pb = Sample(b, n);
        int off = BestOffset(pa, pb);
        var path = new SKPath();
        for (int i = 0; i < n; i++)
        {
            var p = pa[i];
            var q = pb[(i + off) % n];
            float x = p.X + (q.X - p.X) * t;
            float y = p.Y + (q.Y - p.Y) * t;
            if (i == 0) path.MoveTo(x, y); else path.LineTo(x, y);
        }
        path.Close();
        return path;
    }
}
