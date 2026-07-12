using System;
using System.Collections.Generic;
using System.Numerics;
using Clipper2Lib;
using Klip.Engine.ThreeD;
using SkiaSharp;

namespace Klip.Engine;

/// <summary>
/// Vector booleans on SVG path data via Clipper2 (integer-exact — the CorelDRAW-grade engine).
/// Ops work in canvas space: transform each operand by its layer transform first
/// (<see cref="TransformD"/>), then combine. Result is a new canvas-space "d".
/// </summary>
public static class PathBoolean
{
    private const double S = 100.0; // int scaling

    public static string? Op(string dA, string dB, string op)
    {
        var a = ToPaths(dA);
        var b = ToPaths(dB);
        if (a.Count == 0 || b.Count == 0) return null;
        Paths64 r = op switch
        {
            "union" => Clipper.Union(a, b, FillRule.NonZero),
            "intersect" => Clipper.Intersect(a, b, FillRule.NonZero),
            "xor" => Clipper.Xor(a, b, FillRule.NonZero),
            _ => Clipper.Difference(a, b, FillRule.NonZero),   // subtract
        };
        if (r.Count == 0) return null;
        return ToD(r);
    }

    /// <summary>Apply a layer transform (scale → rotate° → translate) to path data.</summary>
    public static string TransformD(string d, double px, double py, double rotDeg, double scale)
    {
        using var p = SKPath.ParseSvgPathData(d);
        if (p is null) return d;
        var m = SKMatrix.CreateScale((float)scale, (float)scale);
        m = m.PostConcat(SKMatrix.CreateRotationDegrees((float)rotDeg));
        m = m.PostConcat(SKMatrix.CreateTranslation((float)px, (float)py));
        p.Transform(m);
        return p.ToSvgPathData();
    }

    private static Paths64 ToPaths(string d)
    {
        var res = new Paths64();
        using var p = SKPath.ParseSvgPathData(d);
        if (p is null) return res;
        foreach (var contour in Extruder.Flatten(p, 1f))
        {
            var path = new Path64(contour.Count);
            foreach (var v in contour)
                path.Add(new Point64((long)Math.Round(v.X * S), (long)Math.Round(v.Y * S)));
            if (path.Count > 2) res.Add(path);
        }
        return res;
    }

    private static string ToD(Paths64 paths)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var path in paths)
        {
            for (int i = 0; i < path.Count; i++)
            {
                var pt = path[i];
                sb.Append(i == 0 ? 'M' : 'L')
                  .Append((pt.X / S).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture))
                  .Append(' ')
                  .Append((pt.Y / S).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture))
                  .Append(' ');
            }
            sb.Append("Z ");
        }
        return sb.ToString().TrimEnd();
    }
}
