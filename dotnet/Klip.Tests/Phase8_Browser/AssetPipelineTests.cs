using System;
using System.IO;
using System.Text;
using Klip.Engine;
using Klip.Model;
using Klip.Tests.Framework;
using SkiaSharp;

namespace Klip.Tests.Phase8_Browser;

/// <summary>Fase 8 — pipeline de assets E2E: baixar → validar (magic bytes) → rotear → a imagem
/// VIRA CAMADA e RENDERIZA (prova por pixels). Idempotência por content-hash; honestidade (HTML≠imagem).</summary>
public static class AssetPipelineTests
{
    private static readonly RenderHarness H = new();

    private static string TmpRoot() => Path.Combine(Path.GetTempPath(), "kliptest_assets_" + Guid.NewGuid().ToString("N"));

    private static string RedPngDataUrl(int size, SKColor color)
    {
        using var bmp = new SKBitmap(size, size);
        using (var cv = new SKCanvas(bmp)) cv.Clear(color);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return "data:image/png;base64," + Convert.ToBase64String(data.ToArray());
    }

    [KlipTest(8, "asset-pipeline: data-URL PNG → validado → camada → RENDERIZA (por pixels) + cache",
        Criterion = "baixar→sniff Image→/images→Layer(ImagePath)→render vermelho no centro; 2ª vez FromCache")]
    public static void DataUrlPngRenderizaCamada()
    {
        var red = new SKColor(0xE4, 0x16, 0x2B);
        string dataUrl = RedPngDataUrl(48, red);
        string root = TmpRoot();
        try
        {
            var res = AssetDownloader.Shared.DownloadAsync(dataUrl, root).GetAwaiter().GetResult();
            Assert.True(res.Kind == AssetKind.Image, $"data-URL PNG detetado como Image (obtive {res.Kind})");
            Assert.True(res.Ext == ".png", "extensão .png");
            Assert.True(File.Exists(res.Path), "ficheiro guardado no disco");
            Assert.True(res.Path.Replace('\\', '/').Contains("/images/"), "roteado para /images");

            // a imagem baixada VIRA CAMADA e RENDERIZA
            var layer = new Layer("img", MorphTrack.Static(Shapes.Rect(24, 24)), 0x00000000u, ImagePath: res.Path);
            var comp = new Comp(64, 64, 30, 0.1, 0xFFFFFFFFu, new[] { layer });
            using var f = H.Render(comp, 0);
            var c = f.AverageAround(32, 32, 3);
            Assert.Greater(c.R, 200, $"centro vermelho: R alto (obtive {c.R})");
            Assert.Less(c.G, 80, $"G baixo (obtive {c.G})");
            Assert.Less(c.B, 90, $"B baixo (obtive {c.B})");
            Assert.Greater(f.ContentPixelCount(Rgba.White), 0, "há conteúdo (imagem) sobre o fundo branco");

            // idempotência por content-hash
            var res2 = AssetDownloader.Shared.DownloadAsync(dataUrl, root).GetAwaiter().GetResult();
            Assert.True(res2.FromCache, "2ª descarga vem da cache (content-hash)");
            Assert.True(res2.Path == res.Path, "mesmo path (idempotente)");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [KlipTest(8, "asset-pipeline: HTML/erro NÃO finge ser imagem (roteado p/ downloads)",
        Criterion = "bytes de HTML → Other → /downloads, nunca /images")]
    public static void HtmlNaoFingeImagem()
    {
        var htmlBytes = Encoding.UTF8.GetBytes("<!DOCTYPE html><html><body>404 Not Found</body></html>");
        Assert.True(AssetSniffer.Detect(htmlBytes, out _) == AssetKind.Other, "sniff HTML → Other");

        string htmlDataUrl = "data:text/html;base64," + Convert.ToBase64String(htmlBytes);
        string root = TmpRoot();
        try
        {
            var res = AssetDownloader.Shared.DownloadAsync(htmlDataUrl, root).GetAwaiter().GetResult();
            Assert.True(res.Kind == AssetKind.Other, "HTML roteado como Other");
            Assert.True(res.Path.Replace('\\', '/').Contains("/downloads/"), "→ /downloads, NÃO /images");
            Assert.False(res.Path.Replace('\\', '/').Contains("/images/"), "NUNCA entra em /images");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [KlipTest(8, "asset-pipeline: download REAL remoto (HTTP+UA+magic bytes), tolerante a offline",
        Criterion = "PNG/JPEG remoto estável → Image; offline → PEND, nunca FAIL")]
    public static void DownloadRealRemotoTolerante()
    {
        const string url = "https://upload.wikimedia.org/wikipedia/commons/thumb/a/a9/Example.jpg/120px-Example.jpg";
        string root = TmpRoot();
        try
        {
            AssetResult r;
            try { r = AssetDownloader.Shared.DownloadAsync(url, root).GetAwaiter().GetResult(); }
            catch (Exception e) { throw new PendingException("sem rede / recurso indisponível: " + e.Message); }

            Assert.True(r.Kind == AssetKind.Image, $"download remoto validado como Image (obtive {r.Kind})");
            Assert.True(File.Exists(r.Path) && r.Path.Replace('\\', '/').Contains("/images/"), "guardado em /images");
            var r2 = AssetDownloader.Shared.DownloadAsync(url, root).GetAwaiter().GetResult();
            Assert.True(r2.FromCache, "2ª descarga do mesmo URL vem da cache");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
