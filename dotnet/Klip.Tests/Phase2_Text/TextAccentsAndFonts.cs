using System;
using System.IO;
using Klip.Engine;
using Klip.Model;
using Klip.Tests.Framework;
using SkiaSharp;

namespace Klip.Tests.Phase2_Text;

/// <summary>
/// TESTES DE ACEITAÇÃO DA FASE 2 — texto UTF-8/acentos + sistema de fontes.
///
/// Bug #1 do Leonel: escrever "coração/ação/português" dava MOJIBAKE (tinha de usar
/// MAIÚSCULAS sem diacríticos). Estes testes PROVAM, contra o motor REAL (TextShape +
/// Renderer), que ç/ã/õ/é/ê/á/à baked para outlines vetoriais renderizam como GLIFOS
/// verdadeiros — não como tofu/□ nem como mojibake (Ã§, Ã£, …).
///
/// PORQUÊ ESCAPES \uXXXX E NÃO LITERAIS ACENTUADOS:
///   As strings acentuadas são construídas com escapes Unicode (ç = ç, …) em vez
///   de bytes literais no .cs. Assim o teste NÃO depende do encoding do próprio ficheiro
///   fonte — os code points corretos entram na string INDEPENDENTEMENTE de como o Roslyn
///   leu o ficheiro. Isto isola a asserção: se falhar, o defeito está no motor, não no .cs.
///   (O mojibake real do Leonel vive na camada de TRANSPORTE — ControlServer/McpStdioBridge
///    a ler bytes UTF-8 como CP1252 — e é corrigido lá; aqui provamos que o RENDER é sã.)
///
/// COMO ACENDER: nada a fazer. O runner descobre [KlipTest] por reflexão e TextShape já
/// existe → estes correm REAIS já (sem #if, sem PendingException). `dotnet run --project
/// Klip.Tests -- --phase 2` mostra-os a PASS.
/// </summary>
public static class TextAccentsAndFonts
{
    private static readonly RenderHarness H = new();

    // ---- amostras construídas a partir de CODE POINTS explícitos (fonte 100% ASCII) --------
    // Não dependem do encoding do .cs: os pontos de código exatos entram na string quer o
    // Roslyn leia o ficheiro como UTF-8, quer não. Assim a prova é sobre ç/ã REAIS — nunca
    // sobre bytes mojibake que por acaso "passariam" no teste.
    //   ç = U+00E7   ã = U+00E3   õ = U+00F5   é = U+00E9   ê = U+00EA   á = U+00E1   à = U+00E0
    private const char CEDILHA_C = (char)0x00E7, TIL_A = (char)0x00E3, TIL_O = (char)0x00F5,
                       AGUDO_E = (char)0x00E9, CIRC_E = (char)0x00EA, AGUDO_A = (char)0x00E1,
                       GRAVE_A = (char)0x00E0;

    // "coração" = c o r a ç ã o
    private static readonly string Coracao = "cora" + CEDILHA_C + TIL_A + "o";
    // "coracao" = mesma palavra SEM diacríticos (baseline p/ contraste de outlines)
    private const string CoracaoPlain = "coracao";
    // "ãõçéêáà" = todos os diacríticos-alvo do PT
    private static readonly string AllDiacritics =
        new(new[] { TIL_A, TIL_O, CEDILHA_C, AGUDO_E, CIRC_E, AGUDO_A, GRAVE_A });
    // "aoceeaa" = os mesmos glifos-base SEM marcas (p/ provar que as marcas engordam os bounds)
    private const string AllDiacriticsStripped = "aoceeaa";

    // Comp largo o suficiente p/ a palavra caber sem clipping (texto centra em 0,0 = centro).
    private static Comp TextComp(string d, int w = 640, int h = 220) =>
        new(w, h, 30, 1.0, Rgba.White.ToArgb(),
            new[] { new Layer("txt", MorphTrack.Static(d), Rgba.Black.ToArgb()) });

    /// <summary>Bounds do path SVG "d" (via Skia) — largura/altura reais do desenho.</summary>
    private static SKRect BoundsOf(string d)
    {
        using var p = SKPath.ParseSvgPathData(d);
        return p is null ? SKRect.Empty : p.Bounds;
    }

    // ==========================================================================================
    // 1) ACENTOS — "coração" produz outlines NÃO-VAZIOS e DIFERENTES de "coracao", e desenha
    //    MAIS tinta (ç tem cedilha, ã tem til). Prova dupla: (a) SVG path data distinto;
    //    (b) contagem de pixels de conteúdo no frame renderizado é MAIOR que a versão plana.
    //    Se ç/ã caíssem em tofu/□ ou mojibake, ou não engordariam a tinta de forma coerente,
    //    ou dariam bounds absurdos — aqui ambos os sinais têm de bater certo.
    // ==========================================================================================
    [KlipTest(2, "acentos — \"coração\" ≠ \"coracao\" (outlines distintos + mais tinta)",
        Criterion = "ç/ã renderizam como glifos: path não-vazio, SVG diferente do plano, e +pixels no frame")]
    public static void Accents_Coracao_DistinctOutlinesAndMoreInk()
    {
        var dAcc   = TextShape.TextPathD(Coracao, 64);
        var dPlain = TextShape.TextPathD(CoracaoPlain, 64);

        // (a) contrato de path: ambos existem, não-vazios, e os acentos MUDAM os outlines.
        Assert.True(!string.IsNullOrEmpty(dAcc),   "TextPathD(\"coração\") devolve path não-vazio");
        Assert.True(!string.IsNullOrEmpty(dPlain), "TextPathD(\"coracao\") devolve path não-vazio");
        Assert.True(dAcc != dPlain,
            "os diacríticos ç/ã produzem SVG path data DIFERENTE de \"coracao\" (não são ignorados/tofu)");

        var bAcc = BoundsOf(dAcc!);
        Assert.Greater(bAcc.Width,  0, "\"coração\": bounds.Width > 0 (desenhou algo)");
        Assert.Greater(bAcc.Height, 0, "\"coração\": bounds.Height > 0 (desenhou algo)");

        // (b) prova no PIXEL: a versão acentuada tem estritamente MAIS tinta (cedilha + til).
        long inkAcc, inkPlain;
        using (var f = H.Render(TextComp(dAcc!), 0))   inkAcc   = f.ContentPixelCount(Rgba.White);
        using (var f = H.Render(TextComp(dPlain!), 0)) inkPlain = f.ContentPixelCount(Rgba.White);

        Assert.Greater(inkPlain, 0, "\"coracao\" renderiza tinta (sanidade do render de texto)");
        Assert.Greater(inkAcc,   0, "\"coração\" renderiza tinta");
        Assert.Greater(inkAcc, inkPlain,
            $"\"coração\" tem mais pixels de conteúdo que \"coracao\" (marcas ç/ã) — {inkAcc} vs {inkPlain}");
    }

    // ==========================================================================================
    // 2) TODOS OS DIACRÍTICOS PT — "ãõçéêáà" dá path não-vazio, bounds > 0, e é MAIS ALTO que a
    //    versão sem marcas ("aoceeaa"): til/acento/circunflexo ficam ACIMA e a cedilha ABAIXO,
    //    logo a altura total cresce. Prova que cada marca vira tinta em vez de desaparecer.
    // ==========================================================================================
    [KlipTest(2, "diacríticos PT — \"ãõçéêáà\" não-vazio, bounds>0, mais alto que sem marcas",
        Criterion = "string com todos os diacríticos: path não-vazio, bounds positivos, altura > base sem marcas")]
    public static void AllPortugueseDiacritics_NonEmptyTallerThanStripped()
    {
        var d = TextShape.TextPathD(AllDiacritics, 64);
        Assert.True(!string.IsNullOrEmpty(d), "TextPathD(\"ãõçéêáà\") devolve path não-vazio");

        var b = BoundsOf(d!);
        Assert.Greater(b.Width,  0, "\"ãõçéêáà\": bounds.Width > 0");
        Assert.Greater(b.Height, 0, "\"ãõçéêáà\": bounds.Height > 0");

        var dStripped = TextShape.TextPathD(AllDiacriticsStripped, 64);
        Assert.True(!string.IsNullOrEmpty(dStripped), "baseline \"aoceeaa\" não-vazio");
        var bStripped = BoundsOf(dStripped!);

        Assert.Greater(b.Height, bStripped.Height,
            $"marcas acima/abaixo elevam a altura: acentuado {b.Height:0.#} > sem-marcas {bStripped.Height:0.#}");

        // E confirma no frame que há tinta a sério (não só metadados de path).
        using var f = H.Render(TextComp(d!, 720, 260), 0);
        Assert.Greater(f.ContentPixelCount(Rgba.White), 0, "\"ãõçéêáà\" renderiza tinta no frame");
    }

    // ==========================================================================================
    // 3) FONTES — a fonte usada MUDA os outlines. Duas provas independentes:
    //    (a) 2 FAMÍLIAS de sistema garantidas no Windows (Arial vs Times New Roman) → SVG e
    //        bounds diferentes p/ o mesmo texto/size (serifa vs sem-serifa).
    //    (b) FONTE DE FICHEIRO via SKTypeface.FromFile (a fundação do verbo load_font): carregar
    //        um .ttf do disco e provar que os seus outlines diferem dos da default do sistema.
    //    Como o texto é BAKED em outlines no insert, chegar a SKTypeface certa ao GetTextPath é
    //    tudo o que a fonte precisa de fazer — é exatamente isto que se prova aqui.
    // ==========================================================================================
    [KlipTest(2, "fontes — família e ficheiro (.ttf) mudam os outlines",
        Criterion = "Arial≠Times (2 famílias) e SKTypeface.FromFile(.ttf)≠default: outlines/bounds diferentes")]
    public static void Fonts_FamilyAndFileChangeOutlines()
    {
        const string sample = "Aeg";  // glifos com contraste forte serifa/sem-serifa e peso
        const float size = 96f;

        // ---- (a) duas FAMÍLIAS de sistema -----------------------------------------------------
        // Pré-condição: as famílias têm de resolver para typefaces DISTINTOS (senão o teste seria
        // vácuo). No Windows 11 ambas existem; se alguma faltasse cairia na default e falhava aqui
        // com mensagem clara (é um problema de ambiente real, não um falso-verde).
        using (var tfA = SKTypeface.FromFamilyName("Arial"))
        using (var tfT = SKTypeface.FromFamilyName("Times New Roman"))
        {
            Assert.True(tfA is not null && tfT is not null, "Arial e Times New Roman resolvem para typefaces");
            Assert.True(!string.Equals(tfA!.FamilyName, tfT!.FamilyName, StringComparison.OrdinalIgnoreCase),
                $"famílias distintas resolvidas (Arial→'{tfA.FamilyName}', Times→'{tfT.FamilyName}')");
        }

        var dArial = TextShape.TextPathD(sample, size, "Arial", bold: false);
        var dTimes = TextShape.TextPathD(sample, size, "Times New Roman", bold: false);
        Assert.True(!string.IsNullOrEmpty(dArial), "Arial → path não-vazio");
        Assert.True(!string.IsNullOrEmpty(dTimes), "Times New Roman → path não-vazio");
        Assert.True(dArial != dTimes,
            "Arial e Times New Roman produzem SVG path data DIFERENTE p/ o mesmo texto (a fonte importa)");

        var bArial = BoundsOf(dArial!);
        var bTimes = BoundsOf(dTimes!);
        Assert.True(
            Math.Abs(bArial.Width - bTimes.Width) > 0.5 || Math.Abs(bArial.Height - bTimes.Height) > 0.5,
            $"bounds diferem entre famílias (Arial {bArial.Width:0.#}x{bArial.Height:0.#} vs " +
            $"Times {bTimes.Width:0.#}x{bTimes.Height:0.#})");

        // ---- (b) fonte de FICHEIRO (.ttf do disco) via SKTypeface.FromFile ---------------------
        // Fundação do verbo load_font: carregar um .ttf arbitrário e usá-lo no MESMO pipeline de
        // baking (SKFont.GetTextPath) que TextShape usa. Impact é condensada/pesada e distintíssima.
        string? ttf = FirstExistingFont("impact.ttf", "comic.ttf", "BROADW.TTF", "ITCBLKAD.TTF", "arialbd.ttf");
        Assert.True(ttf is not null,
            "existe pelo menos um .ttf de sistema p/ provar SKTypeface.FromFile (Impact/Comic/…)");

        using var tfFile = SKTypeface.FromFile(ttf);
        Assert.True(tfFile is not null, $"SKTypeface.FromFile carregou '{Path.GetFileName(ttf)}' do disco");

        var dFromFile = BakeWithTypeface(tfFile!, sample, size);
        Assert.True(!string.IsNullOrEmpty(dFromFile),
            $"outlines da fonte de ficheiro '{Path.GetFileName(ttf)}' não-vazios");
        Assert.True(dFromFile != dArial,
            $"a fonte carregada de ficheiro ('{Path.GetFileName(ttf)}') dá outlines DIFERENTES da Arial de sistema " +
            "(prova que FromFile alimenta o baking) ");
    }

    /// <summary>Devolve o 1º caminho existente em C:\Windows\Fonts entre os candidatos (ou null).</summary>
    private static string? FirstExistingFont(params string[] names)
    {
        var dir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        foreach (var n in names)
        {
            var p = Path.Combine(dir, n);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>
    /// Espelha EXATAMENTE o baking de TextShape (typeface → SKFont → GetTextPath → centrar → SVG),
    /// mas com uma SKTypeface arbitrária (ex.: carregada de ficheiro). É o seam que o verbo
    /// load_font vai usar; aqui serve p/ provar que uma fonte não-de-sistema muda os outlines.
    /// </summary>
    private static string? BakeWithTypeface(SKTypeface tf, string text, float size)
    {
        using var font = new SKFont(tf, size);
        using var path = font.GetTextPath(text, new SKPoint(0, 0));
        if (path is null || path.IsEmpty) return null;
        var b = path.Bounds;
        path.Transform(SKMatrix.CreateTranslation(-b.MidX, -b.MidY));
        return path.ToSvgPathData();
    }
}
