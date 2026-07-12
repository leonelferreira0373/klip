using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;

namespace Klip.Engine.Rive;

/// <summary>
/// Renders a RiveArtboard to Skia. Resolves the component hierarchy (parentId), composes world
/// transforms, builds SKPaths from parametric shapes + point-paths (straight/cubic vertices),
/// and paints fills/strokes with solid colors + linear/radial gradients. Animations mutate object
/// properties in place before a frame is drawn (see RivePlayer).
/// </summary>
public sealed class RiveRenderer
{
    private readonly RiveArtboard _ab;
    private readonly List<RiveObject> _objs;
    private readonly int[] _parent;                    // parent index per object (-1 = artboard/root)
    private readonly List<int>[] _children;
    private readonly SKMatrix[] _world;
    private readonly double[] _worldOpacity;

    public RiveRenderer(RiveArtboard ab)
    {
        _ab = ab;
        _objs = ab.Objects;
        int n = _objs.Count;
        _parent = new int[n];
        _children = new List<int>[n];
        _world = new SKMatrix[n];
        _worldOpacity = new double[n];
        for (int i = 0; i < n; i++) _children[i] = new List<int>();

        for (int i = 1; i < n; i++)                    // 0 = artboard
        {
            int pid = (int)_objs[i].U(RiveKeys.ParentIdKey, 0);
            if (pid < 0 || pid >= n) pid = 0;
            _parent[i] = pid;
            _children[pid].Add(i);
        }
    }

    public double Width => _ab.Width > 0 ? _ab.Width : 500;
    public double Height => _ab.Height > 0 ? _ab.Height : 500;

    /// <summary>Draw the artboard into the given canvas rect, fitting the actual content bounds
    /// (robust to unusual artboard origins; nothing is clipped away when compositing into KLIP).</summary>
    public void Render(SKCanvas canvas, SKRect dst)
    {
        ComputeTransforms();

        // pass 1: measure the union bounds of all shape geometry in world space
        var bounds = SKRect.Empty;
        bool any = false;
        for (int i = 0; i < _objs.Count; i++)
            if (_objs[i].TypeKey == RiveKeys.Shape)
            {
                using var g = BuildShapeGeometry(i);
                if (g is null || g.IsEmpty) continue;
                var b = g.TightBounds;
                if (!any) { bounds = b; any = true; } else bounds.Union(b);
            }
        if (!any) return;
        if (bounds.Width < 1) bounds.Right = bounds.Left + 1;
        if (bounds.Height < 1) bounds.Bottom = bounds.Top + 1;

        double s = Math.Min(dst.Width / bounds.Width, dst.Height / bounds.Height);
        float ox = dst.Left + (float)((dst.Width - bounds.Width * s) / 2);
        float oy = dst.Top + (float)((dst.Height - bounds.Height * s) / 2);

        canvas.Save();
        canvas.Translate(ox, oy);
        canvas.Scale((float)s);
        canvas.Translate(-bounds.Left, -bounds.Top);

        for (int i = 0; i < _objs.Count; i++)
            if (_objs[i].TypeKey == RiveKeys.Shape)
                DrawShape(canvas, i);

        canvas.Restore();
    }

    /// <summary>Aggregate a shape's child paths into one world-space SKPath (for measuring + drawing).</summary>
    private SKPath? BuildShapeGeometry(int shapeIx)
    {
        var geom = new SKPath { FillType = SKPathFillType.Winding };
        foreach (var ci in _children[shapeIx])
        {
            var o = _objs[ci];
            SKPath? p = o.TypeKey switch
            {
                RiveKeys.PointsPath => BuildPointsPath(ci),
                RiveKeys.Rectangle => BuildRect(ci),
                RiveKeys.Ellipse => BuildEllipse(ci),
                RiveKeys.Triangle => BuildTriangle(ci),
                _ => null,
            };
            if (p is null) continue;
            p.Transform(_world[ci]);
            geom.AddPath(p);
            p.Dispose();
        }
        return geom.IsEmpty ? null : geom;
    }

    private void ComputeTransforms()
    {
        _world[0] = SKMatrix.CreateIdentity();
        _worldOpacity[0] = _objs[0].D(RiveKeys.OpacityKey, 1);
        for (int i = 1; i < _objs.Count; i++) ComputeWorld(i);
    }

    private bool[]? _done;
    private void ComputeWorld(int i)
    {
        _done ??= new bool[_objs.Count];
        if (_done[i]) return;
        int p = _parent[i];
        if (p != 0 && !_done[p]) ComputeWorld(p);
        var o = _objs[i];
        var local = SKMatrix.CreateIdentity();
        if (IsTransform(o.TypeKey))
        {
            float x = (float)o.D(RiveKeys.XKey), y = (float)o.D(RiveKeys.YKey);
            float rot = (float)o.D(RiveKeys.RotationKey);         // radians
            float scx = (float)o.D(RiveKeys.ScaleXKey, 1), scy = (float)o.D(RiveKeys.ScaleYKey, 1);
            local = SKMatrix.CreateTranslation(x, y);
            local = local.PreConcat(SKMatrix.CreateRotation(rot));
            local = local.PreConcat(SKMatrix.CreateScale(scx, scy));
        }
        _world[i] = _world[p].PreConcat(local);
        _worldOpacity[i] = _worldOpacity[p] * o.D(RiveKeys.OpacityKey, 1);
        _done[i] = true;
    }

    private static bool IsTransform(int typeKey) => typeKey is RiveKeys.Node or RiveKeys.Shape
        or RiveKeys.Path or RiveKeys.PointsPath or RiveKeys.ParametricPath
        or RiveKeys.Rectangle or RiveKeys.Ellipse or RiveKeys.Triangle;

    private void DrawShape(SKCanvas canvas, int shapeIx)
    {
        using var geom = BuildShapeGeometry(shapeIx);
        if (geom is null || geom.IsEmpty) return;

        double shapeOpacity = _worldOpacity[shapeIx];

        // paints: children Fill/Stroke of the shape
        foreach (var ci in _children[shapeIx])
        {
            var paint = _objs[ci];
            if (paint.TypeKey == RiveKeys.Fill) DrawPaint(canvas, geom, ci, shapeIx, shapeOpacity, false);
            else if (paint.TypeKey == RiveKeys.Stroke) DrawPaint(canvas, geom, ci, shapeIx, shapeOpacity, true);
        }
    }

    private void DrawPaint(SKCanvas canvas, SKPath geom, int paintIx, int shapeIx, double opacity, bool stroke)
    {
        var po = _objs[paintIx];
        if (!po.B(RiveKeys.IsVisibleKey, true)) return;

        using var paint = new SKPaint { IsAntialias = true, Style = stroke ? SKPaintStyle.Stroke : SKPaintStyle.Fill };
        if (stroke)
        {
            paint.StrokeWidth = (float)po.D(RiveKeys.ThicknessKey, 1);
            paint.StrokeCap = (int)po.U(RiveKeys.CapKey) switch { 1 => SKStrokeCap.Round, 2 => SKStrokeCap.Square, _ => SKStrokeCap.Butt };
            paint.StrokeJoin = (int)po.U(RiveKeys.JoinKey) switch { 1 => SKStrokeJoin.Round, 2 => SKStrokeJoin.Bevel, _ => SKStrokeJoin.Miter };
        }
        else
        {
            geom.FillType = (int)po.U(RiveKeys.FillRuleKey) == 1 ? SKPathFillType.EvenOdd : SKPathFillType.Winding;
        }

        // color source: child SolidColor / LinearGradient / RadialGradient of the paint
        int colorChild = _children[paintIx].FirstOrDefault(
            c => _objs[c].TypeKey is RiveKeys.SolidColor or RiveKeys.LinearGradient or RiveKeys.RadialGradient, -1);
        if (colorChild < 0) return;
        var cs = _objs[colorChild];

        if (cs.TypeKey == RiveKeys.SolidColor)
        {
            paint.Color = WithOpacity(cs.U(RiveKeys.SolidColorKey, 0xFF000000), opacity);
        }
        else
        {
            var stops = _children[colorChild].Where(c => _objs[c].TypeKey == RiveKeys.GradientStop)
                .Select(c => _objs[c]).OrderBy(s => s.D(RiveKeys.StopPositionKey)).ToList();
            if (stops.Count == 0) return;
            var colors = stops.Select(s => (SKColor)WithOpacity(s.U(RiveKeys.StopColorKey, 0xFF000000), opacity)).ToArray();
            var pos = stops.Select(s => (float)s.D(RiveKeys.StopPositionKey)).ToArray();

            // gradient coords are in shape-local space → map to world
            var wm = _world[shapeIx];
            var p1 = wm.MapPoint((float)cs.D(RiveKeys.GradStartXKey), (float)cs.D(RiveKeys.GradStartYKey));
            var p2 = wm.MapPoint((float)cs.D(RiveKeys.GradEndXKey), (float)cs.D(RiveKeys.GradEndYKey));
            paint.Shader = cs.TypeKey == RiveKeys.RadialGradient
                ? SKShader.CreateRadialGradient(p1, Dist(p1, p2), colors, pos, SKShaderTileMode.Clamp)
                : SKShader.CreateLinearGradient(p1, p2, colors, pos, SKShaderTileMode.Clamp);
        }

        canvas.DrawPath(geom, paint);
        paint.Shader?.Dispose();
    }

    // ---- geometry builders (local space) ----
    private SKPath? BuildPointsPath(int ix)
    {
        var verts = _children[ix].Where(c => IsVertex(_objs[c].TypeKey)).Select(c => _objs[c]).ToList();
        if (verts.Count < 2) return null;
        bool closed = _objs[ix].B(RiveKeys.IsClosedKey, false);

        var pts = verts.Select(ToVertex).ToList();
        var path = new SKPath();
        path.MoveTo(pts[0].pos);
        int count = closed ? pts.Count : pts.Count - 1;
        for (int i = 0; i < count; i++)
        {
            var a = pts[i]; var b = pts[(i + 1) % pts.Count];
            if (a.hasOut || b.hasIn)
                path.CubicTo(a.hasOut ? a.outCtrl : a.pos, b.hasIn ? b.inCtrl : b.pos, b.pos);
            else
                path.LineTo(b.pos);
        }
        if (closed) path.Close();
        return path;
    }

    private struct V { public SKPoint pos, inCtrl, outCtrl; public bool hasIn, hasOut; }

    private V ToVertex(RiveObject o)
    {
        var v = new V { pos = new SKPoint((float)o.D(RiveKeys.VertexXKey), (float)o.D(RiveKeys.VertexYKey)) };
        switch (o.TypeKey)
        {
            case RiveKeys.CubicMirroredVertex:
            {
                double rot = o.D(RiveKeys.MirrorRotationKey), d = o.D(RiveKeys.MirrorDistanceKey);
                v.outCtrl = Polar(v.pos, rot, d); v.inCtrl = Polar(v.pos, rot + Math.PI, d);
                v.hasIn = v.hasOut = true; break;
            }
            case RiveKeys.CubicAsymmetricVertex:
            {
                double rot = o.D(RiveKeys.AsymRotationKey);
                v.outCtrl = Polar(v.pos, rot, o.D(RiveKeys.AsymOutDistanceKey));
                v.inCtrl = Polar(v.pos, rot + Math.PI, o.D(RiveKeys.AsymInDistanceKey));
                v.hasIn = v.hasOut = true; break;
            }
            case RiveKeys.CubicDetachedVertex:
            {
                v.outCtrl = Polar(v.pos, o.D(RiveKeys.OutRotationKey), o.D(RiveKeys.OutDistanceKey));
                v.inCtrl = Polar(v.pos, o.D(RiveKeys.InRotationKey), o.D(RiveKeys.InDistanceKey));
                v.hasIn = v.hasOut = true; break;
            }
            default: // StraightVertex / plain
                v.inCtrl = v.outCtrl = v.pos; break;
        }
        return v;
    }

    private static SKPoint Polar(SKPoint o, double angle, double dist)
        => new((float)(o.X + Math.Cos(angle) * dist), (float)(o.Y + Math.Sin(angle) * dist));

    private static bool IsVertex(int t) => t is RiveKeys.StraightVertex or RiveKeys.CubicVertex
        or RiveKeys.CubicDetachedVertex or RiveKeys.CubicMirroredVertex or RiveKeys.CubicAsymmetricVertex or RiveKeys.PathVertex;

    private SKPath BuildRect(int ix)
    {
        var o = _objs[ix];
        float w = (float)o.D(RiveKeys.PpWidthKey), h = (float)o.D(RiveKeys.PpHeightKey);
        float oxf = (float)o.D(RiveKeys.PpOriginXKey, 0.5), oyf = (float)o.D(RiveKeys.PpOriginYKey, 0.5);
        var rect = new SKRect(-w * oxf, -h * oyf, w - w * oxf, h - h * oyf);
        float r = (float)o.D(RiveKeys.CornerTL);
        var path = new SKPath();
        if (r > 0) path.AddRoundRect(rect, r, r); else path.AddRect(rect);
        return path;
    }

    private SKPath BuildEllipse(int ix)
    {
        var o = _objs[ix];
        float w = (float)o.D(RiveKeys.PpWidthKey), h = (float)o.D(RiveKeys.PpHeightKey);
        float oxf = (float)o.D(RiveKeys.PpOriginXKey, 0.5), oyf = (float)o.D(RiveKeys.PpOriginYKey, 0.5);
        var path = new SKPath();
        path.AddOval(new SKRect(-w * oxf, -h * oyf, w - w * oxf, h - h * oyf));
        return path;
    }

    private SKPath BuildTriangle(int ix)
    {
        var o = _objs[ix];
        float w = (float)o.D(RiveKeys.PpWidthKey), h = (float)o.D(RiveKeys.PpHeightKey);
        float oxf = (float)o.D(RiveKeys.PpOriginXKey, 0.5), oyf = (float)o.D(RiveKeys.PpOriginYKey, 0.5);
        float l = -w * oxf, t = -h * oyf, rgt = w - w * oxf, b = h - h * oyf;
        var path = new SKPath();
        path.MoveTo((l + rgt) / 2, t); path.LineTo(rgt, b); path.LineTo(l, b); path.Close();
        return path;
    }

    private static uint WithOpacity(uint argb, double op)
    {
        byte a = (byte)Math.Clamp(((argb >> 24) & 0xFF) * op, 0, 255);
        return (argb & 0x00FFFFFF) | ((uint)a << 24);
    }

    private static float Dist(SKPoint a, SKPoint b) { float dx = a.X - b.X, dy = a.Y - b.Y; return (float)Math.Sqrt(dx * dx + dy * dy); }
}
