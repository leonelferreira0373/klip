using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Klip.Engine.Lottie;

/// <summary>Parses bodymovin JSON into a LottieDoc.</summary>
public static class LottieLoader
{
    public static LottieDoc Load(byte[] json)
    {
        using var d = JsonDocument.Parse(json);
        var r = d.RootElement;
        var doc = new LottieDoc
        {
            Fr = GetD(r, "fr", 60),
            Ip = GetD(r, "ip", 0),
            Op = GetD(r, "op", 0),
            W = (int)GetD(r, "w", 0),
            H = (int)GetD(r, "h", 0),
        };
        if (r.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            foreach (var a in assets.EnumerateArray())
            {
                var asset = new LottieAsset { Id = GetS(a, "id") };
                if (a.TryGetProperty("layers", out var al) && al.ValueKind == JsonValueKind.Array)
                    foreach (var l in al.EnumerateArray()) asset.Layers.Add(ReadLayer(l));
                if (asset.Id.Length > 0) doc.Assets[asset.Id] = asset;
            }
        if (r.TryGetProperty("layers", out var layers) && layers.ValueKind == JsonValueKind.Array)
            foreach (var l in layers.EnumerateArray()) doc.Layers.Add(ReadLayer(l));
        return doc;
    }

    private static LottieLayer ReadLayer(JsonElement l)
    {
        var layer = new LottieLayer
        {
            Type = (int)GetD(l, "ty", 4),
            Index = (int)GetD(l, "ind", -1),
            Parent = l.TryGetProperty("parent", out var p) ? (int)p.GetDouble() : -1,
            Name = GetS(l, "nm"),
            RefId = l.TryGetProperty("refId", out var rid) ? rid.GetString() : null,
            Ip = GetD(l, "ip", 0),
            Op = GetD(l, "op", 0),
            St = GetD(l, "st", 0),
            Sr = GetD(l, "sr", 1),
        };
        if (l.TryGetProperty("ks", out var ks)) layer.Transform = ReadTransform(ks);
        if (l.TryGetProperty("shapes", out var sh) && sh.ValueKind == JsonValueKind.Array)
            foreach (var s in sh.EnumerateArray())
            { var ls = ReadShape(s); if (ls is not null) layer.Shapes.Add(ls); }
        if (layer.Type == 1)  // solid
        {
            layer.SolidColor = ParseHexColor(GetS(l, "sc"));
            layer.SolidW = (int)GetD(l, "sw", 0); layer.SolidH = (int)GetD(l, "sh", 0);
        }
        return layer;
    }

    private static LottieTransform ReadTransform(JsonElement ks)
    {
        var t = new LottieTransform();
        if (ks.TryGetProperty("p", out var p))
        {
            bool split = p.TryGetProperty("s", out var sp) && sp.ValueKind == JsonValueKind.True;
            if (split)
            {
                if (p.TryGetProperty("x", out var px)) t.PosX = ReadProp(px);
                if (p.TryGetProperty("y", out var py)) t.PosY = ReadProp(py);
            }
            else t.Position = ReadProp(p);
        }
        if (ks.TryGetProperty("a", out var a)) t.Anchor = ReadProp(a);
        if (ks.TryGetProperty("s", out var s)) t.Scale = ReadProp(s);
        if (ks.TryGetProperty("r", out var r)) t.Rotation = ReadProp(r);
        if (ks.TryGetProperty("o", out var o)) t.Opacity = ReadProp(o);
        return t;
    }

    private static LottieShape? ReadShape(JsonElement s)
    {
        string ty = GetS(s, "ty");
        switch (ty)
        {
            case "gr":
            {
                var g = new ShapeGroup { Kind = ty };
                if (s.TryGetProperty("it", out var it) && it.ValueKind == JsonValueKind.Array)
                    foreach (var i in it.EnumerateArray())
                    {
                        if (GetS(i, "ty") == "tr") { g.Transform = ReadTransform(i); continue; }
                        var child = ReadShape(i); if (child is not null) g.Items.Add(child);
                    }
                return g;
            }
            case "sh":
            {
                var sp = new ShapePath { Kind = ty };
                if (s.TryGetProperty("ks", out var ksh)) sp.Path = ReadPathProp(ksh);
                return sp;
            }
            case "rc":
            {
                var rc = new ShapeRect { Kind = ty };
                if (s.TryGetProperty("p", out var p)) rc.Position = ReadProp(p);
                if (s.TryGetProperty("s", out var sz)) rc.Size = ReadProp(sz);
                if (s.TryGetProperty("r", out var r)) rc.Radius = ReadProp(r);
                return rc;
            }
            case "el":
            {
                var el = new ShapeEllipse { Kind = ty };
                if (s.TryGetProperty("p", out var p)) el.Position = ReadProp(p);
                if (s.TryGetProperty("s", out var sz)) el.Size = ReadProp(sz);
                return el;
            }
            case "fl":
            {
                var fl = new ShapeFill { Kind = ty, FillRule = (int)GetD(s, "r", 1) };
                if (s.TryGetProperty("c", out var c)) fl.Color = ReadProp(c);
                if (s.TryGetProperty("o", out var o)) fl.Opacity = ReadProp(o);
                return fl;
            }
            case "st":
            {
                var st = new ShapeStroke { Kind = ty, Cap = (int)GetD(s, "lc", 2), Join = (int)GetD(s, "lj", 2) };
                if (s.TryGetProperty("c", out var c)) st.Color = ReadProp(c);
                if (s.TryGetProperty("o", out var o)) st.Opacity = ReadProp(o);
                if (s.TryGetProperty("w", out var w)) st.Width = ReadProp(w);
                return st;
            }
            case "tm":
            {
                var tm = new ShapeTrim { Kind = ty };
                if (s.TryGetProperty("s", out var ss)) tm.Start = ReadProp(ss);
                if (s.TryGetProperty("e", out var ee)) tm.End = ReadProp(ee);
                if (s.TryGetProperty("o", out var oo)) tm.Offset = ReadProp(oo);
                return tm;
            }
            case "gf":
            case "gs":
            {
                var g = new ShapeGradient { Kind = ty, IsStroke = ty == "gs", GradientType = (int)GetD(s, "t", 1),
                    StopCount = s.TryGetProperty("g", out var gg) ? (int)GetD(gg, "p", 0) : 0 };
                if (s.TryGetProperty("s", out var st2)) g.Start = ReadProp(st2);
                if (s.TryGetProperty("e", out var en)) g.End = ReadProp(en);
                if (s.TryGetProperty("g", out var gr) && gr.TryGetProperty("k", out var gk)) g.Colors = ReadProp(gk);
                if (s.TryGetProperty("o", out var o)) g.Opacity = ReadProp(o);
                if (s.TryGetProperty("w", out var w)) g.Width = ReadProp(w);
                return g;
            }
            default: return null;   // mm (merge), rd (round), rp (repeater)… v2
        }
    }

    // ---- animatable property ----
    private static AnimProp ReadProp(JsonElement p)
    {
        var ap = new AnimProp();
        bool animated = p.TryGetProperty("a", out var av) && av.ValueKind == JsonValueKind.Number && av.GetDouble() != 0;
        if (!p.TryGetProperty("k", out var k)) return ap;

        if (!animated)
        {
            ap.Static = ToVec(k);
            return ap;
        }
        ap.Animated = true;
        foreach (var kf in k.EnumerateArray())
        {
            var f = new AnimProp.Kf { T = GetD(kf, "t", 0), Hold = GetD(kf, "h", 0) != 0 };
            f.S = kf.TryGetProperty("s", out var s) ? ToVec(s) : Array.Empty<double>();
            if (kf.TryGetProperty("e", out var e)) f.E = ToVec(e);
            if (kf.TryGetProperty("o", out var o)) { f.Ox = Handle(o, "x"); f.Oy = Handle(o, "y"); }
            if (kf.TryGetProperty("i", out var i)) { f.Ix = Handle(i, "x"); f.Iy = Handle(i, "y"); }
            ap.Keys.Add(f);
        }
        return ap;
    }

    private static AnimProp ReadPathProp(JsonElement p)
    {
        var ap = new AnimProp();
        bool animated = p.TryGetProperty("a", out var av) && av.ValueKind == JsonValueKind.Number && av.GetDouble() != 0;
        if (!p.TryGetProperty("k", out var k)) return ap;
        if (!animated) { ap.StaticPath = ReadPathData(k); return ap; }
        ap.Animated = true;
        foreach (var kf in k.EnumerateArray())
        {
            var f = new AnimProp.Kf { T = GetD(kf, "t", 0), Hold = GetD(kf, "h", 0) != 0, S = Array.Empty<double>() };
            if (kf.TryGetProperty("s", out var s) && s.ValueKind == JsonValueKind.Array && s.GetArrayLength() > 0)
                f.Path = ReadPathData(s[0]);
            if (kf.TryGetProperty("o", out var o)) { f.Ox = Handle(o, "x"); f.Oy = Handle(o, "y"); }
            if (kf.TryGetProperty("i", out var i)) { f.Ix = Handle(i, "x"); f.Iy = Handle(i, "y"); }
            ap.Keys.Add(f);
        }
        return ap;
    }

    private static PathData ReadPathData(JsonElement e)
    {
        var pd = new PathData { Closed = e.TryGetProperty("c", out var c) && c.GetBoolean() };
        pd.V = ReadPts(e, "v"); pd.I = ReadPts(e, "i"); pd.O = ReadPts(e, "o");
        return pd;
    }

    private static double[][] ReadPts(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return Array.Empty<double[]>();
        var list = new List<double[]>();
        foreach (var pt in arr.EnumerateArray()) list.Add(ToVec(pt));
        return list.ToArray();
    }

    private static double[] Handle(JsonElement h, string comp)
    {
        if (!h.TryGetProperty(comp, out var v)) return Array.Empty<double>();
        return v.ValueKind == JsonValueKind.Array ? ToVec(v) : new[] { v.GetDouble() };
    }

    private static double[] ToVec(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Number) return new[] { e.GetDouble() };
        if (e.ValueKind == JsonValueKind.Array)
        {
            var list = new List<double>();
            foreach (var x in e.EnumerateArray()) if (x.ValueKind == JsonValueKind.Number) list.Add(x.GetDouble());
            return list.ToArray();
        }
        return Array.Empty<double>();
    }

    private static double GetD(JsonElement e, string name, double def)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : def;
    private static string GetS(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static uint ParseHexColor(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 6 && uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var rgb))
            return 0xFF000000 | rgb;
        return 0xFF000000;
    }
}
