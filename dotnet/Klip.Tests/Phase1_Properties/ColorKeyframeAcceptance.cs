using Klip.Engine;
using Klip.Model;
using Klip.Tests.Framework;

namespace Klip.Tests.Phase1_Properties;

/// <summary>
/// TESTE DE ACEITAÇÃO DA FASE 1 — cor keyframável como propriedade uniforme.
///
/// Critério (do briefing): criar uma camada com COR keyframada (vermelho→azul em 1s)
/// e PROVAR, lendo o PIXEL do frame renderizado, que:
///   • t = 0.0s → centro é VERMELHO
///   • t = 0.5s → centro é ROXO   (mistura R+B)
///   • t = 1.0s → centro é AZUL
///
/// Há duas versões, com as MESMAS asserções de pixel:
///   1) <see cref="ColorTransition_Compositing_ProofToday"/> — PROVA HOJE, sem tocar no
///      modelo, via composição (opacity-kf de camada vermelha sobre azul). Garante que as
///      asserções e limiares de cor estão corretos e que o harness lê pixels a sério.
///   2) <see cref="ColorChannelKeyframe_Acceptance"/> — o teste FORTE contra a API nova
///      (cor-como-canal). Fica PENDING até a Fase 1 aterrar; depois vira PASS/FAIL real.
///      O ÚNICO ponto a implementar é o seam <see cref="MakeFillKeyframedLayer"/>.
/// </summary>
public static class ColorKeyframeAcceptance
{
    private static readonly RenderHarness H = new();

    private static Comp CompWith(params Layer[] layers) =>
        new(200, 200, 30, 1.0, Rgba.White.ToArgb(), layers);

    // ---- asserções de cor partilhadas (o "gabarito" vermelho→roxo→azul) --------------
    private static void AssertRed(Rgba p, double t)
    {
        Assert.Greater(p.R, 200, $"t={t}s centro vermelho: R alto ({p})");
        Assert.Less(p.G, 70, $"t={t}s centro vermelho: G baixo ({p})");
        Assert.Less(p.B, 70, $"t={t}s centro vermelho: B baixo ({p})");
    }

    private static void AssertBlue(Rgba p, double t)
    {
        Assert.Greater(p.B, 200, $"t={t}s centro azul: B alto ({p})");
        Assert.Less(p.R, 70, $"t={t}s centro azul: R baixo ({p})");
        Assert.Less(p.G, 70, $"t={t}s centro azul: G baixo ({p})");
    }

    private static void AssertPurpleMid(Rgba p, double t)
    {
        // Gate alargado p/ [90,210]: a interpolação de cor correcta é em sRGB-LINEAR premult,
        // cujo midpoint vermelho→azul ≈ (188,0,188) — mais alto que o lerp sRGB ingénuo (~127).
        // Continua a apanhar hold (R=255>210) e jump (R<90 ou B<90). G<70 + R≈B garantem "roxo".
        Assert.InRange(90, 210, p.R, $"t={t}s centro roxo: R a meio ({p})");
        Assert.InRange(90, 210, p.B, $"t={t}s centro roxo: B a meio ({p})");
        Assert.Less(p.G, 70, $"t={t}s centro roxo: G baixo ({p})");
        Assert.Less(System.Math.Abs(p.R - p.B), 70, $"t={t}s centro roxo: R≈B ({p})");
    }

    // ===================================================================================
    // 1) PROVA HOJE — composição source-over (não precisa de cor keyframável no modelo).
    //    Camada AZUL opaca por baixo; camada VERMELHA por cima com Opacity 1→0 em 1s.
    //    No centro: t0 vermelho, t0.5 vermelho@50% sobre azul = roxo, t1 azul.
    // ===================================================================================
    [KlipTest(1, "transição de cor (composição) — vermelho→roxo→azul",
        Criterion = "PROVA hoje, via compositing, que o harness distingue vermelho/roxo/azul no pixel central")]
    public static void ColorTransition_Compositing_ProofToday()
    {
        var blueBase = new Layer("base", MorphTrack.Static(Shapes.Circle(70)), Rgba.Blue.ToArgb());
        var redTop = new Layer("top", MorphTrack.Static(Shapes.Circle(70)), Rgba.Red.ToArgb(),
            Opacity: Track.Of(new Keyframe(0, 1), new Keyframe(1, 0)));
        var comp = CompWith(blueBase, redTop); // ordem: red desenhado por cima do blue

        AssertRed(H.SampleCenter(comp, 0.0), 0.0);
        AssertPurpleMid(H.SampleCenter(comp, 0.5), 0.5);
        AssertBlue(H.SampleCenter(comp, 1.0), 1.0);
    }

    // ===================================================================================
    // 2) ACEITAÇÃO FORTE — cor-como-canal (FillArgb deixa de ser uint estático).
    //    Uma ÚNICA camada cujo FILL é keyframado vermelho→azul. Mesmas asserções.
    // ===================================================================================
    [KlipTest(1, "cor keyframada (canal de propriedade) — vermelho→azul",
        Criterion = "camada única com FILL keyframado: pixel central é vermelho@0s, roxo@0.5s, azul@1s")]
    public static void ColorChannelKeyframe_Acceptance()
    {
        var comp = CompWith(
            MakeFillKeyframedLayer("hero", Shapes.Circle(70),
                from: Rgba.Red, to: Rgba.Blue, durationSeconds: 1.0));

        AssertRed(H.SampleCenter(comp, 0.0), 0.0);
        AssertPurpleMid(H.SampleCenter(comp, 0.5), 0.5);
        AssertBlue(H.SampleCenter(comp, 1.0), 1.0);
    }

    /// <summary>
    /// SEAM DA FASE 1 — constrói uma camada cujo FILL vai de <paramref name="from"/> a
    /// <paramref name="to"/> ao longo de <paramref name="durationSeconds"/>.
    ///
    /// É o ÚNICO ponto que depende do sistema de propriedades novo. O agente da Fase 1
    /// implementa isto com a API que escolher (4 tracks de canal, um ColorTrack, ou o
    /// property-bag (layerId, propPath)) e liga a constante KLIP_COLOR_KF no csproj.
    /// A referência abaixo assume 4 canais escalares FillR/FillG/FillB/FillA como Tracks
    /// no record Layer — ajusta se a API final diferir (uma linha por canal).
    /// </summary>
    private static Layer MakeFillKeyframedLayer(string name, string shapeD, Rgba from, Rgba to, double durationSeconds)
    {
#if KLIP_COLOR_KF
        // ---- API final da Fase 1: COR como canal uniforme (ColorTrack) --------
        return new Layer(name, MorphTrack.Static(shapeD), from.ToArgb(),
            FillColor: ColorTrack.Of(
                new ColorKey(0, from.ToArgb()),
                new ColorKey(durationSeconds, to.ToArgb())));
#else
        _ = (name, shapeD, from, to, durationSeconds);
        throw new PendingException(
            "cor-como-canal ainda não aterrou: define <DefineConstants>KLIP_COLOR_KF</DefineConstants> " +
            "e implementa o seam MakeFillKeyframedLayer contra a API de propriedades da Fase 1.");
#endif
    }
}
