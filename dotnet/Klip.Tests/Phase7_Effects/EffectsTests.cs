using System;
using Klip.Engine;
using Klip.Model;
using Klip.Tests.Framework;

namespace Klip.Tests.Phase7_Effects;

/// <summary>
/// Fase 7 — EFEITOS PREMIUM provados por PIXELS reais: glow (halo aditivo), drop-shadow (offset+direção),
/// motion-blur (borrão DIRECIONAL que segue o movimento, no-op quando parado) e track-matte (recorte
/// alpha normal/invertido, com consumo da camada-fonte). Tudo keyframável pelo sistema uniforme.
/// </summary>
public static class EffectsTests
{
    private static readonly RenderHarness H = new();

    // ---- helper: caixa envolvente + contagem do conteúdo (dist ao fundo > threshold) ----
    private readonly record struct Box(long Count, int MinX, int MinY, int MaxX, int MaxY)
    {
        public int W => MaxX - MinX; public int Hh => MaxY - MinY;
    }
    private static Box Bounds(RenderHarness.Frame f, Rgba bg, double thr = 40)
    {
        long n = 0; int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        for (int y = 0; y < f.Height; y++)
        for (int x = 0; x < f.Width; x++)
            if (f.At(x, y).RgbDistance(bg) > thr)
            { n++; if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y; }
        return n == 0 ? new Box(0, 0, 0, 0, 0) : new Box(n, minX, minY, maxX, maxY);
    }

    // ------------------------------------------------------------------ TESTE 1 — GLOW acende halo
    [KlipTest(7, "glow: acende um halo de LUZ à volta da forma (fundo escuro, aditivo)",
        Criterion = "com glow há pixels acesos no anel onde sem glow era fundo escuro")]
    public static void GlowLightsHalo()
    {
        var dark = new Rgba(10, 10, 10, 255);
        Layer disc = new("d", MorphTrack.Static(Shapes.Circle(20)), 0xFFFFFFFF);
        var plain = new Comp(400, 400, 30, 0.1, 0xFF0A0A0A, new[] { disc });
        var glow = new Comp(400, 400, 30, 0.1, 0xFF0A0A0A, new[]
        {
            disc with { Glow = new GlowSpec(Radius: Track.Const(25), Intensity: Track.Const(1.0)) }
        });

        long aPlain, aGlow;
        using (var f = H.Render(plain, 0)) aPlain = f.ContentPixelCount(dark);
        using (var f = H.Render(glow, 0)) aGlow = f.ContentPixelCount(dark);
        Assert.Greater(aGlow, aPlain + 500, $"glow adiciona halo (sem={aPlain}, com={aGlow})");
        Assert.Greater(aGlow, aPlain * 1.2, "o halo é substancial (>20% mais área acesa)");

        // ponto no ANEL, fora do disco r=20 (a 30px do centro): apagado sem glow, aceso com glow
        using var fp = H.Render(plain, 0);
        using var fg = H.Render(glow, 0);
        Assert.Less(fp.AverageAround(230, 200, 3).RgbDistance(dark), 20, "sem glow o anel é fundo escuro");
        Assert.Greater(fg.AverageAround(230, 200, 3).RgbDistance(dark), 40, "com glow o anel está aceso");
    }

    // ------------------------------------------------------------------ TESTE 2 — DROP-SHADOW offset+direção
    [KlipTest(7, "drop-shadow: sombra deslocada (dx,dy) e DIRECIONAL atrás da forma (fundo claro)",
        Criterion = "escurece no offset (+dx,+dy), fundo no oposto, forma por cima no centro")]
    public static void DropShadowOffsetDirectional()
    {
        var white = Rgba.White;
        Layer disc = new("d", MorphTrack.Static(Shapes.Circle(25)), 0xFF101010);
        var plain = new Comp(400, 400, 30, 0.1, white.ToArgb(), new[] { disc });
        var shad = new Comp(400, 400, 30, 0.1, white.ToArgb(), new[]
        {
            disc with { DropShadow = new ShadowSpec(Track.Const(40), Track.Const(40), Track.Const(6), Track.Const(0.75)) }
        });

        using var fp = H.Render(plain, 0);
        using var fs = H.Render(shad, 0);
        // no offset (240,240), fora do disco central: sem sombra = branco; com sombra = escurecido
        Assert.Less(fp.AverageAround(240, 240, 3).RgbDistance(white), 20, "sem sombra o offset é branco");
        Assert.Greater(fs.AverageAround(240, 240, 3).RgbDistance(white), 80, "a sombra escurece o offset (+dx,+dy)");
        // DIRECIONAL: no lado oposto (160,160) NÃO há sombra (prova que não é glow simétrico)
        Assert.Less(fs.AverageAround(160, 160, 3).RgbDistance(white), 30, "o oposto (−dx,−dy) fica sem sombra → direcional");
        // a forma desenha-se POR CIMA da sombra no centro
        Assert.Greater(fs.AverageAround(200, 200, 3).RgbDistance(white), 200, "a forma cobre a sombra no centro");
    }

    // ------------------------------------------------------------------ TESTE 3 — MOTION-BLUR direcional
    [KlipTest(7, "motion-blur: borrão DIRECIONAL que segue a velocidade (alonga em X, não em Y)",
        Criterion = "bbox mais larga em X que a nítida; altura ~inalterada; rasto esbatido")]
    public static void MotionBlurDirectional()
    {
        var white = Rgba.White;
        Layer disc = new("d", MorphTrack.Static(Shapes.Circle(15)), 0xFF101010,
            PosX: Track.Of(new Keyframe(0.0, -150), new Keyframe(0.1, 150)));   // varre X depressa
        var crisp = new Comp(480, 160, 30, 0.2, white.ToArgb(), new[] { disc });
        var blur = new Comp(480, 160, 30, 0.2, white.ToArgb(), new[]
        {
            disc with { MotionBlur = Track.Const(1.5), MotionBlurSamples = 16 }
        });

        Box bc, bb;
        using (var f = H.Render(crisp, 0.05)) bc = Bounds(f, white, 25);
        using (var f = H.Render(blur, 0.05)) bb = Bounds(f, white, 25);
        Assert.Greater(bb.W, 60, $"o borrão alonga em X (largura={bb.W})");
        Assert.Greater(bb.W, bc.W * 1.6, $"muito mais largo que a nítida (nítida={bc.W}, blur={bb.W})");
        Assert.Less(Math.Abs(bb.Hh - bc.Hh), 14, $"altura ~inalterada → anisotropia alinhada com a velocidade (crisp={bc.Hh}, blur={bb.Hh})");
        // rasto: (290,80) está fora do disco nítido mas dentro do smear
        using var fc = H.Render(crisp, 0.05);
        using var fb = H.Render(blur, 0.05);
        Assert.Less(fc.AverageAround(290, 80, 3).RgbDistance(white), 15, "nítida: fora do disco = branco");
        Assert.Greater(fb.AverageAround(290, 80, 3).RgbDistance(white), 18, "blur: há rasto esbatido no caminho");
    }

    // ------------------------------------------------------------------ TESTE 4 — MOTION-BLUR só reage a movimento
    [KlipTest(7, "motion-blur: PARADO é no-op (não é blur gaussiano estático)",
        Criterion = "sem keyframes de movimento, a área é idêntica com/sem obturador")]
    public static void MotionBlurStaticNoOp()
    {
        var white = Rgba.White;
        Layer disc = new("d", MorphTrack.Static(Shapes.Circle(30)), 0xFF101010);   // PARADO
        var plain = new Comp(200, 200, 30, 0.1, white.ToArgb(), new[] { disc });
        var mb = new Comp(200, 200, 30, 0.1, white.ToArgb(), new[]
        {
            disc with { MotionBlur = Track.Const(1.5), MotionBlurSamples = 16 }
        });
        long a1, a2;
        using (var f = H.Render(plain, 0.05)) a1 = f.ContentPixelCount(white);
        using (var f = H.Render(mb, 0.05)) a2 = f.ContentPixelCount(white);
        Assert.Near(a1, a2, a1 * 0.03, $"parado → motion-blur é no-op (sem={a1}, com={a2})");
    }

    // ------------------------------------------------------------------ TESTE 5 — TRACK-MATTE alpha-normal
    [KlipTest(7, "track-matte alpha: recorta o alvo pelo alpha da fonte + consome a fonte",
        Criterion = "visível onde a fonte cobre; recortado onde não; a fonte não se auto-desenha")]
    public static void TrackMatteAlphaNormal()
    {
        var white = Rgba.White;
        Layer src = new("src", MorphTrack.Static(Shapes.Rect(120, 120)), 0xFF808080, PosX: Track.Const(120), Id: "matte_src");
        Layer tgt = new("tgt", MorphTrack.Static(Shapes.Circle(40)), 0xFF101010, Id: "tgt",
            MatteSourceId: "matte_src", Matte: MatteMode.AlphaNormal);
        var comp = new Comp(400, 400, 30, 0.1, white.ToArgb(), new[] { tgt, src });

        using var f = H.Render(comp, 0);
        Assert.Greater(f.AverageAround(225, 200, 3).RgbDistance(white), 200, "dentro do círculo E da fonte → visível");
        Assert.Less(f.AverageAround(175, 200, 3).RgbDistance(white), 20, "dentro do círculo mas fora da fonte → recortado");
        Assert.Less(f.AverageAround(300, 200, 3).RgbDistance(white), 20, "onde a fonte cobre mas fora do círculo → nada (fonte não se auto-desenha)");
    }

    // ------------------------------------------------------------------ TESTE 6 — TRACK-MATTE alpha-invert
    [KlipTest(7, "track-matte invertido: complemento exato do normal (DstOut)",
        Criterion = "visível onde a fonte NÃO cobre; recortado onde cobre")]
    public static void TrackMatteAlphaInvert()
    {
        var white = Rgba.White;
        Layer src = new("src", MorphTrack.Static(Shapes.Rect(120, 120)), 0xFF808080, PosX: Track.Const(120), Id: "matte_src");
        Layer tgt = new("tgt", MorphTrack.Static(Shapes.Circle(40)), 0xFF101010, Id: "tgt",
            MatteSourceId: "matte_src", Matte: MatteMode.AlphaInvert);
        var comp = new Comp(400, 400, 30, 0.1, white.ToArgb(), new[] { tgt, src });

        using var f = H.Render(comp, 0);
        Assert.Greater(f.AverageAround(175, 200, 3).RgbDistance(white), 200, "lado sem fonte → visível (invertido)");
        Assert.Less(f.AverageAround(225, 200, 3).RgbDistance(white), 20, "lado com fonte → recortado (invertido)");
    }

    // ------------------------------------------------------------------ TESTE 7 — keyframável pelo sistema uniforme
    [KlipTest(7, "efeitos: glow/sombra/motion-blur keyframáveis pelos verbos uniformes (sem verbos novos)",
        Criterion = "AddKeyframe/GetValue/Canonical funcionam nos novos paths")]
    public static void EffectsAreUniformKeyframable()
    {
        Layer l = new("x", MorphTrack.Static(Shapes.Circle(20)), 0xFF6D5EF6);
        l = PropRegistry.AddKeyframe(l, "glow.radius", 0, PropValue.Of(0.0));
        l = PropRegistry.AddKeyframe(l, "glow.radius", 1, PropValue.Of(40.0));
        Assert.Near(20, PropRegistry.GetValue(l, "glow.radius", 0.5).Scalar, 1e-6, "glow.radius anima 0→40 (meio=20)");
        Assert.True(l.Glow != null, "GlowSpec criado via path uniforme");

        l = PropRegistry.AddKeyframe(l, "shadow.dx", 0, PropValue.Of(10.0));
        l = PropRegistry.AddKeyframe(l, "shadow.opacity", 0, PropValue.Of(0.2));
        l = PropRegistry.AddKeyframe(l, "shadow.opacity", 1, PropValue.Of(0.8));
        Assert.True(l.DropShadow != null, "ShadowSpec criado via path uniforme");
        Assert.Near(0.5, PropRegistry.GetValue(l, "shadow.opacity", 0.5).Scalar, 1e-6, "shadow.opacity anima 0.2→0.8 (meio=0.5)");

        l = PropRegistry.AddKeyframe(l, "motion.blur", 0, PropValue.Of(1.5));
        Assert.True(l.MotionBlur != null, "MotionBlur track criada via path uniforme");
        Assert.Near(1.5, PropRegistry.GetValue(l, "motion.blur", 0.3).Scalar, 1e-6, "motion.blur = 1.5");

        Assert.True(PropRegistry.Canonical("mblur") == "motion.blur", "alias mblur resolve");
        Assert.True(PropRegistry.Canonical("glow_radius") == "glow.radius", "alias glow_radius resolve");
        Assert.True(PropRegistry.Canonical("shadow_blur") == "shadow.blur", "alias shadow_blur resolve");
    }
}
