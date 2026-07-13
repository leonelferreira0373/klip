using System.Linq;
using System.Text;
using Klip.Engine;
using Klip.Tests.Framework;

namespace Klip.Tests.Phase8_Browser;

/// <summary>Fase 8 — extração PURA de DOM (HTML→estrutura). Testável offline; o JS injetado no
/// browser devolve o mesmo shape. Prova absolutização, dedupe, caps e strip de &lt;script&gt;.</summary>
public static class DomExtractTests
{
    [KlipTest(8, "dom-extract: estrutura + absolutização de URLs relativos/protocol-relative",
        Criterion = "title/headings/links/images/videos/audios corretos e absolutos; <script> fora do texto")]
    public static void EstruturaEAbsolutizacao()
    {
        const string html =
            "<html><head><title>A &amp; B</title></head><body>" +
            "<h1>Bem-vindo</h1><h2>Sub</h2>" +
            "<a href=\"/produtos\">Produtos</a>" +
            "<a href=\"https://x.com/a\">Abs</a>" +
            "<a href=\"//cdn/z\">Proto</a>" +
            "<img src=\"a.png\" alt=\"imagem A\">" +
            "<video><source src=\"v.mp4\"></video>" +
            "<audio src=\"s.mp3\"></audio>" +
            "<script>ignora_isto()</script>" +
            "</body></html>";
        var r = DomExtract.Parse(html, "https://ex.com/loja/");

        Assert.True(r.Title == "A & B", $"title decodifica entidades (obtive '{r.Title}')");
        Assert.True(r.Headings.Count == 2 && r.Headings[0] == "Bem-vindo" && r.Headings[1] == "Sub", "headings h1+h2");
        Assert.True(r.Links[0].Href == "https://ex.com/produtos" && r.Links[0].Text == "Produtos", $"relativo → absoluto (obtive '{r.Links[0].Href}')");
        Assert.True(r.Links.Any(l => l.Href == "https://x.com/a"), "link absoluto preservado");
        Assert.True(r.Links.Any(l => l.Href == "https://cdn/z"), "protocol-relative → https");
        Assert.True(r.Images.Count == 1 && r.Images[0].Src == "https://ex.com/loja/a.png", $"img absolutizada (obtive '{(r.Images.Count > 0 ? r.Images[0].Src : "∅")}')");
        Assert.True(r.Videos.Count == 1 && r.Videos[0] == "https://ex.com/loja/v.mp4", "vídeo via <source> absolutizado");
        Assert.True(r.Audios.Count == 1 && r.Audios[0] == "https://ex.com/loja/s.mp3", "áudio src directo absolutizado");
        Assert.True(!r.Text.Contains("ignora_isto"), "<script> removido do texto legível");
    }

    [KlipTest(8, "dom-extract: dedupe de links iguais + cap de imagens (200)",
        Criterion = "2 links idênticos → 1; 250 imagens → 200")]
    public static void DedupeECaps()
    {
        var sb = new StringBuilder();
        sb.Append("<a href=\"/x\">X</a><a href=\"/x\">X</a>");
        for (int i = 0; i < 250; i++) sb.Append("<img src=\"/i").Append(i).Append(".png\">");
        var r = DomExtract.Parse(sb.ToString(), "https://e.com/");

        Assert.True(r.Links.Count == 1, $"links duplicados deduplicados (obtive {r.Links.Count})");
        Assert.True(r.Images.Count == 200, $"imagens limitadas a 200 (obtive {r.Images.Count})");
    }
}
