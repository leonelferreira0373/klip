using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;

namespace Klip.Engine.Lottie;

/// <summary>Renders a LottieDoc frame to Skia: layer transforms (+parenting, opacity), shape groups
/// with paths/rects/ellipses, solid + gradient fills/strokes, and trim paths.</summary>
public sealed class LottieRenderer
{
    private readonly LottieDoc _doc;
    public LottieRenderer(LottieDoc doc) => _doc = doc;

    public double Width => _doc.W > 0 ? _doc.W : 512;
    public double Height => _doc.H > 0 ? _doc.H : 512;

    /// <summary>Draw the composition at frame `frame` into dst (fit-contain of the comp box).</summary>
    public void Render(SKCanvas canvas, SKRect dst, double frame)
    {
        double s = Math.Min(dst.Width / Width, dst.Height / Height);
        float ox = dst.Left + (float)((dst.Width - Width * s) / 2);
        float oy = dst.Top + (float)((dst.Height - Height * s) / 2);
        canvas.Save();
        canvas.Translate(ox, oy);
        canvas.Scale((float)s);
        canvas.ClipRect(new SKRect(0, 0, (float)Width, (float)Height));
        RenderLayers(canvas, _doc.Layers, frame);
        canvas.Restore();
    }

    private void RenderLayers(SKCanvas canvas, List<LottieLayer> layers, double frame)
    {
        // draw back-to-front: bodymovin lists topmost first, so reverse
        var byIndex = layers.Where(l => l.Index >= 0).ToDictionary(l => l.Index);
        for (int i = layers.Count - 1; i >= 0; i--)
        {
            var layer = layers[i];
            if (frame < layer.Ip || frame >= layer.Op) continue;    // out of layer lifetime
            if (layer.Type is not (4 or 1 or 0)) continue;          // shape / solid / precomp only (v1)

            var m = WorldMatrix(layer, byIndex, frame, out double opacity);
            if (opacity <= 0.003) continue;

            canvas.Save();
            canvas.Concat(ref m);

            if (layer.Type == 4)
                RenderShapes(canvas, layer.Shapes, frame, opacity);
            else if (layer.Type == 1 && layer.SolidW > 0)
            {
                using var p = new SKPaint { Color = WithA(layer.SolidColor, opacity), IsAntialias = true };
                canvas.DrawRect(0, 0, layer.SolidW, layer.SolidH, p);
            }
            else if (layer.Type == 0 && layer.RefId is string rid && _doc.Assets.TryGetValue(rid, out var asset))
                RenderLayers(canvas, asset.Layers, frame);

            canvas.Restore();
        }
    }

    private SKMatrix WorldMatrix(LottieLayer layer, Dictionary<int, LottieLayer> byIndex, double frame, out double opacity)
    {
        var m = LocalMatrix(layer.Transform, frame, out opacity);
        int guard = 0;
        int parent = layer.Parent;
        while (parent >= 0 && byIndex.TryGetValue(parent, out var pl) && guard++ < 32)
        {
            var pm = LocalMatrix(pl.Transform, frame, out _);
            m = pm.PreConcat(m);
            parent = pl.Parent;
        }
        return m;
    }

    private static SKMatrix LocalMatrix(LottieTransform t, double frame, out double opacity)
    {
        var (pxD, pyD) = t.PositionAt(frame);
        var a = t.Anchor.Eval(frame);
        var sc = t.Scale.Eval(frame);
        double rot = t.Rotation.Scalar(frame);
        opacity = t.Opacity.Scalar(frame) / 100.0;

        float px = (float)pxD, py = (float)pyD;
        float ax = a.Length > 0 ? (float)a[0] : 0, ay = a.Length > 1 ? (float)a[1] : 0;
        float sx = sc.Length > 0 ? (float)(sc[0] / 100.0) : 1, sy = sc.Length > 1 ? (float)(sc[1] / 100.0) : 1;

        var m = SKMatrix.CreateTranslation(px, py);
        m = m.PreConcat(SKMatrix.CreateRotationDegrees((float)rot));
        m = m.PreConcat(SKMatrix.CreateScale(sx, sy));
        m = m.PreConcat(SKMatrix.CreateTranslation(-ax, -ay));
        return m;
    }

    // ---- shapes ----
    private void RenderShapes(SKCanvas canvas, List<LottieShape> shapes, double frame, double opacity)
    {
        // a shape list is a sequence of groups (and possibly bare shapes); render each group
        foreach (var sh in shapes)
        {
            if (sh is ShapeGroup g) RenderGroup(canvas, g, frame, opacity);
            else if (shapes.Any(x => x is ShapeFill or ShapeStroke or ShapeGradient))
            { /* bare shapes handled by wrapping below */ }
        }
        // handle a layer whose shapes are bare (path + fill directly, no group)
        if (!shapes.Any(s => s is ShapeGroup))
            RenderGroupItems(canvas, shapes, frame, opacity);
    }

    private void RenderGroup(SKCanvas canvas, ShapeGroup g, double frame, double parentOpacity)
    {
        var m = LocalMatrix(g.Transform, frame, out double gop);
        canvas.Save();
        canvas.Concat(ref m);
        RenderGroupItems(canvas, g.Items, frame, parentOpacity * gop);
        canvas.Restore();
    }

    private void RenderGroupItems(SKCanvas canvas, List<LottieShape> items, double frame, double opacity)
    {
        // collect geometry that precedes each paint (bodymovin: paints apply to prior paths in the group)
        using var geom = new SKPath { FillType = SKPathFillType.Winding };
        ShapeTrim? trim = null;
        foreach (var it in items)
        {
            switch (it)
            {
                case ShapePath sp: AppendPath(geom, EvalPath(sp.Path, frame)); break;
                case ShapeRect rc: geom.AddPath(BuildRect(rc, frame)); break;
                case ShapeEllipse el: geom.AddPath(BuildEllipse(el, frame)); break;
                case ShapeTrim tm: trim = tm; break;
                case ShapeGroup sub: RenderGroup(canvas, sub, frame, opacity); break;
            }
        }
        if (geom.IsEmpty) { PaintOnly(canvas, items, geom, frame, opacity); return; }

        using var drawGeom = ApplyTrim(geom, trim, frame);

        foreach (var it in items)
        {
            if (it is ShapeFill fl) DrawFill(canvas, drawGeom, fl, frame, opacity);
            else if (it is ShapeStroke st) DrawStroke(canvas, drawGeom, st, frame, opacity);
            else if (it is ShapeGradient gr) DrawGradient(canvas, drawGeom, gr, frame, opacity);
        }
    }

    private void PaintOnly(SKCanvas canvas, List<LottieShape> items, SKPath geom, double frame, double opacity) { }

    private SKPath ApplyTrim(SKPath geom, ShapeTrim? trim, double frame)
    {
        if (trim is null) return new SKPath(geom);
        double a = trim.Start.Scalar(frame) / 100.0, b = trim.End.Scalar(frame) / 100.0, off = trim.Offset.Scalar(frame) / 360.0;
        a = Math.Clamp(a + off, 0, 1); b = Math.Clamp(b + off, 0, 1);
        if (a > b) (a, b) = (b, a);
        if (a <= 0.0001 && b >= 0.9999) return new SKPath(geom);
        using var meas = new SKPathMeasure(geom, false);
        float len = meas.Length;
        var outp = new SKPath();
        meas.GetSegment((float)(a * len), (float)(b * len), outp, true);
        return outp;
    }

    private static void AppendPath(SKPath geom, PathData? pd)
    {
        if (pd is null || pd.V.Length == 0) return;
        var v = pd.V; var inh = pd.I; var outh = pd.O;
        int n = v.Length;
        geom.MoveTo((float)v[0][0], (float)v[0][1]);
        int segs = pd.Closed ? n : n - 1;
        for (int i = 0; i < segs; i++)
        {
            int j = (i + 1) % n;
            var a = v[i]; var b = v[j];
            var ao = outh.Length > i ? outh[i] : new double[] { 0, 0 };
            var bi = inh.Length > j ? inh[j] : new double[] { 0, 0 };
            geom.CubicTo((float)(a[0] + ao[0]), (float)(a[1] + ao[1]),
                         (float)(b[0] + bi[0]), (float)(b[1] + bi[1]),
                         (float)b[0], (float)b[1]);
        }
        if (pd.Closed) geom.Close();
    }

    private static PathData? EvalPath(AnimProp prop, double frame)
    {
        if (!prop.Animated) return prop.StaticPath;
        if (prop.Keys.Count == 0) return null;
        if (frame <= prop.Keys[0].T) return prop.Keys[0].Path;
        if (frame >= prop.Keys[^1].T) return prop.Keys[^1].Path;
        int i = 0;
        while (i < prop.Keys.Count - 1 && prop.Keys[i + 1].T <= frame) i++;
        var a = prop.Keys[i]; var b = prop.Keys[i + 1];
        if (a.Hold || a.Path is null || b.Path is null) return a.Path;
        double span = b.T - a.T, u = span <= 0 ? 0 : (frame - a.T) / span;
        return LerpPath(a.Path, b.Path, u);
    }

    private static PathData LerpPath(PathData a, PathData b, double t)
    {
        int n = Math.Min(a.V.Length, b.V.Length);
        var pd = new PathData { Closed = a.Closed, V = new double[n][], I = new double[n][], O = new double[n][] };
        for (int i = 0; i < n; i++)
        {
            pd.V[i] = Lerp2(a.V[i], b.V[i], t);
            pd.I[i] = Lerp2(a.I.Length > i ? a.I[i] : Zero, b.I.Length > i ? b.I[i] : Zero, t);
            pd.O[i] = Lerp2(a.O.Length > i ? a.O[i] : Zero, b.O.Length > i ? b.O[i] : Zero, t);
        }
        return pd;
    }
    private static readonly double[] Zero = { 0, 0 };
    private static double[] Lerp2(double[] a, double[] b, double t)
        => new[] { a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t };

    private static SKPath BuildRect(ShapeRect rc, double frame)
    {
        var p = rc.Position.Eval(frame); var s = rc.Size.Eval(frame);
        float cx = p.Length > 0 ? (float)p[0] : 0, cy = p.Length > 1 ? (float)p[1] : 0;
        float w = s.Length > 0 ? (float)s[0] : 0, h = s.Length > 1 ? (float)s[1] : 0;
        float r = (float)rc.Radius.Scalar(frame);
        var rect = new SKRect(cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2);
        var path = new SKPath();
        if (r > 0) path.AddRoundRect(rect, r, r); else path.AddRect(rect);
        return path;
    }

    private static SKPath BuildEllipse(ShapeEllipse el, double frame)
    {
        var p = el.Position.Eval(frame); var s = el.Size.Eval(frame);
        float cx = p.Length > 0 ? (float)p[0] : 0, cy = p.Length > 1 ? (float)p[1] : 0;
        float w = s.Length > 0 ? (float)s[0] : 0, h = s.Length > 1 ? (float)s[1] : 0;
        var path = new SKPath();
        path.AddOval(new SKRect(cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2));
        return path;
    }

    private static void DrawFill(SKCanvas canvas, SKPath geom, ShapeFill fl, double frame, double opacity)
    {
        geom.FillType = fl.FillRule == 2 ? SKPathFillType.EvenOdd : SKPathFillType.Winding;
        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill,
            Color = ColorOf(fl.Color.Eval(frame), fl.Opacity.Scalar(frame) / 100.0 * opacity) };
        canvas.DrawPath(geom, paint);
    }

    private static void DrawStroke(SKCanvas canvas, SKPath geom, ShapeStroke st, double frame, double opacity)
    {
        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke,
            StrokeWidth = (float)st.Width.Scalar(frame),
            Color = ColorOf(st.Color.Eval(frame), st.Opacity.Scalar(frame) / 100.0 * opacity),
            StrokeCap = st.Cap switch { 1 => SKStrokeCap.Butt, 3 => SKStrokeCap.Square, _ => SKStrokeCap.Round },
            StrokeJoin = st.Join switch { 1 => SKStrokeJoin.Miter, 3 => SKStrokeJoin.Bevel, _ => SKStrokeJoin.Round } };
        canvas.DrawPath(geom, paint);
    }

    private static void DrawGradient(SKCanvas canvas, SKPath geom, ShapeGradient g, double frame, double opacity)
    {
        var st = g.Start.Eval(frame); var en = g.End.Eval(frame);
        var p1 = new SKPoint(st.Length > 0 ? (float)st[0] : 0, st.Length > 1 ? (float)st[1] : 0);
        var p2 = new SKPoint(en.Length > 0 ? (float)en[0] : 0, en.Length > 1 ? (float)en[1] : 0);
        var raw = g.Colors.Eval(frame);
        int stops = g.StopCount > 0 ? g.StopCount : raw.Length / 4;
        if (stops < 2) return;
        var colors = new SKColor[stops]; var pos = new float[stops];
        double a = g.Opacity.Scalar(frame) / 100.0 * opacity;
        for (int i = 0; i < stops; i++)
        {
            int o = i * 4;
            pos[i] = o < raw.Length ? (float)raw[o] : (float)i / (stops - 1);
            byte R = C(raw, o + 1), G = C(raw, o + 2), B = C(raw, o + 3);
            colors[i] = new SKColor(R, G, B, (byte)Math.Clamp(a * 255, 0, 255));
        }
        using var paint = new SKPaint { IsAntialias = true, Style = g.IsStroke ? SKPaintStyle.Stroke : SKPaintStyle.Fill };
        if (g.IsStroke) paint.StrokeWidth = (float)g.Width.Scalar(frame);
        paint.Shader = g.GradientType == 2
            ? SKShader.CreateRadialGradient(p1, Dist(p1, p2), colors, pos, SKShaderTileMode.Clamp)
            : SKShader.CreateLinearGradient(p1, p2, colors, pos, SKShaderTileMode.Clamp);
        canvas.DrawPath(geom, paint);
        paint.Shader?.Dispose();
    }

    private static byte C(double[] a, int i) => (byte)Math.Clamp((i < a.Length ? a[i] : 0) * 255, 0, 255);
    private static SKColor ColorOf(double[] c, double alpha)
        => new(C(c, 0), C(c, 1), C(c, 2), (byte)Math.Clamp(alpha * 255, 0, 255));
    private static SKColor WithA(uint argb, double op)
        => new((byte)((argb >> 16) & 0xFF), (byte)((argb >> 8) & 0xFF), (byte)(argb & 0xFF), (byte)Math.Clamp(((argb >> 24) & 0xFF) * op, 0, 255));
    private static float Dist(SKPoint a, SKPoint b) { float dx = a.X - b.X, dy = a.Y - b.Y; return (float)Math.Sqrt(dx * dx + dy * dy); }
}
