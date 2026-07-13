using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace Klip.Engine;

/// <summary>Um path importado de SVG: "d" (já em espaço SVG, transforms aplicados) + cor de fill.</summary>
public sealed record SvgPath(string D, uint? FillArgb);

/// <summary>
/// Import de ficheiros/texto SVG → lista de paths "d" (com transforms translate/scale/rotate/matrix/skew
/// aplicados e fill lido), que a App transforma em camadas editáveis. Puro (SkiaSharp+System).
/// </summary>
public static class SvgImport
{
    private const RegexOptions O = RegexOptions.IgnoreCase | RegexOptions.Singleline;

    public static IReadOnlyList<SvgPath> ImportPaths(string fileOrText)
    {
        string svg = File.Exists(fileOrText) ? File.ReadAllText(fileOrText) : fileOrText;
        var outp = new List<SvgPath>();

        // transform do 1º <g>/<svg> envolvente (v1: um nível — nesting profundo é limitação documentada)
        var gm = Regex.Match(svg, @"<(?:g|svg)\b[^>]*\btransform\s*=\s*(""|')(?<t>.*?)\1", O);
        SKMatrix outer = gm.Success ? ParseTransform(gm.Groups["t"].Value) : SKMatrix.CreateIdentity();

        foreach (Match m in Regex.Matches(svg, @"<path\b[^>]*?>", O))
        {
            var dm = Regex.Match(m.Value, @"\bd\s*=\s*(""|')(?<d>.*?)\1", O);
            if (dm.Success) Add(outp, dm.Groups["d"].Value, m.Value, outer);
        }
        foreach (Match m in Regex.Matches(svg, @"<rect\b[^>]*?>", O)) { var d = RectD(m.Value); if (d != null) Add(outp, d, m.Value, outer); }
        foreach (Match m in Regex.Matches(svg, @"<circle\b[^>]*?>", O)) { var d = OvalD(m.Value, false); if (d != null) Add(outp, d, m.Value, outer); }
        foreach (Match m in Regex.Matches(svg, @"<ellipse\b[^>]*?>", O)) { var d = OvalD(m.Value, true); if (d != null) Add(outp, d, m.Value, outer); }
        foreach (Match m in Regex.Matches(svg, @"<(polygon|polyline)\b[^>]*?>", O)) { var d = PolyD(m.Value, m.Groups[1].Value.ToLowerInvariant() == "polygon"); if (d != null) Add(outp, d, m.Value, outer); }
        return outp;
    }

    private static void Add(List<SvgPath> outp, string d, string tag, SKMatrix outer)
    {
        var tm = Regex.Match(tag, @"\btransform\s*=\s*(""|')(?<t>.*?)\1", O);
        var own = tm.Success ? ParseTransform(tm.Groups["t"].Value) : SKMatrix.CreateIdentity();
        var mat = SKMatrix.Concat(outer, own);   // outer aplicado DEPOIS do próprio
        string finalD = d;
        using (var p = SKPath.ParseSvgPathData(d))
            if (p is not null) { p.Transform(mat); finalD = p.ToSvgPathData(); }
        outp.Add(new SvgPath(finalD, ReadFill(tag)));
    }

    internal static SKMatrix ParseTransform(string transform)
    {
        var total = SKMatrix.CreateIdentity();
        foreach (Match m in Regex.Matches(transform ?? "", @"(?<fn>\w+)\s*\((?<args>[^)]*)\)"))
        {
            var fn = m.Groups["fn"].Value.ToLowerInvariant();
            var n = Nums(m.Groups["args"].Value);
            SKMatrix t = fn switch
            {
                "translate" => SKMatrix.CreateTranslation(At(n, 0), At(n, 1)),
                "scale" => SKMatrix.CreateScale(At(n, 0, 1), n.Length > 1 ? n[1] : At(n, 0, 1)),
                "rotate" => n.Length >= 3 ? RotateAbout(n[0], n[1], n[2]) : SKMatrix.CreateRotationDegrees(At(n, 0)),
                "skewx" => Skew(MathF.Tan(At(n, 0) * MathF.PI / 180f), 0),
                "skewy" => Skew(0, MathF.Tan(At(n, 0) * MathF.PI / 180f)),
                "matrix" => n.Length >= 6 ? new SKMatrix(n[0], n[2], n[4], n[1], n[3], n[5], 0, 0, 1) : SKMatrix.CreateIdentity(),
                _ => SKMatrix.CreateIdentity(),
            };
            total = SKMatrix.Concat(total, t);   // esquerda→direita: total = T1*T2*…*Tn
        }
        return total;
    }

    private static SKMatrix RotateAbout(float a, float cx, float cy)
        => SKMatrix.Concat(SKMatrix.Concat(SKMatrix.CreateTranslation(cx, cy), SKMatrix.CreateRotationDegrees(a)), SKMatrix.CreateTranslation(-cx, -cy));
    private static SKMatrix Skew(float sx, float sy) => new() { ScaleX = 1, ScaleY = 1, Persp2 = 1, SkewX = sx, SkewY = sy };

    public static SKRect UnionBounds(IEnumerable<string> ds)
    {
        SKRect u = SKRect.Empty; bool any = false;
        foreach (var d in ds)
        {
            using var p = SKPath.ParseSvgPathData(d);
            if (p is null || p.IsEmpty) continue;
            u = any ? SKRect.Union(u, p.Bounds) : p.Bounds; any = true;
        }
        return u;
    }

    /// <summary>Recentra TODOS os paths (convenção KLIP: forma centrada em 0,0) preservando posições relativas.</summary>
    public static IReadOnlyList<SvgPath> CenterAll(IReadOnlyList<SvgPath> paths)
    {
        if (paths.Count == 0) return paths;
        var b = UnionBounds(paths.Select(p => p.D));
        var mat = SKMatrix.CreateTranslation(-b.MidX, -b.MidY);
        var outp = new List<SvgPath>(paths.Count);
        foreach (var p in paths)
        {
            string d = p.D;
            using (var sp = SKPath.ParseSvgPathData(d))
                if (sp is not null) { sp.Transform(mat); d = sp.ToSvgPathData(); }
            outp.Add(p with { D = d });
        }
        return outp;
    }

    // ---- shape → d (bónus) ----
    private static string? RectD(string tag)
    {
        float? x = Attr(tag, "x"), y = Attr(tag, "y"), w = Attr(tag, "width"), h = Attr(tag, "height");
        if (w is null || h is null) return null;
        float X = x ?? 0, Y = y ?? 0, W = w.Value, H = h.Value;
        return $"M{F(X)} {F(Y)} L{F(X + W)} {F(Y)} L{F(X + W)} {F(Y + H)} L{F(X)} {F(Y + H)} Z";
    }
    private static string? OvalD(string tag, bool ellipse)
    {
        float? cx = Attr(tag, "cx"), cy = Attr(tag, "cy");
        using var p = new SKPath();
        if (ellipse) { float? rx = Attr(tag, "rx"), ry = Attr(tag, "ry"); if (rx is null || ry is null) return null; p.AddOval(new SKRect((cx ?? 0) - rx.Value, (cy ?? 0) - ry.Value, (cx ?? 0) + rx.Value, (cy ?? 0) + ry.Value)); }
        else { float? r = Attr(tag, "r"); if (r is null) return null; p.AddCircle(cx ?? 0, cy ?? 0, r.Value); }
        return p.ToSvgPathData();
    }
    private static string? PolyD(string tag, bool closed)
    {
        var pm = Regex.Match(tag, @"\bpoints\s*=\s*(""|')(?<p>.*?)\1", O);
        if (!pm.Success) return null;
        var n = Nums(pm.Groups["p"].Value);
        if (n.Length < 4) return null;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i + 1 < n.Length; i += 2)
            sb.Append(i == 0 ? 'M' : 'L').Append(F(n[i])).Append(' ').Append(F(n[i + 1])).Append(' ');
        if (closed) sb.Append('Z');
        return sb.ToString().Trim();
    }

    private static uint? ReadFill(string tag)
    {
        var m = Regex.Match(tag, @"\bfill\s*=\s*(""|')(?<f>.*?)\1", O);
        if (!m.Success) return null;
        var f = m.Groups["f"].Value.Trim();
        if (f.Length == 0 || f.Equals("none", StringComparison.OrdinalIgnoreCase)) return null;
        if (f.StartsWith('#'))
        {
            var hex = f[1..];
            if (hex.Length == 3) hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
            if (hex.Length == 6 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
                return 0xFF000000u | rgb;
            if (hex.Length == 8 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var argb))
                return argb;
        }
        return null;   // nomes de cor / rgb() não suportados (v1)
    }

    // ---- helpers ----
    private static float[] Nums(string s) => Regex.Matches(s, @"-?\d*\.?\d+(?:[eE][-+]?\d+)?")
        .Select(m => float.Parse(m.Value, CultureInfo.InvariantCulture)).ToArray();
    private static float At(float[] n, int i, float def = 0) => i < n.Length ? n[i] : def;
    private static float? Attr(string tag, string name)
    {
        var m = Regex.Match(tag, @"\b" + name + @"\s*=\s*(""|')(?<v>-?\d*\.?\d+)", O);
        return m.Success ? float.Parse(m.Groups["v"].Value, CultureInfo.InvariantCulture) : null;
    }
    private static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
