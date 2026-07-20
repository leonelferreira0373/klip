using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml;

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
    /// <param name="RgbDerivado">
    /// TRUE quando o sRGB desta cor NÃO veio do livro: foi calculado a partir do CMYK por uma
    /// fórmula ingénua, sem perfil ICC nem gama. Acontece em 76 dos 177 ficheiros do Corel — entre
    /// eles TOYO, DIC, FOCOLTONE, TRUMATCH e todos os HKS, que só trazem CMYK.
    /// Sem esta bandeira, essas cores competiam de igual para igual num ΔE com 4 casas decimais e
    /// davam uma confiança que os dados não suportam. Quem mostra a cor ao utilizador tem de o dizer.
    /// </param>
    public sealed record SpotColor(
        string Name,
        uint Argb,
        float C, float M, float Y, float K,
        bool HasCmyk,
        float L, float A, float B, bool HasLab,
        bool RgbDerivado = false);

    public sealed record Palette(string Name, string Group, string Source, IReadOnlyList<SpotColor> Colors);

    private static List<Palette>? _cache;
    private static MatchEntry[]? _index;
    private static readonly object _gate = new();

    /// <summary>Cor já convertida para Lab, pronta a comparar. Ver <see cref="Nearest"/>.</summary>
    private readonly record struct MatchEntry(Palette Pal, SpotColor Col, double L, double A, double B);

    /// <summary>
    /// Orçamento de tempo do varrimento inicial. É um TRAVÃO DE EMERGÊNCIA para o caso de o
    /// ExtraRoots apontar para um disco de rede morto — não é um afinador de desempenho.
    /// Está deliberadamente FOLGADO: quando este limite dispara, livros inteiros desaparecem
    /// da lista, e uma lista de PANTONE que muda de tamanho conforme o dia é muito pior do que
    /// um arranque lento. Com o Corel local o varrimento anda pelos 2 s.
    /// </summary>
    public static TimeSpan DiscoverBudget { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Todas as paletas encontradas. Descobre uma vez e guarda.</summary>
    public static IReadOnlyList<Palette> All
    {
        get
        {
            // Duplo-check com lock: a aba Cor e o bus da IA podem pedir isto ao mesmo tempo, e
            // varrer 177 ficheiros duas vezes em paralelo é desperdício puro.
            var c = _cache;
            if (c is not null) return c;
            lock (_gate) return _cache ??= DiscoverSafe();
        }
    }

    /// <summary>Redescobre (o utilizador instalou o Corel entretanto, ou apontou uma pasta nova).</summary>
    public static void Refresh() { lock (_gate) { _cache = null; _index = null; } }

    /// <summary>Pastas extra onde procurar, definidas pelo utilizador.</summary>
    public static readonly List<string> ExtraRoots = new();

    /// <summary>Diagnóstico do último varrimento — vai para o Inspector quando o utilizador
    /// pergunta "porque é que não vejo os meus PANTONE?".</summary>
    public static string LastDiscoverReport { get; private set; } = "(ainda não varrido)";

    /// <summary>Nada aqui pode deitar abaixo o arranque: se o varrimento estoirar, ficam as
    /// paletas livres e o motivo escrito no <see cref="LastDiscoverReport"/>.</summary>
    private static List<Palette> DiscoverSafe()
    {
        try { return Discover(); }
        catch (Exception ex)
        {
            LastDiscoverReport = $"varrimento falhou ({ex.GetType().Name}: {ex.Message}) — só paletas livres";
            try { return Builtin().ToList(); }
            catch { return new List<Palette>(); }
        }
    }

    private static List<Palette> Discover()
    {
        var sw = Stopwatch.StartNew();
        var list = new List<Palette>(Builtin());

        // Junta primeiro os caminhos todos: enumerar disco dentro do Parallel.ForEach dava
        // contenção de I/O sem ganho nenhum.
        var files = new List<string>();
        int skippedRoots = 0;
        foreach (var root in PaletteRoots())
        {
            if (sw.Elapsed > DiscoverBudget) { skippedRoots++; continue; }
            if (!Directory.Exists(root)) continue;
            try { files.AddRange(Directory.EnumerateFiles(root, "*.xml", SearchOption.AllDirectories)); }
            catch { skippedRoots++; }   // pasta sem permissão / caminho longo demais — segue
        }

        // Tecto duro: uma pasta com 100 mil XML (alguém apontou o ExtraRoots ao C:\) não pode
        // transformar a abertura da aba Cor numa indexação do disco inteiro.
        const int MaxFiles = 4000;
        int truncated = 0;
        if (files.Count > MaxFiles) { truncated = files.Count - MaxFiles; files.RemoveRange(MaxFiles, truncated); }

        var loaded = new ConcurrentBag<Palette>();
        int failed = 0, aborted = 0;

        // Ler XML é I/O + parse: paralelizar corta o varrimento dos livros do Corel para ~1/4.
        // A ordem que sai daqui é indeterminada, mas isso não interessa — ordena-se no fim.
        Parallel.ForEach(files, f =>
        {
            if (sw.Elapsed > DiscoverBudget) { System.Threading.Interlocked.Increment(ref aborted); return; }
            Palette? pal;
            try { pal = TryLoadCorelXml(f); }
            catch { System.Threading.Interlocked.Increment(ref failed); return; }   // ficheiro corrompido → ignora, nunca rebenta
            if (pal is not null && pal.Colors.Count > 0) loaded.Add(pal);
            else System.Threading.Interlocked.Increment(ref failed);
        });

        list.AddRange(loaded);

        // nomes repetidos (versões antigas do mesmo livro) → fica a que tem mais cores
        var final = list
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(p => p.Colors.Count).First())
            .OrderBy(p => p.Group, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        LastDiscoverReport =
            $"{final.Count} paletas, {final.Sum(p => p.Colors.Count)} cores, {sw.ElapsedMilliseconds} ms " +
            $"(ficheiros: {files.Count}, ignorados: {failed}, por tempo: {aborted}, cortados: {truncated}, raízes falhadas: {skippedRoots})";
        return final;
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
    /// Paleta XML do CorelDRAW. Há DOIS esquemas, e o KLIP tem de ler os dois:
    ///
    ///   novo:   &lt;cs name="PANTONE 185 C"&gt;&lt;color cs="LAB" tints="..."/&gt;&lt;color cs="RGB" .../&gt;&lt;/cs&gt;
    ///   antigo: &lt;colors&gt;&lt;page&gt;&lt;color name="TRUMATCH 1-A" cs="CMYK" tints="..."/&gt;
    ///
    /// O antigo é o do TRUMATCH, do PANTONE DS e de tudo o que está em "Previous Version" —
    /// 117 dos 177 ficheiros. Lê-los só pelo &lt;cs&gt; deixava o TRUMATCH INTEIRO de fora sem
    /// uma única mensagem de erro; era um buraco silencioso.
    ///
    /// Lido em STREAMING (XmlReader) e não com XDocument: são 23 MB de XML em 177 ficheiros e
    /// construir a árvore inteira de cada um só para deitar fora a seguir tornava o primeiro
    /// abrir da aba Cor num assunto de 10+ segundos.
    ///
    /// Preferimos RGB/sRGB para ecrã; se só houver LAB, convertemos; se só houver CMYK, aproximamos.
    /// </summary>
    private static Palette? TryLoadCorelXml(string file)
    {
        try
        {
            if (new FileInfo(file).Length > 24 * 1024 * 1024) return null;   // ficheiro absurdo → ignora
        }
        catch { return null; }

        // XmlResolver a null e DTD ignorado: um XML de paleta trocado (ou malicioso) não vai
        // buscar entidades à rede nem faz billion-laughs dentro do KLIP.
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            IgnoreProcessingInstructions = true,
            CloseInput = true,
        };

        string? name = null;
        var colors = new List<SpotColor>();

        try
        {
            using var r = XmlReader.Create(file, settings);

            if (!r.ReadToFollowing("palette")) return null;
            if (!r.Name.Equals("palette", StringComparison.OrdinalIgnoreCase)) return null;

            name = r.GetAttribute("name");
            // Os livros antigos não têm @name mas têm @prefix ("TRUMATCH ") — dá um nome de
            // paleta muito melhor ao utilizador do que o ficheiro em minúsculas ("trumatch").
            if (string.IsNullOrWhiteSpace(name)) name = r.GetAttribute("prefix")?.Trim();

            // estado do <cs> aberto (esquema novo)
            string? csName = null;
            float[]? rgb = null, lab = null, cmyk = null;

            while (r.Read())
            {
                if (r.NodeType == XmlNodeType.EndElement)
                {
                    if (csName is not null && r.Name.Equals("cs", StringComparison.OrdinalIgnoreCase))
                    {
                        AddModern(colors, csName, rgb, lab, cmyk);
                        csName = null; rgb = lab = cmyk = null;
                    }
                    continue;
                }
                if (r.NodeType != XmlNodeType.Element) continue;

                if (r.Name.Equals("cs", StringComparison.OrdinalIgnoreCase))
                {
                    // um <cs/> vazio nunca dá EndElement — fecha-se já, senão o estado
                    // ficava pendurado e a cor seguinte herdava o nome errado
                    var n = r.GetAttribute("name");
                    if (r.IsEmptyElement) { csName = null; rgb = lab = cmyk = null; continue; }
                    csName = string.IsNullOrWhiteSpace(n) ? null : n.Trim();
                    rgb = lab = cmyk = null;
                    continue;
                }

                if (!r.Name.Equals("color", StringComparison.OrdinalIgnoreCase)) continue;

                var space = (r.GetAttribute("cs") ?? "").ToUpperInvariant();
                var t = ParseTints(r.GetAttribute("tints"));
                if (t is null) continue;

                if (csName is not null)
                {
                    switch (space)   // o 1º RGB do ficheiro é o sRGB
                    {
                        case "RGB" or "SRGB" when t.Length >= 3: rgb ??= t; break;
                        case "LAB" when t.Length >= 3: lab ??= t; break;
                        case "CMYK" when t.Length >= 4: cmyk ??= t; break;
                    }
                    continue;
                }

                // esquema antigo: o <color> está sozinho e traz o nome em cima. Sem @name é um
                // reservado/separador do Corel (defcmyk.xml está cheio deles) — não é cor nenhuma.
                var cn = r.GetAttribute("name");
                if (!string.IsNullOrWhiteSpace(cn)) AddLegacy(colors, cn.Trim(), space, t);
            }
        }
        catch { return null; }   // XML truncado a meio, ficheiro bloqueado, encoding partido — ignora

        if (colors.Count == 0) return null;
        if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileNameWithoutExtension(file);

        var group = file.Contains("PANTONE", StringComparison.OrdinalIgnoreCase) ? "PANTONE"
                  : file.Contains(Path.DirectorySeparatorChar + "HKS", StringComparison.OrdinalIgnoreCase) ? "HKS"
                  : file.Contains(Path.DirectorySeparatorChar + "Spot" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? "Spot"
                  : "Process";
        return new Palette(name!, group, file, colors);
    }

    private static void AddModern(List<SpotColor> into, string csName, float[]? rgb, float[]? lab, float[]? cmyk)
    {
        uint argb; bool derivado = false;
        if (rgb is not null) argb = Pack(rgb[0], rgb[1], rgb[2]);
        else if (lab is not null) argb = LabToArgb(lab);
        else if (cmyk is not null) { argb = CmykToArgb(cmyk[0], cmyk[1], cmyk[2], cmyk[3]); derivado = true; }
        else return;

        into.Add(new SpotColor(
            csName, argb,
            cmyk?[0] ?? 0, cmyk?[1] ?? 0, cmyk?[2] ?? 0, cmyk?[3] ?? 0, cmyk is not null,
            // o Corel guarda o LAB normalizado: L em 0..1, a/b deslocados para 0..1 em ±128
            lab is not null ? lab[0] * 100f : 0,
            lab is not null ? lab[1] * 256f - 128f : 0,
            lab is not null ? lab[2] * 256f - 128f : 0,
            lab is not null, derivado));
    }

    private static void AddLegacy(List<SpotColor> into, string cn, string space, float[] t)
    {
        uint argb;
        float cy = 0, mg = 0, yl = 0, kk = 0; bool hasCmyk = false;
        float lL = 0, lA = 0, lB = 0; bool hasLab = false;

        switch (space)
        {
            case "CMYK" when t.Length >= 4:
                cy = t[0]; mg = t[1]; yl = t[2]; kk = t[3]; hasCmyk = true;
                argb = CmykToArgb(cy, mg, yl, kk);
                break;
            case "RGB" or "SRGB" when t.Length >= 3:
                argb = Pack(t[0], t[1], t[2]);
                break;
            case "LAB" when t.Length >= 3:
                argb = LabToArgb(t);
                lL = t[0] * 100f; lA = t[1] * 256f - 128f; lB = t[2] * 256f - 128f; hasLab = true;
                break;
            case "GRAY" or "GREY" when t.Length >= 1:
                // cinzento vem num único valor 0..1 (0 = preto); em CMYK é só o K ao contrário
                argb = Pack(t[0], t[0], t[0]);
                kk = 1f - t[0]; hasCmyk = true;
                break;
            default:
                return;
        }

        // só o ramo CMYK inventa o sRGB; o GRAY vem já como valor de ecrã
        into.Add(new SpotColor(cn, argb, cy, mg, yl, kk, hasCmyk, lL, lA, lB, hasLab,
                               RgbDerivado: space == "CMYK"));
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

    /// <summary>
    /// Procura por nome em todas as paletas (é assim que se escreve "185" e sai PANTONE 185 C).
    ///
    /// ORDENA POR RELEVÂNCIA, não por ordem de ficheiro: escrever "185" varria antes o
    /// "PANTONE 8185 C" e o "Process Blue 185-1" só porque estavam num livro mais acima no
    /// disco. Aqui o nome exacto vem primeiro, depois o número isolado como palavra, depois
    /// começa-por, e só no fim o contém-algures.
    /// </summary>
    public static IEnumerable<(Palette pal, SpotColor color)> Search(string query, int limit = 60)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<(Palette, SpotColor)>();
        if (limit <= 0) return Array.Empty<(Palette, SpotColor)>();
        var q = query.Trim();

        var hits = new List<(int rank, int group, int len, Palette pal, SpotColor color)>();
        foreach (var p in All)
        {
            int gp = GroupPriority(p.Group);
            foreach (var c in p.Colors)
            {
                int r = Rank(c.Name, q);
                if (r >= 0) hits.Add((r, gp, c.Name.Length, p, c));
            }
        }

        // Desempate: grupo antes de comprimento. "185" bate igualmente em "PANTONE 185 C" e em
        // "185:255  Gray" (uma entrada da rampa de cinzentos do Corel) — os dois com 13 letras —
        // e sem esta ordem era o cinzento que saía primeiro, exactamente o lixo que se queria
        // evitar. Depois o mais curto: entre "PANTONE 185 C" e "PANTONE 185 CP", o canónico é o curto.
        return hits
            .OrderBy(h => h.rank)
            .ThenBy(h => h.group)
            .ThenBy(h => h.len)
            .ThenBy(h => h.color.Name, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(h => (h.pal, h.color))
            .ToList();
    }

    /// <summary>Quem procura uma cor por nome procura quase sempre um livro de spot, não uma
    /// rampa genérica. Menor é melhor.</summary>
    private static int GroupPriority(string group) => group switch
    {
        "PANTONE" => 0,
        "HKS" => 1,
        "Spot" => 2,
        "Process" => 3,
        _ => 4,   // "Livre" (CSS/cinzentos) — só quando não há mais nada
    };

    /// <summary>
    /// Relevância: menor é melhor, −1 é "não serve".
    ///
    /// Uma só passagem de IndexOf decide tudo. Parece pormenor, mas isto corre 90 mil vezes por
    /// tecla premida na caixa de procura: com quatro varrimentos separados (Equals/token/
    /// StartsWith/Contains) a procura demorava mais de um segundo e escrevia-se aos soluços.
    /// </summary>
    private static int Rank(string name, string q)
    {
        const StringComparison IC = StringComparison.OrdinalIgnoreCase;

        int i = name.IndexOf(q, IC);
        if (i < 0) return -1;                                  // 99% das cores morrem aqui
        if (name.Length == q.Length) return 0;                 // igual (o IndexOf já provou que bate)

        // "palavra inteira": tem de ter fronteira dos dois lados — é o que faz "185" bater em
        // "PANTONE 185 C" e NÃO em "PANTONE 1850 C". Procura-se a MELHOR ocorrência, porque a
        // primeira pode ser a má ("PANTONE 1185 C 185"), e por isso continua-se a varrer.
        int best = i == 0 ? 2 : 3;                             // começa-por : contém-algures
        while (i >= 0)
        {
            if ((i == 0 || !char.IsLetterOrDigit(name[i - 1])) && IsBoundary(name, i + q.Length)) return 1;
            i = name.IndexOf(q, i + 1, IC);
        }
        return best;
    }

    private static bool IsBoundary(string name, int at) => at >= name.Length || !char.IsLetterOrDigit(name[at]);

    /// <summary>
    /// Cor mais próxima em ΔE2000 — "qual o PANTONE mais parecido com isto?".
    ///
    /// ΔE2000 e não a distância euclidiana do CIE76: a euclidiana trata 1 unidade de azul
    /// saturado como 1 unidade de cinzento, e por isso escolhia PANTONE azuis errados com
    /// uma confiança irritante. O ΔE2000 pesa croma, matiz e a rotação dos azuis.
    ///
    /// OS DOIS LADOS VÊM DO sRGB (D65), de propósito, e NÃO do Lab que o livro traz. Parece
    /// errado — o Lab do livro é medido com espectrofotómetro e é mais "verdadeiro" — mas os
    /// dois não são o mesmo espaço: o Corel gera o RGB do livro através de um perfil ICC, não
    /// desta matriz. Medido nos 43327 spots com Lab do livro, o desvio entre o Lab do livro e o
    /// Lab calculado do RGB do MESMO spot tem mediana 0.86 ΔE, p90 2.35 e máximo 33.8 (os
    /// fluorescentes, que estão fora do gamut sRGB e o Corel corta). Misturar os dois fazia com
    /// que perguntar pelo #E4002B — que É, byte a byte, o PANTONE 185 C — devolvesse um DIC a
    /// 0.74 e empurrasse o 185 C para 1.40. Comparar ecrã-com-ecrã é o que responde à pergunta
    /// que o utilizador faz de facto: "que spot se parece com este pixel?".
    /// </summary>
    public static (Palette pal, SpotColor color, double dE)? Nearest(uint argb, string? paletteName = null)
    {
        var query = ColorScience.SrgbToLab(argb);
        var idx = Index;
        if (idx.Length == 0) return null;

        // Arranca-se no L mais parecido e abre-se para os dois lados. O índice está ordenado por
        // L exactamente para isto: como ΔE2000 ≥ |ΔL|/SL e SL nunca passa de ~1.747, assim que
        // houver um candidato a ΔE=0.3 tudo o que esteja a mais de 0.53 de L é impossível de
        // ganhar e nem sequer se calcula. Sem isto eram 103 mil ΔE2000 por cada mexida do
        // conta-gotas — 160 ms, o suficiente para o rato "colar".
        const double SLmax = 1.7471;

        int lo = 0, hi = idx.Length;
        while (lo < hi) { int m = (lo + hi) >> 1; if (idx[m].L < query.L) lo = m + 1; else hi = m; }

        Palette? bp = null; SpotColor? bc = null; double bd = double.PositiveInfinity;

        void Consider(in MatchEntry e)
        {
            if (paletteName is { Length: > 0 } && !e.Pal.Name.Contains(paletteName, StringComparison.OrdinalIgnoreCase)) return;
            double d = ColorScience.DeltaE2000(query, (e.L, e.A, e.B));
            if (d > bd) return;

            // Empate a decidir por ordem do array era instável: o mesmo #FFD100 tanto dava um
            // "PANTONE+ CMYK Coated" como um "FASHION + HOME" têxtil, conforme a ordenação.
            // Quem faz o conta-gotas quer o livro de chapa, e quer o nome canónico.
            if (d == bd && bp is not null)
            {
                int g = GroupPriority(e.Pal.Group), gb = GroupPriority(bp.Group);
                if (g > gb) return;
                if (g == gb && e.Col.Name.Length >= bc!.Name.Length) return;
            }
            bd = d; bp = e.Pal; bc = e.Col;
        }

        // Nota: NÃO se sai mais cedo com ΔE=0. Não é preciso — com bd=0 a condição de corte passa
        // a "L diferente" e as duas varreduras param logo a seguir aos empates exactos, que são
        // justamente os que é preciso ver todos para escolher o melhor livro.
        for (int i = lo; i < idx.Length; i++)
        {
            if (idx[i].L - query.L > bd * SLmax) break;
            Consider(idx[i]);
        }
        for (int i = lo - 1; i >= 0; i--)
        {
            if (query.L - idx[i].L > bd * SLmax) break;
            Consider(idx[i]);
        }

        return bp is null ? null : (bp, bc!, bd);
    }

    /// <summary>
    /// Todas as cores candidatas já em Lab. Sem isto, cada <see cref="Nearest"/> refazia
    /// 90 mil conversões sRGB→Lab (3 Pow + 3 Cbrt cada) — quase um segundo por conta-gotas do
    /// rato. Converte-se uma vez e o conta-gotas passa a ser instantâneo.
    /// </summary>
    private static MatchEntry[] Index
    {
        get
        {
            var i = _index;
            if (i is not null) return i;
            var pals = All;   // fora do lock: o Monitor é reentrante, mas descobrir lá dentro
                              // prendia toda a gente durante os 2 s do varrimento
            lock (_gate)
            {
                if (_index is not null) return _index;
                var list = new List<MatchEntry>();
                foreach (var p in pals)
                {
                    if (p.Group == "Livre") continue;   // CSS/cinzentos não são cores de chapa
                    foreach (var c in p.Colors)
                    {
                        var (L, a, b) = ColorScience.SrgbToLab(c.Argb);
                        list.Add(new MatchEntry(p, c, L, a, b));
                    }
                }
                // ordenado por L: é o que permite ao Nearest cortar a busca (ver lá).
                list.Sort(static (x, y) => x.L.CompareTo(y.L));
                return _index = list.ToArray();
            }
        }
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
