using System;
using System.Collections.Generic;
using SkiaSharp;

namespace Klip.Engine;

/// <summary>
/// Shape-morph REAL (não o fake "colapsar-e-florescer"): decompõe cada path nos seus CONTORNOS,
/// reamostra cada contorno por comprimento de arco (N pontos), emparelha os contornos de A com os
/// de B (maior área primeiro → contorno exterior casa com exterior, buracos com buracos), escolhe
/// o deslocamento cíclico que minimiza a torção por par, e faz lerp ponto-a-ponto. Contornos a mais
/// num dos lados colapsam para / nascem do seu centróide (fade). PRESERVA BURACOS (multi-contorno).
/// Robusto e swim-resistant. Caminho de crescimento: correspondência bezier verdadeira + ARAP.
/// </summary>
public static class PathMorph
{
    private const int DefaultN = 192;

    /// <summary>Reamostra TODOS os contornos de <paramref name="path"/> para N pontos por comprimento de arco.</summary>
    public static List<SKPoint[]> SampleContours(SKPath path, int n)
    {
        var result = new List<SKPoint[]>();
        using var m = new SKPathMeasure(path, forceClosed: true);
        do
        {
            float len = m.Length;
            if (len <= 0) continue;                 // contorno degenerado (ponto) — ignora
            var pts = new SKPoint[n];
            for (int i = 0; i < n; i++)
            {
                m.GetPosition(len * i / n, out var p);
                pts[i] = p;
            }
            result.Add(pts);
        } while (m.NextContour());

        if (result.Count == 0)                      // path sem comprimento → nuvem no centro do bounds
        {
            var b = path.Bounds;
            var pts = new SKPoint[n];
            for (int i = 0; i < n; i++) pts[i] = new SKPoint(b.MidX, b.MidY);
            result.Add(pts);
        }
        return result;
    }

    /// <summary>Retro-compat: primeiro contorno reamostrado (o antigo comportamento single-contour).</summary>
    public static SKPoint[] Sample(SKPath path, int n) => SampleContours(path, n)[0];

    private static SKPoint Centroid(SKPoint[] p)
    {
        double x = 0, y = 0;
        foreach (var q in p) { x += q.X; y += q.Y; }
        return new SKPoint((float)(x / p.Length), (float)(y / p.Length));
    }

    /// <summary>Área com sinal (fórmula do sapateiro) — a magnitude ordena exterior vs buracos.</summary>
    private static double AbsArea(SKPoint[] p)
    {
        double a = 0;
        for (int i = 0; i < p.Length; i++)
        {
            var u = p[i]; var v = p[(i + 1) % p.Length];
            a += (double)u.X * v.Y - (double)v.X * u.Y;
        }
        return Math.Abs(a) / 2;
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
                if (err >= bestErr) break;          // early-exit: já pior que o melhor
            }
            if (err < bestErr) { bestErr = err; best = off; }
        }
        return best;
    }

    /// <summary>Contorno degenerado: n cópias de <paramref name="c"/> (usado quando falta contorno de um lado).</summary>
    private static SKPoint[] PointCloud(SKPoint c, int n)
    {
        var p = new SKPoint[n];
        for (int i = 0; i < n; i++) p[i] = c;
        return p;
    }

    /// <summary>Path interpolado de <paramref name="a"/> para <paramref name="b"/> em t∈[0,1], preservando buracos/contornos.</summary>
    public static SKPath Interpolate(SKPath a, SKPath b, float t, int n = DefaultN)
    {
        var ca = SampleContours(a, n);
        var cb = SampleContours(b, n);

        // Emparelha por área decrescente: exterior↔exterior, buraco↔buraco. Preserva a ordem/winding de cada.
        ca.Sort((u, v) => AbsArea(v).CompareTo(AbsArea(u)));
        cb.Sort((u, v) => AbsArea(v).CompareTo(AbsArea(u)));

        int pairs = Math.Max(ca.Count, cb.Count);
        var path = new SKPath();                    // FillType = Winding → contornos opostos = buraco
        for (int k = 0; k < pairs; k++)
        {
            // Contorno em falta num dos lados → colapsa para / nasce do centróide do outro (fade suave).
            SKPoint[] pa = k < ca.Count ? ca[k] : PointCloud(Centroid(cb[k]), n);
            SKPoint[] pb = k < cb.Count ? cb[k] : PointCloud(Centroid(ca[k]), n);

            int off = BestOffset(pa, pb);
            for (int i = 0; i < n; i++)
            {
                var p = pa[i];
                var q = pb[(i + off) % n];
                float x = p.X + (q.X - p.X) * t;
                float y = p.Y + (q.Y - p.Y) * t;
                if (i == 0) path.MoveTo(x, y); else path.LineTo(x, y);
            }
            path.Close();
        }
        return path;
    }
}
