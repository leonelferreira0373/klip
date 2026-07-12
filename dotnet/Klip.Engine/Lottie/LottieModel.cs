using System;
using System.Collections.Generic;

namespace Klip.Engine.Lottie;

/// <summary>Parsed Lottie (bodymovin) document. Frame-based timeline (fr fps, ip→op frames).</summary>
public sealed class LottieDoc
{
    public double Fr = 60, Ip, Op;
    public int W, H;
    public List<LottieLayer> Layers { get; } = new();
    public Dictionary<string, LottieAsset> Assets { get; } = new();
    public double DurationSeconds => Fr > 0 ? (Op - Ip) / Fr : 0;
}

public sealed class LottieAsset
{
    public string Id = "";
    public List<LottieLayer> Layers { get; } = new();   // precomp assets
}

public sealed class LottieLayer
{
    public int Type;                 // 0 precomp, 1 solid, 2 image, 3 null, 4 shape, 5 text
    public int Index = -1;
    public int Parent = -1;
    public string Name = "";
    public string? RefId;            // precomp/image asset id
    public double Ip, Op, St;        // in/out/start (frames, comp time)
    public double Sr = 1;            // time stretch
    public LottieTransform Transform = new();
    public List<LottieShape> Shapes { get; } = new();
    public uint SolidColor;          // solid layers
    public int SolidW, SolidH;
}

public sealed class LottieTransform
{
    public AnimProp Position = AnimProp.Const2(0, 0);
    public AnimProp? PosX, PosY;     // split position (bodymovin "s":true)
    public AnimProp Anchor = AnimProp.Const2(0, 0);
    public AnimProp Scale = AnimProp.Const2(100, 100);
    public AnimProp Rotation = AnimProp.Const(0);
    public AnimProp Opacity = AnimProp.Const(100);

    public (double x, double y) PositionAt(double frame)
    {
        if (PosX is not null || PosY is not null)
            return (PosX?.Scalar(frame) ?? 0, PosY?.Scalar(frame) ?? 0);
        var p = Position.Eval(frame);
        return (p.Length > 0 ? p[0] : 0, p.Length > 1 ? p[1] : 0);
    }
}

public abstract class LottieShape { public string Kind = ""; }

public sealed class ShapeGroup : LottieShape { public List<LottieShape> Items { get; } = new(); public LottieTransform Transform = new(); }
public sealed class ShapePath : LottieShape { public AnimProp Path = new(); }
public sealed class ShapeRect : LottieShape { public AnimProp Position = AnimProp.Const2(0,0); public AnimProp Size = AnimProp.Const2(0,0); public AnimProp Radius = AnimProp.Const(0); }
public sealed class ShapeEllipse : LottieShape { public AnimProp Position = AnimProp.Const2(0,0); public AnimProp Size = AnimProp.Const2(0,0); }
public sealed class ShapeFill : LottieShape { public AnimProp Color = new(); public AnimProp Opacity = AnimProp.Const(100); public int FillRule = 1; }
public sealed class ShapeStroke : LottieShape { public AnimProp Color = new(); public AnimProp Opacity = AnimProp.Const(100); public AnimProp Width = AnimProp.Const(1); public int Cap = 2, Join = 2; }
public sealed class ShapeTrim : LottieShape { public AnimProp Start = AnimProp.Const(0); public AnimProp End = AnimProp.Const(100); public AnimProp Offset = AnimProp.Const(0); }
public sealed class ShapeGradient : LottieShape
{
    public bool IsStroke;
    public int GradientType = 1;     // 1 linear, 2 radial
    public AnimProp Start = AnimProp.Const2(0,0);
    public AnimProp End = AnimProp.Const2(0,0);
    public AnimProp Colors = new();  // flattened [pos,r,g,b, ...]
    public int StopCount;
    public AnimProp Opacity = AnimProp.Const(100);
    public AnimProp Width = AnimProp.Const(1);
}

/// <summary>A Lottie animatable property: static values or keyframes with bezier easing.</summary>
public sealed class AnimProp
{
    public bool Animated;
    public double[] Static = Array.Empty<double>();
    public List<Kf> Keys = new();

    public struct Kf
    {
        public double T;             // frame
        public double[] S;           // start value (this kf)
        public double[]? E;          // legacy end value
        public double[]? Ox, Oy, Ix, Iy;  // bezier handles (per-dim; often single)
        public bool Hold;
        public PathData? Path;       // for path keyframes
    }

    public PathData? StaticPath;

    public static AnimProp Const(double v) => new() { Static = new[] { v } };
    public static AnimProp Const2(double a, double b) => new() { Static = new[] { a, b } };

    /// <summary>Evaluate to a vector at the given frame.</summary>
    public double[] Eval(double frame)
    {
        if (!Animated || Keys.Count == 0) return Static;
        if (frame <= Keys[0].T) return Keys[0].S;
        if (frame >= Keys[^1].T) return (Keys[^1].E ?? Keys[^1].S);
        int i = 0;
        while (i < Keys.Count - 1 && Keys[i + 1].T <= frame) i++;
        var a = Keys[i]; var b = Keys[i + 1];
        if (a.Hold) return a.S;
        double span = b.T - a.T;
        double u = span <= 0 ? 0 : (frame - a.T) / span;
        double e = Ease(u, a);
        var av = a.S; var bv = a.E ?? b.S;
        int n = Math.Min(av.Length, bv.Length);
        var outv = new double[n];
        for (int k = 0; k < n; k++) outv[k] = av[k] + (bv[k] - av[k]) * e;
        return outv;
    }

    public double Scalar(double frame) { var v = Eval(frame); return v.Length > 0 ? v[0] : 0; }

    private static double Ease(double u, Kf a)
    {
        if (a.Ox is null || a.Ix is null || a.Ox.Length == 0 || a.Ix.Length == 0) return u;   // linear
        double ox = a.Ox[0], oy = (a.Oy ?? a.Ox)[0], ix = a.Ix[0], iy = (a.Iy ?? a.Ix)[0];
        return CubicBezier(u, ox, oy, ix, iy);
    }

    private static double CubicBezier(double u, double x1, double y1, double x2, double y2)
    {
        if (u <= 0) return 0; if (u >= 1) return 1;
        double t = u;
        for (int i = 0; i < 8; i++)
        {
            double x = B(t, x1, x2) - u, dx = Bd(t, x1, x2);
            if (Math.Abs(x) < 1e-5 || Math.Abs(dx) < 1e-9) break;
            t = Math.Clamp(t - x / dx, 0, 1);
        }
        return B(t, y1, y2);
    }
    private static double B(double t, double a, double b) { double mt = 1 - t; return 3 * mt * mt * t * a + 3 * mt * t * t * b + t * t * t; }
    private static double Bd(double t, double a, double b) { double mt = 1 - t; return 3 * mt * mt * a + 6 * mt * t * (b - a) + 3 * t * t * (1 - b); }
}

/// <summary>A bezier contour: absolute vertices v[], relative in-tangents i[], out-tangents o[].</summary>
public sealed class PathData
{
    public double[][] V = Array.Empty<double[]>();
    public double[][] I = Array.Empty<double[]>();
    public double[][] O = Array.Empty<double[]>();
    public bool Closed;
}
