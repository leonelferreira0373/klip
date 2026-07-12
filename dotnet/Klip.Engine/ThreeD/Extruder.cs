using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Clipper2Lib;
using LibTessDotNet;
using SkiaSharp;

namespace Klip.Engine.ThreeD;

/// <summary>
/// 2D vector path → 3D extruded mesh with beveled front/back edges.
/// Bevel rings are generated with Clipper2 InflatePaths (negative offset), whose cleanup
/// dissolves the self-intersections that break naive miter offsets on thin/concave shapes
/// (glyph necks etc.). Ring correspondence via arc-length resampling + best cyclic offset.
/// Output = interleaved [px,py,pz, nx,ny,nz] flat-shaded triangle soup, lit two-sided.
/// </summary>
public static class Extruder
{
    private const double CScale = 1000.0;   // clipper int scale

    public static float[] Build(SKPath path, float scale, float depth, float bevel, out int vertCount)
    {
        // UNIÃO de todos os contornos primeiro — glifos (ex. "K" da Segoe) vêm como contornos
        // SOBREPOSTOS; sem união, o even-odd cancela a sobreposição e paredes interiores furam a face.
        var rings = UnionAll(Flatten(path, scale));
        float zCapF = depth * 0.5f, zWallF = depth * 0.5f - bevel;
        float zCapB = -depth * 0.5f, zWallB = -depth * 0.5f + bevel;

        var tris = new List<float>(8192);
        var capRings = new List<List<Vector2>>();   // inset rings (front/back caps)

        foreach (var (contour, isHole) in rings)
        {
            int n = Math.Max(64, Math.Min(256, contour.Count * 2));
            var outer = Resample(contour, n);
            // bevel move a aresta PARA DENTRO do sólido: encolhe outers, EXPANDE furos
            var insetPieces = ClipperInset(contour, isHole ? -bevel : bevel);
            List<Vector2> inner;
            if (insetPieces is null || insetPieces.Count == 0)
            {
                inner = outer;              // bevel colapsou (forma fina) → sem bevel neste contorno
                capRings.Add(outer);
            }
            else
            {
                // correspondência LOCAL contra TODAS as peças do inset (o encolhimento pode
                // partir a forma em várias — ex. o pescoço do "K"); projetar só na maior
                // esticava quads pela face = sawtooth
                inner = outer.Select(p => ClosestOnPieces(insetPieces, p)).ToList();
                capRings.AddRange(insetPieces);   // caps = todas as peças Clipper limpas
            }

            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                Vector3 oFa = new(outer[i].X, outer[i].Y, zWallF), oFb = new(outer[j].X, outer[j].Y, zWallF);
                Vector3 oBa = new(outer[i].X, outer[i].Y, zWallB), oBb = new(outer[j].X, outer[j].Y, zWallB);
                Vector3 iFa = new(inner[i].X, inner[i].Y, zCapF), iFb = new(inner[j].X, inner[j].Y, zCapF);
                Vector3 iBa = new(inner[i].X, inner[i].Y, zCapB), iBb = new(inner[j].X, inner[j].Y, zCapB);

                Quad(tris, oFa, oFb, oBb, oBa);   // side wall
                Quad(tris, iFa, iFb, oFb, oFa);   // front bevel band
                Quad(tris, oBa, oBb, iBb, iBa);   // back bevel band
            }
        }

        AddCap(tris, capRings, zCapF, new Vector3(0, 0, 1), reversed: false);
        AddCap(tris, capRings, zCapB, new Vector3(0, 0, -1), reversed: true);

        vertCount = tris.Count / 6;
        return tris.ToArray();
    }

    private static Vector2 ClosestOnPoly(List<Vector2> poly, Vector2 p)
    {
        float bestD = float.MaxValue;
        Vector2 best = poly[0];
        for (int i = 0; i < poly.Count; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Count];
            var ab = b - a;
            float len2 = ab.LengthSquared();
            float t = len2 < 1e-12f ? 0f : Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f);
            var q = a + ab * t;
            float d = (p - q).LengthSquared();
            if (d < bestD) { bestD = d; best = q; }
        }
        return best;
    }

    private static double SignedArea(List<Vector2> p)
    {
        double a = 0;
        for (int i = 0; i < p.Count; i++)
        {
            var u = p[i]; var v = p[(i + 1) % p.Count];
            a += u.X * v.Y - v.X * u.Y;
        }
        return a * 0.5;
    }

    /// <summary>Une TODOS os contornos num sólido (NonZero) → anéis limpos + flag de furo.</summary>
    private static List<(List<Vector2> ring, bool isHole)> UnionAll(List<List<Vector2>> contours)
    {
        var subj = new Paths64();
        foreach (var c in contours)
        {
            var p = new Path64(c.Count);
            foreach (var v in c)
                p.Add(new Point64((long)Math.Round(v.X * CScale), (long)Math.Round(v.Y * CScale)));
            if (p.Count > 2) subj.Add(p);
        }
        var u = Clipper.Union(subj, FillRule.NonZero);
        var res = new List<(List<Vector2>, bool)>();
        foreach (var ring in u)
        {
            if (ring.Count < 3) continue;
            bool isHole = Clipper.Area(ring) < 0;
            res.Add((ring.Select(pt => new Vector2((float)(pt.X / CScale), (float)(pt.Y / CScale))).ToList(), isHole));
        }
        return res;
    }

    // ---- clipper inset (self-intersection-proof) ----
    private static List<List<Vector2>>? ClipperInset(List<Vector2> contour, float dist)
    {
        var p = new Path64(contour.Count);
        foreach (var v in contour)
            p.Add(new Point64((long)Math.Round(v.X * CScale), (long)Math.Round(v.Y * CScale)));
        // simplificar mata os micro-zigzags da quantização; Round nunca cria espigões de miter
        var subj = Clipper.SimplifyPaths(new Paths64 { p }, 2.0);
        var inflated = Clipper.InflatePaths(subj, -dist * CScale, JoinType.Round, EndType.Polygon);
        var pieces = new List<List<Vector2>>();
        foreach (var ring in inflated)
        {
            if (ring.Count < 3) continue;
            pieces.Add(ring.Select(pt => new Vector2((float)(pt.X / CScale), (float)(pt.Y / CScale))).ToList());
        }
        return pieces.Count == 0 ? null : pieces;
    }

    private static Vector2 ClosestOnPieces(List<List<Vector2>> pieces, Vector2 p)
    {
        Vector2 best = pieces[0][0];
        float bestD = float.MaxValue;
        foreach (var poly in pieces)
        {
            var q = ClosestOnPoly(poly, p);
            float d = (p - q).LengthSquared();
            if (d < bestD) { bestD = d; best = q; }
        }
        return best;
    }

    // ---- resample a closed polyline to n points by arc length ----
    private static List<Vector2> Resample(List<Vector2> pts, int n)
    {
        int m = pts.Count;
        var segLen = new double[m];
        double total = 0;
        for (int i = 0; i < m; i++)
        {
            segLen[i] = (pts[(i + 1) % m] - pts[i]).Length();
            total += segLen[i];
        }
        if (total <= 1e-9) return Enumerable.Repeat(pts[0], n).ToList();

        var res = new List<Vector2>(n);
        double step = total / n, acc = 0;
        int seg = 0;
        double segAcc = 0;
        for (int k = 0; k < n; k++)
        {
            double target = k * step;
            while (segAcc + segLen[seg] < target && seg < m - 1) { segAcc += segLen[seg]; seg++; }
            double local = segLen[seg] <= 1e-12 ? 0 : (target - segAcc) / segLen[seg];
            var a = pts[seg];
            var b = pts[(seg + 1) % m];
            res.Add(a + (b - a) * (float)local);
        }
        return res;
    }

    private static int BestOffset(List<Vector2> a, List<Vector2> b)
    {
        int n = a.Count, best = 0;
        double bestErr = double.MaxValue;
        for (int off = 0; off < n; off++)
        {
            double err = 0;
            for (int i = 0; i < n; i += 4)   // stride 4: rápido e suficiente
            {
                var d = a[i] - b[(i + off) % n];
                err += d.LengthSquared();
                if (err >= bestErr) break;
            }
            if (err < bestErr) { bestErr = err; best = off; }
        }
        return best;
    }

    private static List<Vector2> Rotate(List<Vector2> p, int off)
    {
        var r = new List<Vector2>(p.Count);
        for (int i = 0; i < p.Count; i++) r.Add(p[(i + off) % p.Count]);
        return r;
    }

    // ---- flatten ----
    public static List<List<Vector2>> Flatten(SKPath path, float scale)
    {
        var contours = new List<List<Vector2>>();
        List<Vector2>? cur = null;
        SKPoint last = default;
        using var it = path.CreateRawIterator();
        var pts = new SKPoint[4];
        SKPathVerb verb;
        while ((verb = it.Next(pts)) != SKPathVerb.Done)
        {
            switch (verb)
            {
                case SKPathVerb.Move:
                    if (cur is { Count: > 1 }) contours.Add(cur);
                    cur = new List<Vector2>();
                    last = pts[0];
                    cur.Add(V(pts[0], scale));
                    break;
                case SKPathVerb.Line:
                    cur!.Add(V(pts[1], scale)); last = pts[1]; break;
                case SKPathVerb.Quad:
                case SKPathVerb.Conic:
                    for (int i = 1; i <= 8; i++) cur!.Add(V(QuadPt(last, pts[1], pts[2], i / 8f), scale));
                    last = pts[2]; break;
                case SKPathVerb.Cubic:
                    for (int i = 1; i <= 12; i++) cur!.Add(V(CubicPt(last, pts[1], pts[2], pts[3], i / 12f), scale));
                    last = pts[3]; break;
                case SKPathVerb.Close:
                    break;
            }
        }
        if (cur is { Count: > 1 }) contours.Add(cur);
        foreach (var c in contours)
            if (c.Count > 1 && (c[0] - c[^1]).Length() < 1e-4f) c.RemoveAt(c.Count - 1);
        return contours;
    }

    private static Vector2 V(SKPoint p, float s) => new(p.X * s, p.Y * s);
    private static SKPoint QuadPt(SKPoint a, SKPoint b, SKPoint c, float t)
    { float u = 1 - t; return new(u * u * a.X + 2 * u * t * b.X + t * t * c.X, u * u * a.Y + 2 * u * t * b.Y + t * t * c.Y); }
    private static SKPoint CubicPt(SKPoint a, SKPoint b, SKPoint c, SKPoint d, float t)
    { float u = 1 - t; return new(u * u * u * a.X + 3 * u * u * t * b.X + 3 * u * t * t * c.X + t * t * t * d.X,
                                   u * u * u * a.Y + 3 * u * u * t * b.Y + 3 * u * t * t * c.Y + t * t * t * d.Y); }

    // ---- cap tessellation ----
    private static void AddCap(List<float> tris, List<List<Vector2>> contours, float z, Vector3 nrm, bool reversed)
    {
        var tess = new Tess();
        foreach (var c in contours)
        {
            var cv = new ContourVertex[c.Count];
            for (int i = 0; i < c.Count; i++)
                cv[i].Position = new LibTessDotNet.Vec3 { X = c[i].X, Y = c[i].Y, Z = 0 };
            tess.AddContour(cv, ContourOrientation.Original);
        }
        tess.Tessellate(WindingRule.EvenOdd, ElementType.Polygons, 3);
        for (int e = 0; e < tess.ElementCount; e++)
        {
            int i0 = tess.Elements[e * 3 + 0], i1 = tess.Elements[e * 3 + 1], i2 = tess.Elements[e * 3 + 2];
            if (i0 < 0 || i1 < 0 || i2 < 0) continue;
            var a = tess.Vertices[i0].Position; var b = tess.Vertices[i1].Position; var d = tess.Vertices[i2].Position;
            Vector3 pa = new(a.X, a.Y, z), pb = new(b.X, b.Y, z), pc = new(d.X, d.Y, z);
            if (reversed) (pb, pc) = (pc, pb);
            Push(tris, pa, nrm); Push(tris, pb, nrm); Push(tris, pc, nrm);
        }
    }

    private static void Quad(List<float> t, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    { Tri(t, a, b, c); Tri(t, a, c, d); }

    private static void Tri(List<float> t, Vector3 a, Vector3 b, Vector3 c)
    {
        var cross = Vector3.Cross(b - a, c - a);
        var n = cross.LengthSquared() < 1e-12f ? Vector3.UnitZ : Vector3.Normalize(cross);
        Push(t, a, n); Push(t, b, n); Push(t, c, n);
    }

    private static void Push(List<float> t, Vector3 p, Vector3 n)
    { t.Add(p.X); t.Add(p.Y); t.Add(p.Z); t.Add(n.X); t.Add(n.Y); t.Add(n.Z); }
}
