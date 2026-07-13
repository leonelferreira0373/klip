using System.IO;
using System.Text;
using Klip.Engine;
using Klip.Model;
using Klip.Tests.Framework;

namespace Klip.Tests.Phase2_Text;

/// <summary>Fase 2 — texto UTF-8/acentos. Prova, sem fakes, que ç/ã/õ/é renderizam como glifos
/// reais (outlines distintos do ASCII), que o texto acentuado desenha pixels, e isola a root
/// cause do mojibake (só o decode UTF-8 preserva o texto).</summary>
public static class AccentsTests
{
    private static readonly RenderHarness H = new();

    [KlipTest(2, "acentos: outlines distintos de sem-acentos (ç/ã/õ/é = glifos, não mojibake)")]
    public static void AccentsProduceDistinctOutlines()
    {
        Assert.True(TextShape.TextPathD("coração", 120) is { Length: > 0 }, "coração → path não-vazio");
        Assert.True(TextShape.TextPathD("coração", 120) != TextShape.TextPathD("coracao", 120), "coração ≠ coracao");
        Assert.True(TextShape.TextPathD("português", 120) != TextShape.TextPathD("portugues", 120), "português ≠ portugues");
        Assert.True(TextShape.TextPathD("ação", 120) != TextShape.TextPathD("acao", 120), "ação ≠ acao");
        Assert.True(TextShape.TextPathD("ãõçéêáà", 120) is { Length: > 0 }, "todos os diacríticos → path não-vazio");
    }

    [KlipTest(2, "acentos: texto acentuado renderiza pixels reais no frame")]
    public static void AccentedTextRendersPixels()
    {
        var d = TextShape.TextPathD("coração", 90)!;
        var layer = new Layer("txt", MorphTrack.Static(d), 0xFF101010);   // texto preto
        var comp = new Comp(240, 130, 30, 1.0, Rgba.White.ToArgb(), new[] { layer });
        using var f = H.Render(comp, 0);
        long content = f.ContentPixelCount(Rgba.White);
        Assert.Greater(content, 200, $"glifos acentuados desenham conteúdo (px={content})");
    }

    [KlipTest(2, "encoding: só o decode UTF-8 preserva 'coração' (isola a root cause do mojibake)")]
    public static void Utf8DecodePreservesAccents()
    {
        var bytes = Encoding.UTF8.GetBytes("coração");
        // espelho EXATO do fix: StreamReader UTF-8 sobre os bytes crus (como o --mcp-stdio agora lê o stdin)
        using var sr = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false);
        string correct = sr.ReadToEnd();
        // simula a consola Windows legada (Latin1/CP1252) a descodificar os MESMOS bytes → o bug antigo
        string wrong = Encoding.Latin1.GetString(bytes);
        Assert.True(correct == "coração", $"UTF-8 preserva ('{correct}')");
        Assert.True(wrong != "coração", $"Latin1 estraga → mojibake ('{wrong}')");
        Assert.True(TextShape.TextPathD(wrong, 100) != TextShape.TextPathD("coração", 100),
            "os outlines do mojibake diferem dos corretos → o decode errado desenharia glifos errados");
    }
}
