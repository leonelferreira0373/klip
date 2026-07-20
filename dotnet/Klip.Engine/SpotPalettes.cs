using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace Klip.Engine;

/// <summary>
/// BIBLIOTECAS DE COR PROFISSIONAIS (PANTONE, HKS, TOYO, DIC, FOCOLTONE, TRUMATCH).
///
/// As bases de cor da PANTONE e companhia são licenciadas — não vão embutidas no KLIP. São LIDAS
/// DO DISCO a partir de um CorelDRAW instalado nesta máquina, que já as traz licenciadas ao
/// utilizador. Sem Corel instalado, sobram as paletas livres (SVG/CSS, escalas de cinzento) e o
/// utilizador pode sempre apontar para um ficheiro seu.
///
/// Cada cor traz sRGB (ecrã) E CMYK (chapa), portanto uma cor escolhida aqui atravessa o
/// <see cref="CmykExport"/> sem ser reinventada — é o que separa isto de um seletor de cor de
/// brinquedo: a cor que vês é a cor que sai na gráfica.
/// </summary>
public static class SpotPalettes
{
    public sealed record SpotColor(
        string Name,
        uint Argb,
        float C, float M, float Y, float K,
        bool HasCmyk,
        float L, float A, float B, bool HasLab);

    public sealed record Palette(string Name, string Group, string Source, IReadOnlyList<SpotColor> Colors);

    private static List<Palette>? _cache;

    /// <summary>Todas as paletas encontradas. Descobre uma vez e guarda.</summary>
    public static IReadOnlyList<Palette> All => _cache ??= Discover();

    /// <summary>Redescobre (o utilizador instalou o Corel entretanto, ou apontou uma pasta nova).</summary>
    public static void Refresh() => _cache = null;

    /// <summary>Pastas extra onde procurar, definidas pelo utilizador.</summary>
    public static readonly List<string> ExtraRoots = new();

    private static List<Palette> Discover()
    {
        var list = new List<Palette>();
        foreach (var p in Builtin()) list.Add(p);

        foreach (var root in PaletteRoots())
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*.xml", SearchOption.AllDirectories); }
            catch { continue; }
            foreach (var f in files)
            {
                var pal = TryLoadCorelXml(f);
                if (pal is not null && pal.Colors.Count > 0) list.Add(pal);
            }
        }

        // nomes repetidos (versões antigas do mesmo livro) → fica a que tem mais cores
        return list
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(p => p.Colors.Count).First())
            .OrderBy(p => p.Group, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> PaletteRoots()
    {
        foreach (var e in ExtraRoots) yield return e;

        // CorelDRAW: .../CorelDRAW Graphics Suite/<versão>/Color/Palettes
        foreach (var pf in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 })
        {
            if (string.IsNullOrEmpty(pf)) continue;
            var corel = Path.Combine(pf, "Corel");
            if (!Directory.Exists(corel)) continue;
            IEnumerable<string> suites;
            try { suites = Directory.EnumerateDirectories(corel, "CorelDRAW*"); }
            catch { continue; }
            foreach (var s in suites)
            {
                IEnumerable<string> vers;
                try { vers = Directory.EnumerateDirectories(s); } catch { continue; }
                foreach (var v in vers)
                {
                    var pal = Path.Combine(v, "Color", "Palettes");
                    if (Directory.Exists(pal)) yield return pal;
                }
            }
        }
    }

    /// <summary>
    /// Paleta XML do CorelDRAW. Cada cor é um &lt;cs name="..."&gt; com um ou mais
    /// &lt;color cs="CMYK|LAB|RGB|sRGB|AdobeRGB" tints="a,b,c[,d]"/&gt; — valores 0..1.
    /// Preferimos RGB/sRGB para ecrã; se só houver LAB, convertemos; se só houver CMYK, aproximamos.
    /// </summary>
    private static Palette? TryLoadCorelXml(string file)
    {
        XDocument doc;
        try
        {
            if (new FileInfo(file).Length > 24 * 1024 * 1024) return null;   // ficheiro absurdo → ignora
            doc = XDocument.Load(file);
        }
        catch { return null; }

        var root = doc.Root;
        if (root is null || !root.Name.LocalName.Equals("palette", StringComparison.OrdinalIgnoreCase)) return null;

        var name = (string?)root.Attribute("name");
        if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileNameWithoutExtension(file);

        var colors = new List<SpotColor>();
        foreach (var cs in root.Descendants().Where(e => e.Name.LocalName == "cs"))
        {
            var cn = (string?)cs.Attribute("name");
            if (string.IsNullOrWhiteSpace(cn)) continue;

            float[]? rgb = null, lab = null, cmyk = null;
            foreach (var c in cs.Elements().Where(e => e.Name.LocalName == "color"))
            {
                var space = ((string?)c.Attribute("cs") ?? "").ToUpperInvariant();
                var t = ParseTints((string?)c.Attribute("tints"));
                if (t is null) continue;
                switch (space)
                {
                    case "RGB" or "SRGB" when t.Length >= 3: rgb ??= t; break;   // o 1º RGB do ficheiro é o sRGB
                    case "LAB" when t.Length >= 3: lab ??= t; break;
                    case "CMYK" when t.Length >= 4: cmyk ??= t; break;
                }
            }

            uint argb;
            if (rgb is not null) argb = Pack(rgb[0], rgb[1], rgb[2]);
            else if (lab is not null) argb = LabToArgb(lab);
            else if (cmyk is not null) argb = CmykToArgb(cmyk[0], cmyk[1], cmyk[2], cmyk[3]);
            else continue;

            colors.Add(new SpotColor(
                cn!.Trim(), argb,
                cmyk?[0] ?? 0, cmyk?[1] ?? 0, cmyk?[2] ?? 0, cmyk?[3] ?? 0, cmyk is not null,
                // o Corel guarda o LAB normalizado: L em 0..1, a/b deslocados para 0..1 em ±128
                lab is not null ? lab[0] * 100f : 0,
                lab is not null ? lab[1] * 256f - 128f : 0,
                lab is not null ? lab[2] * 256f - 128f : 0,
                lab is not null));
        }
        if (colors.Count == 0) return null;

        var group = file.Contains("PANTONE", StringComparison.OrdinalIgnoreCase) ? "PANTONE"
                  : file.Contains(Path.DirectorySeparatorChar + "HKS", StringComparison.OrdinalIgnoreCase) ? "HKS"
                  : file.Contains(Path.DirectorySeparatorChar + "Spot" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? "Spot"
                  : "Process";
        return new Palette(name!, group, file, colors);
    }

    private static float[]? ParseTints(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var parts = s.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var r = new float[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out r[i])) return null;
        return r;
    }

    private static uint Pack(float r, float g, float b) =>
        0xFF000000u
        | ((uint)Math.Clamp((int)MathF.Round(r * 255f), 0, 255) << 16)
        | ((uint)Math.Clamp((int)MathF.Round(g * 255f), 0, 255) << 8)
        | (uint)Math.Clamp((int)MathF.Round(b * 255f), 0, 255);

    /// <summary>CMYK "de secretária" (sem perfil) — só para paletas que não trazem outra coisa.</summary>
    private static uint CmykToArgb(float c, float m, float y, float k) =>
        Pack((1 - c) * (1 - k), (1 - m) * (1 - k), (1 - y) * (1 - k));

    /// <summary>L*a*b* (D50) → sRGB. É o caminho mais fiel quando o livro não traz RGB.</summary>
    private static uint LabToArgb(float[] t)
    {
        float L = t[0] * 100f, a = t[1] * 256f - 128f, b = t[2] * 256f - 128f;
        float fy = (L + 16f) / 116f, fx = fy + a / 500f, fz = fy - b / 200f;
        static float Inv(float f) => f > 6f / 29f ? f * f * f : 3f * (6f / 29f) * (6f / 29f) * (f - 4f / 29f);
        // ponto branco D50 (é o que a indústria gráfica usa)
        float X = 0.9642f * Inv(fx), Y = 1.0000f * Inv(fy), Z = 0.8249f * Inv(fz);
        // Bradford D50→D65 já embutido nesta matriz XYZ→sRGB
        float r = 3.1338561f * X - 1.6168667f * Y - 0.4906146f * Z;
        float g = -0.9787684f * X + 1.9161415f * Y + 0.0334540f * Z;
        float bl = 0.0719453f * X - 0.2289914f * Y + 1.4052427f * Z;
        static float Gam(float v)
        {
            v = Math.Clamp(v, 0f, 1f);
            return v <= 0.0031308f ? 12.92f * v : 1.055f * MathF.Pow(v, 1f / 2.4f) - 0.055f;
        }
        return Pack(Gam(r), Gam(g), Gam(bl));
    }

    /// <summary>Paletas livres, sempre presentes (não dependem de haver Corel instalado).</summary>
    private static IEnumerable<Palette> Builtin()
    {
        yield return new Palette("Cores CSS/SVG", "Livre", "embutida",
            SvgNames.Select(kv => new SpotColor(kv.Key, kv.Value, 0, 0, 0, 0, false, 0, 0, 0, false)).ToList());

        var grays = new List<SpotColor>();
        for (int i = 0; i <= 20; i++)
        {
            int v = (int)MathF.Round(i * 255f / 20f);
            grays.Add(new SpotColor($"Cinzento {i * 5}%", 0xFF000000u | ((uint)v << 16) | ((uint)v << 8) | (uint)v,
                                    0, 0, 0, 1f - i / 20f, true, 0, 0, 0, false));
        }
        yield return new Palette("Escala de cinzentos", "Livre", "embutida", grays);
    }

    /// <summary>Procura por nome em todas as paletas (é assim que se escreve "185" e sai PANTONE 185 C).</summary>
    public static IEnumerable<(Palette pal, SpotColor color)> Search(string query, int limit = 60)
    {
        if (string.IsNullOrWhiteSpace(query)) yield break;
        var q = query.Trim();
        int n = 0;
        // passagem 1: começa por / igual — o que se procura costuma estar aqui
        foreach (var p in All)
            foreach (var c in p.Colors)
                if (c.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                {
                    yield return (p, c);
                    if (++n >= limit) yield break;
                }
    }

    /// <summary>Cor mais próxima em ΔE (CIE76 sobre Lab) — "qual o PANTONE mais parecido com isto?".</summary>
    public static (Palette pal, SpotColor color, double dE)? Nearest(uint argb, string? paletteName = null)
    {
        var (l0, a0, b0) = ArgbToLab(argb);
        (Palette, SpotColor, double)? best = null;
        foreach (var p in All)
        {
            if (p.Group == "Livre") continue;
            if (paletteName is { Length: > 0 } && !p.Name.Contains(paletteName, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var c in p.Colors)
            {
                var (l1, a1, b1) = c.HasLab ? (c.L, c.A, c.B) : ArgbToLab(c.Argb);
                double d = Math.Sqrt((l0 - l1) * (l0 - l1) + (a0 - a1) * (a0 - a1) + (b0 - b1) * (b0 - b1));
                if (best is null || d < best.Value.Item3) best = (p, c, d);
            }
        }
        return best is null ? null : (best.Value.Item1, best.Value.Item2, best.Value.Item3);
    }

    private static (float L, float a, float b) ArgbToLab(uint argb)
    {
        static float Lin(float v) => v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
        float r = Lin(((argb >> 16) & 0xFF) / 255f), g = Lin(((argb >> 8) & 0xFF) / 255f), b = Lin((argb & 0xFF) / 255f);
        float X = 0.4360747f * r + 0.3850649f * g + 0.1430804f * b;   // sRGB → XYZ D50
        float Y = 0.2225045f * r + 0.7168786f * g + 0.0606169f * b;
        float Z = 0.0139322f * r + 0.0971045f * g + 0.7141733f * b;
        static float F(float t) => t > 216f / 24389f ? MathF.Cbrt(t) : (24389f / 27f * t + 16f) / 116f;
        float fx = F(X / 0.9642f), fy = F(Y), fz = F(Z / 0.8249f);
        return (116f * fy - 16f, 500f * (fx - fy), 200f * (fy - fz));
    }

    // 140 nomes CSS/SVG — domínio público, sempre disponíveis.
    private static readonly Dictionary<string, uint> SvgNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["aliceblue"] = 0xFFF0F8FF, ["antiquewhite"] = 0xFFFAEBD7, ["aqua"] = 0xFF00FFFF,
        ["aquamarine"] = 0xFF7FFFD4, ["azure"] = 0xFFF0FFFF, ["beige"] = 0xFFF5F5DC,
        ["bisque"] = 0xFFFFE4C4, ["black"] = 0xFF000000, ["blanchedalmond"] = 0xFFFFEBCD,
        ["blue"] = 0xFF0000FF, ["blueviolet"] = 0xFF8A2BE2, ["brown"] = 0xFFA52A2A,
        ["burlywood"] = 0xFFDEB887, ["cadetblue"] = 0xFF5F9EA0, ["chartreuse"] = 0xFF7FFF00,
        ["chocolate"] = 0xFFD2691E, ["coral"] = 0xFFFF7F50, ["cornflowerblue"] = 0xFF6495ED,
        ["cornsilk"] = 0xFFFFF8DC, ["crimson"] = 0xFFDC143C, ["cyan"] = 0xFF00FFFF,
        ["darkblue"] = 0xFF00008B, ["darkcyan"] = 0xFF008B8B, ["darkgoldenrod"] = 0xFFB8860B,
        ["darkgray"] = 0xFFA9A9A9, ["darkgreen"] = 0xFF006400, ["darkkhaki"] = 0xFFBDB76B,
        ["darkmagenta"] = 0xFF8B008B, ["darkolivegreen"] = 0xFF556B2F, ["darkorange"] = 0xFFFF8C00,
        ["darkorchid"] = 0xFF9932CC, ["darkred"] = 0xFF8B0000, ["darksalmon"] = 0xFFE9967A,
        ["darkseagreen"] = 0xFF8FBC8F, ["darkslateblue"] = 0xFF483D8B, ["darkslategray"] = 0xFF2F4F4F,
        ["darkturquoise"] = 0xFF00CED1, ["darkviolet"] = 0xFF9400D3, ["deeppink"] = 0xFFFF1493,
        ["deepskyblue"] = 0xFF00BFFF, ["dimgray"] = 0xFF696969, ["dodgerblue"] = 0xFF1E90FF,
        ["firebrick"] = 0xFFB22222, ["floralwhite"] = 0xFFFFFAF0, ["forestgreen"] = 0xFF228B22,
        ["fuchsia"] = 0xFFFF00FF, ["gainsboro"] = 0xFFDCDCDC, ["ghostwhite"] = 0xFFF8F8FF,
        ["gold"] = 0xFFFFD700, ["goldenrod"] = 0xFFDAA520, ["gray"] = 0xFF808080,
        ["green"] = 0xFF008000, ["greenyellow"] = 0xFFADFF2F, ["honeydew"] = 0xFFF0FFF0,
        ["hotpink"] = 0xFFFF69B4, ["indianred"] = 0xFFCD5C5C, ["indigo"] = 0xFF4B0082,
        ["ivory"] = 0xFFFFFFF0, ["khaki"] = 0xFFF0E68C, ["lavender"] = 0xFFE6E6FA,
        ["lavenderblush"] = 0xFFFFF0F5, ["lawngreen"] = 0xFF7CFC00, ["lemonchiffon"] = 0xFFFFFACD,
        ["lightblue"] = 0xFFADD8E6, ["lightcoral"] = 0xFFF08080, ["lightcyan"] = 0xFFE0FFFF,
        ["lightgoldenrodyellow"] = 0xFFFAFAD2, ["lightgray"] = 0xFFD3D3D3, ["lightgreen"] = 0xFF90EE90,
        ["lightpink"] = 0xFFFFB6C1, ["lightsalmon"] = 0xFFFFA07A, ["lightseagreen"] = 0xFF20B2AA,
        ["lightskyblue"] = 0xFF87CEFA, ["lightslategray"] = 0xFF778899, ["lightsteelblue"] = 0xFFB0C4DE,
        ["lightyellow"] = 0xFFFFFFE0, ["lime"] = 0xFF00FF00, ["limegreen"] = 0xFF32CD32,
        ["linen"] = 0xFFFAF0E6, ["magenta"] = 0xFFFF00FF, ["maroon"] = 0xFF800000,
        ["mediumaquamarine"] = 0xFF66CDAA, ["mediumblue"] = 0xFF0000CD, ["mediumorchid"] = 0xFFBA55D3,
        ["mediumpurple"] = 0xFF9370DB, ["mediumseagreen"] = 0xFF3CB371, ["mediumslateblue"] = 0xFF7B68EE,
        ["mediumspringgreen"] = 0xFF00FA9A, ["mediumturquoise"] = 0xFF48D1CC, ["mediumvioletred"] = 0xFFC71585,
        ["midnightblue"] = 0xFF191970, ["mintcream"] = 0xFFF5FFFA, ["mistyrose"] = 0xFFFFE4E1,
        ["moccasin"] = 0xFFFFE4B5, ["navajowhite"] = 0xFFFFDEAD, ["navy"] = 0xFF000080,
        ["oldlace"] = 0xFFFDF5E6, ["olive"] = 0xFF808000, ["olivedrab"] = 0xFF6B8E23,
        ["orange"] = 0xFFFFA500, ["orangered"] = 0xFFFF4500, ["orchid"] = 0xFFDA70D6,
        ["palegoldenrod"] = 0xFFEEE8AA, ["palegreen"] = 0xFF98FB98, ["paleturquoise"] = 0xFFAFEEEE,
        ["palevioletred"] = 0xFFDB7093, ["papayawhip"] = 0xFFFFEFD5, ["peachpuff"] = 0xFFFFDAB9,
        ["peru"] = 0xFFCD853F, ["pink"] = 0xFFFFC0CB, ["plum"] = 0xFFDDA0DD,
        ["powderblue"] = 0xFFB0E0E6, ["purple"] = 0xFF800080, ["rebeccapurple"] = 0xFF663399,
        ["red"] = 0xFFFF0000, ["rosybrown"] = 0xFFBC8F8F, ["royalblue"] = 0xFF4169E1,
        ["saddlebrown"] = 0xFF8B4513, ["salmon"] = 0xFFFA8072, ["sandybrown"] = 0xFFF4A460,
        ["seagreen"] = 0xFF2E8B57, ["seashell"] = 0xFFFFF5EE, ["sienna"] = 0xFFA0522D,
        ["silver"] = 0xFFC0C0C0, ["skyblue"] = 0xFF87CEEB, ["slateblue"] = 0xFF6A5ACD,
        ["slategray"] = 0xFF708090, ["snow"] = 0xFFFFFAFA, ["springgreen"] = 0xFF00FF7F,
        ["steelblue"] = 0xFF4682B4, ["tan"] = 0xFFD2B48C, ["teal"] = 0xFF008080,
        ["thistle"] = 0xFFD8BFD8, ["tomato"] = 0xFFFF6347, ["turquoise"] = 0xFF40E0D0,
        ["violet"] = 0xFFEE82EE, ["wheat"] = 0xFFF5DEB3, ["white"] = 0xFFFFFFFF,
        ["whitesmoke"] = 0xFFF5F5F5, ["yellow"] = 0xFFFFFF00, ["yellowgreen"] = 0xFF9ACD32,
    };
}
