using Klip.Engine;
using Klip.Model;
using Klip.Tests.Framework;

namespace Klip.Tests.Phase1_Properties;

/// <summary>
/// SELF-TESTS do harness (Fase 0) — provam que renderizamos frames REAIS e lemos
/// PIXELS reais do motor atual, sem qualquer alteração ao modelo. Se estes passam,
/// as asserções de cor/pixel usadas nos testes de aceitação são de confiança.
/// </summary>
public static class HarnessSelfTests
{
    private static readonly RenderHarness H = new();

    // Comp base: 200x200, fundo branco, 1s, 30fps.
    private static Comp CompWith(params Layer[] layers) =>
        new(200, 200, 30, 1.0, Rgba.White.ToArgb(), layers);

    [KlipTest(0, "fundo renderiza",
        Criterion = "background do comp aparece no pixel central quando não há camadas")]
    public static void BackgroundIsPainted()
    {
        var comp = new Comp(64, 64, 30, 1.0, Rgba.Red.ToArgb(), System.Array.Empty<Layer>());
        var px = H.SampleCenter(comp, 0);
        Assert.Less(px.RgbDistance(Rgba.Red), 12, "fundo central ≈ vermelho puro");
    }

    [KlipTest(0, "fill estático lê-se no pixel",
        Criterion = "camada azul preenchida → centro do frame é azul")]
    public static void StaticFillReadsBack()
    {
        var layer = new Layer("disc", MorphTrack.Static(Shapes.Circle(60)), Rgba.Blue.ToArgb());
        var px = H.Render(CompWith(layer), 0).AtDispose(f => f.Center());
        Assert.Less(px.RgbDistance(Rgba.Blue), 16, "centro ≈ azul puro");
    }

    [KlipTest(0, "opacidade keyframada muda o pixel",
        Criterion = "Opacity 0→1 em 1s: centro vai de fundo(branco) até fill(preto)")]
    public static void OpacityKeyframeMovesPixel()
    {
        var layer = new Layer("fade", MorphTrack.Static(Shapes.Circle(60)), Rgba.Black.ToArgb(),
            Opacity: Track.Of(new Keyframe(0, 0), new Keyframe(1, 1)));
        var comp = CompWith(layer);

        double d0 = H.SampleCenter(comp, 0).RgbDistance(Rgba.Black);   // longe do preto (é branco)
        double dh = H.SampleCenter(comp, 0.5).RgbDistance(Rgba.Black); // ~cinzento
        double d1 = H.SampleCenter(comp, 1).RgbDistance(Rgba.Black);   // ≈ preto

        Assert.Greater(d0, 300, "t=0 opacidade 0 → centro é fundo branco");
        Assert.Less(d1, 16, "t=1 opacidade 1 → centro é fill preto");
        Assert.Less(dh, d0, "t=0.5 aproxima-se do fill vs t=0");
        Assert.Greater(dh, d1, "t=0.5 ainda não chegou ao fill pleno");
    }

    [KlipTest(0, "PosX keyframada move o conteúdo",
        Criterion = "PosX -60→+60 em 1s: centróide do conteúdo passa da esquerda p/ a direita do centro")]
    public static void PosXKeyframeMovesContent()
    {
        var layer = new Layer("mover", MorphTrack.Static(Shapes.Circle(24)), Rgba.Black.ToArgb(),
            PosX: Track.Of(new Keyframe(0, -60), new Keyframe(1, 60)));
        var comp = CompWith(layer);
        int mid = comp.Width / 2;

        double cx0 = H.Render(comp, 0).AtDispose(f => f.ContentCentroidX(Rgba.White));
        double cx1 = H.Render(comp, 1).AtDispose(f => f.ContentCentroidX(Rgba.White));

        Assert.Less(cx0, mid - 20, "t=0 conteúdo à esquerda do centro");
        Assert.Greater(cx1, mid + 20, "t=1 conteúdo à direita do centro");
        Assert.Greater(cx1, cx0, "centróide moveu-se para a direita ao longo do tempo");
    }
}

/// <summary>Açúcar: renderiza, aplica f, faz Dispose do frame — evita fugas nos testes.</summary>
internal static class FrameExt
{
    public static T AtDispose<T>(this RenderHarness.Frame f, System.Func<RenderHarness.Frame, T> read)
    {
        using (f) return read(f);
    }
}
