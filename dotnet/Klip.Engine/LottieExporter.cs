using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Klip.Model;
using SkiaSharp;

namespace Klip.Engine;

/// <summary>
/// Export Lottie (bodymovin JSON) — camadas vetoriais com transform animado (posição split x/y,
/// rotação, escala, opacidade), fill/stroke e TRIM PATHS (a linha-a-desenhar-se vira "tm").
/// v1: sem imagens/3D/blur/masks (gate honesto — melhor omitir que renderizar errado).
/// </summary>
public static class LottieExporter
{
    public static (int exported, int skipped) Export(Comp comp, string path)
    {
        int frames = Math.Max(1, (int)Math.Round(comp.Duration * comp.Fps));
        var layersArr = new JsonArray();
        int skipped = 0;
        int ind = 0;

        foreach (var l in comp.Layers.Reverse())          // lottie: primeiro = topo
        {
            if (l.ImagePath is not null || l.ThreeD is not null) { skipped++; continue; }
            string d = l.Shape.Keys.Count > 0 ? l.Shape.Keys[0].PathD : "";
            using var p = SKPath.ParseSvgPathData(d);
            if (p is null || p.IsEmpty) { skipped++; continue; }

            var shapes = new JsonArray();
            foreach (var contour in PathContours(p)) shapes.Add(contour);

            if (l.TrimStart is not null || l.TrimEnd is not null)
                shapes.Add(new JsonObject
                {
                    ["ty"] = "tm",
                    ["s"] = Anim(l.TrimStart, 0, comp.Fps, v => v * 100),
                    ["e"] = Anim(l.TrimEnd, 1, comp.Fps, v => v * 100),
                    ["o"] = Static(0),
                    ["m"] = 1,
                });

            if (l.StrokeArgb is uint sc && l.StrokeWidth > 0)
                shapes.Add(new JsonObject
                {
                    ["ty"] = "st",
                    ["c"] = StaticColor(sc),
                    ["o"] = Static(((sc >> 24) & 0xFF) / 255.0 * 100),
                    ["w"] = Static(l.StrokeWidth),
                    ["lc"] = 2, ["lj"] = 2,
                });

            byte fa = (byte)((l.FillArgb >> 24) & 0xFF);
            if (fa > 0)
                shapes.Add(new JsonObject
                {
                    ["ty"] = "fl",
                    ["c"] = StaticColor(l.FillArgb),
                    ["o"] = Static(fa / 255.0 * 100),
                });

            layersArr.Add(new JsonObject
            {
                ["ddd"] = 0, ["ind"] = ++ind, ["ty"] = 4, ["nm"] = l.Name,
                ["ks"] = new JsonObject
                {
                    ["o"] = Anim(l.Opacity, 1, comp.Fps, v => v * 100),
                    ["r"] = Anim(l.Rotation, 0, comp.Fps, v => v),
                    ["p"] = new JsonObject
                    {
                        ["s"] = true,
                        ["x"] = Anim(l.PosX, 0, comp.Fps, v => v + comp.Width / 2.0),
                        ["y"] = Anim(l.PosY, 0, comp.Fps, v => v + comp.Height / 2.0),
                    },
                    ["a"] = new JsonObject { ["a"] = 0, ["k"] = new JsonArray(0, 0, 0) },
                    ["s"] = AnimScale(l.Scale, comp.Fps),
                },
                ["shapes"] = shapes,
                ["ip"] = 0, ["op"] = frames, ["st"] = 0,
            });
        }

        var root = new JsonObject
        {
            ["v"] = "5.7.4", ["fr"] = comp.Fps, ["ip"] = 0, ["op"] = frames,
            ["w"] = comp.Width, ["h"] = comp.Height, ["nm"] = "KLIP", ["ddd"] = 0,
            ["assets"] = new JsonArray(), ["layers"] = layersArr,
        };
        File.WriteAllText(path, root.ToJsonString());
        return (layersArr.Count, skipped);
    }

    // ---- path d → lottie shape(s) ----
    private static System.Collections.Generic.IEnumerable<JsonObject> PathContours(SKPath path)
    {
        var v = new JsonArray(); var it = new JsonArray(); var ot = new JsonArray();
        bool closed = false; bool has = false;
        SKPoint last = default;
        var raw = path.CreateRawIterator();
        var pts = new SKPoint[4];
        SKPathVerb verb;
        var outList = new System.Collections.Generic.List<JsonObject>();

        void Flush()
        {
            if (!has) return;
            outList.Add(new JsonObject
            {
                ["ty"] = "sh",
                ["ks"] = new JsonObject
                {
                    ["a"] = 0,
                    ["k"] = new JsonObject { ["i"] = it, ["o"] = ot, ["v"] = v, ["c"] = closed },
                },
            });
            v = new JsonArray(); it = new JsonArray(); ot = new JsonArray();
            closed = false; has = false;
        }

        void AddV(SKPoint pt) { v.Add(new JsonArray(pt.X, pt.Y)); it.Add(new JsonArray(0, 0)); ot.Add(new JsonArray(0, 0)); }

        while ((verb = raw.Next(pts)) != SKPathVerb.Done)
        {
            switch (verb)
            {
                case SKPathVerb.Move: Flush(); AddV(pts[0]); last = pts[0]; has = true; break;
                case SKPathVerb.Line: AddV(pts[1]); last = pts[1]; break;
                case SKPathVerb.Cubic:
                    ot[v.Count - 1] = new JsonArray(pts[1].X - last.X, pts[1].Y - last.Y);
                    AddV(pts[3]);
                    it[v.Count - 1] = new JsonArray(pts[2].X - pts[3].X, pts[2].Y - pts[3].Y);
                    last = pts[3];
                    break;
                case SKPathVerb.Quad:
                case SKPathVerb.Conic:
                {
                    // quad → cubic
                    var c1 = new SKPoint(last.X + 2f / 3f * (pts[1].X - last.X), last.Y + 2f / 3f * (pts[1].Y - last.Y));
                    var c2 = new SKPoint(pts[2].X + 2f / 3f * (pts[1].X - pts[2].X), pts[2].Y + 2f / 3f * (pts[1].Y - pts[2].Y));
                    ot[v.Count - 1] = new JsonArray(c1.X - last.X, c1.Y - last.Y);
                    AddV(pts[2]);
                    it[v.Count - 1] = new JsonArray(c2.X - pts[2].X, c2.Y - pts[2].Y);
                    last = pts[2];
                    break;
                }
                case SKPathVerb.Close: closed = true; break;
            }
        }
        Flush();
        return outList;
    }

    // ---- tracks → lottie animated properties ----
    private static JsonObject Static(double v) => new() { ["a"] = 0, ["k"] = v };

    private static JsonObject StaticColor(uint argb) => new()
    {
        ["a"] = 0,
        ["k"] = new JsonArray(((argb >> 16) & 0xFF) / 255.0, ((argb >> 8) & 0xFF) / 255.0, (argb & 0xFF) / 255.0, 1),
    };

    private static JsonObject Anim(Track? tr, double def, double fps, Func<double, double> map)
    {
        if (tr is null || tr.Keys.Count == 0) return Static(map(def));
        if (tr.Keys.Count == 1) return Static(map(tr.Keys[0].Value));
        var k = new JsonArray();
        foreach (var kf in tr.Keys)
        {
            // correct bodymovin easing handles: o={x:[x1],y:[y1]}, i={x:[x2],y:[y2]}
            double x1, y1, x2, y2;
            if (kf.Bez is { Length: 4 } bz) { x1 = bz[0]; y1 = bz[1]; x2 = bz[2]; y2 = bz[3]; }
            else { x1 = 0.35; y1 = 0; x2 = 0.65; y2 = 1; }
            k.Add(new JsonObject
            {
                ["t"] = Math.Round(kf.Time * fps, 2),
                ["s"] = new JsonArray(map(kf.Value)),
                ["o"] = new JsonObject { ["x"] = new JsonArray(x1), ["y"] = new JsonArray(y1) },
                ["i"] = new JsonObject { ["x"] = new JsonArray(x2), ["y"] = new JsonArray(y2) },
            });
        }
        return new JsonObject { ["a"] = 1, ["k"] = k };
    }

    private static JsonObject AnimScale(Track? tr, double fps)
    {
        if (tr is null || tr.Keys.Count <= 1)
        {
            double s = (tr?.Keys.Count == 1 ? tr.Keys[0].Value : 1.0) * 100;
            return new JsonObject { ["a"] = 0, ["k"] = new JsonArray(s, s, 100) };
        }
        var k = new JsonArray();
        foreach (var kf in tr.Keys)
        {
            double x1, y1, x2, y2;
            if (kf.Bez is { Length: 4 } bz) { x1 = bz[0]; y1 = bz[1]; x2 = bz[2]; y2 = bz[3]; }
            else { x1 = 0.35; y1 = 0; x2 = 0.65; y2 = 1; }
            k.Add(new JsonObject
            {
                ["t"] = Math.Round(kf.Time * fps, 2),
                ["s"] = new JsonArray(kf.Value * 100, kf.Value * 100, 100),
                ["o"] = new JsonObject { ["x"] = new JsonArray(x1), ["y"] = new JsonArray(y1) },
                ["i"] = new JsonObject { ["x"] = new JsonArray(x2), ["y"] = new JsonArray(y2) },
            });
        }
        return new JsonObject { ["a"] = 1, ["k"] = k };
    }
}
