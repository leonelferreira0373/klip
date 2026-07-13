using System;
using Klip.Engine;
using Klip.Model;
using Klip.Tests.Framework;

namespace Klip.Tests.Phase10_Particles;

/// <summary>
/// Fase 10 — PARTÍCULAS / EMISSORES provadas por PIXELS reais e DETERMINISMO.
///
/// Um <see cref="ParticleSpec"/> na camada (campo trailing <c>Particles</c>, retro-compat total)
/// gera N sprites (poses da <c>Shape</c> da camada) com posição/velocidade/escala/opacidade/cor/rotação
/// derivadas por PRNG SEMEADO puro-função de (Seed, índice-da-partícula) → o frame em t é 100%
/// reproduzível. Nada de Random/Date no motor, nada de estado mutável entre frames.
///
/// Modelo por partícula i (contrato que estes testes fixam):
///   birth_i = i / Rate           ; viva sse birth_i ≤ t &lt; birth_i + Lifetime ; frac = (t−birth_i)/Lifetime
///   h(Seed,i,canal) ∈ [−1,1]     ; ângulo = DirectionDeg ± h·SpreadDeg ; v = Speed·dir
///   pos(a) = origem + p0(SpawnRadius) + v·a + ½·(0,Gravity)·a²
///   opacidade = fade-in(FadeIn)·fade-out(FadeOut) por frac ; cor = lerp(ColorA,ColorB, h|frac)
///
/// Convenção do canvas: +Y é PARA BAIXO. Origem do emissor = transform da camada (default = centro).
/// </summary>
public static class ParticleEmitterTests
{
    private static readonly RenderHarness H = new();

    // ---------------------------------------------------------------- helpers de pixels
    private readonly record struct Box(long Count, int MinX, int MinY, int MaxX, int MaxY, double Cx, double Cy)
    {
        public int W => Count == 0 ? 0 : MaxX - MinX;
        public int Hh => Count == 0 ? 0 : MaxY - MinY;
    }

    /// <summary>Caixa envolvente + contagem + centróide do conteúdo (dist ao fundo &gt; thr).</summary>
    private static Box Bounds(RenderHarness.Frame f, Rgba bg, double thr = 40)
    {
        long n = 0; int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        double sumX = 0, sumY = 0;
        for (int y = 0; y < f.Height; y++)
        for (int x = 0; x < f.Width; x++)
            if (f.At(x, y).RgbDistance(bg) > thr)
            {
                n++; sumX += x; sumY += y;
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
            }
        return n == 0 ? new Box(0, 0, 0, 0, 0, double.NaN, double.NaN)
                      : new Box(n, minX, minY, maxX, maxY, sumX / n, sumY / n);
    }

    /// <summary>Soma-de-verificação estável de TODOS os pixels (prova determinismo byte-a-byte).</summary>
    private static long Checksum(RenderHarness.Frame f)
    {
        unchecked
        {
            long h = 1469598103934665603; // FNV-ish
            for (int y = 0; y < f.Height; y++)
            for (int x = 0; x < f.Width; x++)
            {
                var p = f.At(x, y);
                h = (h ^ p.R) * 1099511628211;
                h = (h ^ p.G) * 1099511628211;
                h = (h ^ p.B) * 1099511628211;
                h = (h ^ p.A) * 1099511628211;
            }
            return h;
        }
    }

    private const uint DarkBg = 0xFF0A0A0A;
    private static readonly Rgba Dark = new(10, 10, 10, 255);

    /// <summary>Sprite base minúsculo (Circle raio 6 → diâmetro ~12px) para o espalhamento ser óbvio.</summary>
    private static Layer Sprite(ParticleSpec spec) =>
        new("emit", MorphTrack.Static(Shapes.Circle(6)), 0xFFFFFFFF, Particles: spec);

    // ============================================================ TESTE 1 — ESPALHAMENTO
    [KlipTest(10, "emissor: espalha conteúdo numa CAIXA muito maior que 1 sprite (spread + velocidade)",
        Criterion = "bounds do conteúdo >> tamanho do sprite (~12px) a t médio")]
    public static void EmitterSpreadsFarBeyondOneSprite()
    {
        // omni (SpreadDeg=180 → cone de 360°), velocidade decente, muitas partículas
        var spec = new ParticleSpec(
            Seed: 7,
            Rate: Track.Const(200),
            Lifetime: Track.Const(1.0),
            Speed: Track.Const(150),
            SpreadDeg: Track.Const(180),
            ParticleScale: Track.Const(1.0));
        var comp = new Comp(400, 400, 30, 1.5, DarkBg, new[] { Sprite(spec) });

        // referência: UM sprite estático (sem emissor) → caixa ~ 12px
        var one = new Comp(400, 400, 30, 1.5, DarkBg,
            new[] { new Layer("one", MorphTrack.Static(Shapes.Circle(6)), 0xFFFFFFFF) });

        Box bOne, bEmit;
        using (var f = H.Render(one, 0.5)) bOne = Bounds(f, Dark);
        using (var f = H.Render(comp, 0.5)) bEmit = Bounds(f, Dark);

        Assert.Less(bOne.W, 30, $"1 sprite é pequeno em X (largura={bOne.W})");
        Assert.Less(bOne.Hh, 30, $"1 sprite é pequeno em Y (altura={bOne.Hh})");
        // a t=0.5, idades até 0.5s → distância até ~75px do centro → caixa ~150px em cada eixo (com folga)
        Assert.Greater(bEmit.W, 100, $"o emissor espalha largo em X (largura={bEmit.W})");
        Assert.Greater(bEmit.Hh, 100, $"o emissor espalha largo em Y (altura={bEmit.Hh})");
        Assert.Greater(bEmit.W, bOne.W * 5, "muito maior que 1 sprite em X (>5×)");
        Assert.Greater(bEmit.Hh, bOne.Hh * 5, "muito maior que 1 sprite em Y (>5×)");
    }

    // ============================================================ TESTE 2 — CRESCIMENTO
    [KlipTest(10, "emissor: as partículas ACUMULAM-se enquanto emite (área t=0.1 << t=1.0)",
        Criterion = "ContentPixelCount cresce claramente com o tempo enquanto Lifetime cobre a janela")]
    public static void EmitterAccumulatesOverTime()
    {
        // Lifetime 2.0 > 1.0 → tudo o que nasce em [0,1] continua vivo em t=1.0
        var spec = new ParticleSpec(
            Seed: 3,
            Rate: Track.Const(100),
            Lifetime: Track.Const(2.0),
            Speed: Track.Const(100),
            SpreadDeg: Track.Const(180),
            ParticleScale: Track.Const(1.0));
        var comp = new Comp(400, 400, 30, 2.5, DarkBg, new[] { Sprite(spec) });

        long early, late;
        using (var f = H.Render(comp, 0.1)) early = f.ContentPixelCount(Dark);
        using (var f = H.Render(comp, 1.0)) late = f.ContentPixelCount(Dark);

        Assert.Greater(early, 0, "há já algumas partículas cedo (t=0.1)");
        // ~11 partículas amontoadas no centro vs ~100 espalhadas → área muito maior (folga: >2×)
        Assert.Greater(late, early * 2.0, $"a área cresce com o tempo (t=0.1={early}, t=1.0={late})");
    }

    // ============================================================ TESTE 3 — GRAVIDADE
    [KlipTest(10, "emissor: com Gravity>0 o centróide Y cai ABAIXO da origem (partículas caem)",
        Criterion = "centróide Y (grav) > centróide Y (sem grav) e > centro do canvas")]
    public static void GravityPullsCentroidDown()
    {
        ParticleSpec Base(Track gravity) => new(
            Seed: 11,
            Rate: Track.Const(150),
            Lifetime: Track.Const(1.5),
            Speed: Track.Const(80),
            SpreadDeg: Track.Const(180),   // omni → sem gravidade o centróide fica ~ na origem
            Gravity: gravity,
            ParticleScale: Track.Const(1.0));

        var noGrav = new Comp(400, 400, 30, 2.0, DarkBg, new[] { Sprite(Base(Track.Const(0))) });
        var grav   = new Comp(400, 400, 30, 2.0, DarkBg, new[] { Sprite(Base(Track.Const(800))) });

        Box b0, bg;
        using (var f = H.Render(noGrav, 1.0)) b0 = Bounds(f, Dark);
        using (var f = H.Render(grav, 1.0)) bg = Bounds(f, Dark);

        Assert.Greater(b0.Count, 0, "há conteúdo sem gravidade");
        Assert.Greater(bg.Count, 0, "há conteúdo com gravidade");
        double cy = 200; // centro do canvas 400×400 = origem default do emissor
        // sem gravidade e omni → centróide ~ na origem (folga generosa de 25px)
        Assert.Less(Math.Abs(b0.Cy - cy), 25, $"sem gravidade o centróide Y ~ origem ({b0.Cy:0})");
        // com gravidade → puxado nitidamente para baixo (+Y)
        Assert.Greater(bg.Cy, cy + 30, $"com gravidade o centróide Y cai abaixo da origem ({bg.Cy:0})");
        Assert.Greater(bg.Cy, b0.Cy + 25, $"gravidade desce o centróide (sem={b0.Cy:0}, com={bg.Cy:0})");
    }

    // ============================================================ TESTE 4 — DETERMINISMO
    [KlipTest(10, "emissor: DETERMINISTA — render(comp,t) duas vezes → pixels idênticos byte-a-byte",
        Criterion = "ContentPixelCount E checksum de TODOS os pixels iguais entre dois renders")]
    public static void EmitterIsDeterministic()
    {
        var spec = new ParticleSpec(
            Seed: 424242,
            Rate: Track.Const(180),
            Lifetime: Track.Const(1.2),
            Speed: Track.Const(130),
            Gravity: Track.Const(300),
            SpreadDeg: Track.Const(120),
            DirectionDeg: -90,
            SpinDegPerSec: 240,
            ColorA: 0xFFFF3040,
            ColorB: 0xFF3040FF,
            ParticleScale: Track.Const(1.0));
        var comp = new Comp(420, 300, 30, 2.0, DarkBg, new[] { Sprite(spec) });

        long c1, c2, h1, h2;
        using (var f = H.Render(comp, 0.73)) { c1 = f.ContentPixelCount(Dark); h1 = Checksum(f); }
        using (var f = H.Render(comp, 0.73)) { c2 = f.ContentPixelCount(Dark); h2 = Checksum(f); }

        Assert.Greater(c1, 0, "há conteúdo para comparar");
        Assert.Equal((int)Math.Min(c1, int.MaxValue), (int)Math.Min(c2, int.MaxValue), "ContentPixelCount idêntico entre renders");
        Assert.True(h1 == h2, $"checksum de pixels idêntico → determinismo byte-a-byte (h1={h1}, h2={h2})");
    }

    // ============================================================ TESTE 5 — FADE no fim de vida
    [KlipTest(10, "emissor: FADE — perto do fim de vida a partícula fica mais fraca (opacidade cai)",
        Criterion = "1 partícula isolada e parada: cobertura/cor mais forte a meio que perto do fim")]
    public static void ParticleFadesNearEndOfLife()
    {
        // rate=1 → partícula 0 nasce em 0; partícula 1 só em t=1.0. Speed=0 → fica no centro (estacionária).
        // Lifetime=1.0 → em t∈[0,1) só a partícula 0 está viva; FadeOut recorta a cauda.
        var spec = new ParticleSpec(
            Seed: 5,
            Rate: Track.Const(1),
            Lifetime: Track.Const(1.0),
            Speed: Track.Const(0),
            SpreadDeg: Track.Const(0),
            Gravity: Track.Const(0),
            SpawnRadius: Track.Const(0),
            ParticleScale: Track.Const(1.0),
            FadeIn: 0.15,
            FadeOut: 0.4);
        var comp = new Comp(200, 200, 30, 1.5, DarkBg, new[]
        {
            new Layer("emit", MorphTrack.Static(Shapes.Circle(16)), 0xFFFFFFFF, Particles: spec)
        });

        // a meio da vida (frac≈0.5) → opacidade plena ; perto do fim (frac≈0.95) → dentro do fade-out
        double distMid, distEnd; long covMid, covEnd;
        using (var f = H.Render(comp, 0.5))  { distMid = f.AverageAround(100, 100, 3).RgbDistance(Dark); covMid = f.ContentPixelCount(Dark); }
        using (var f = H.Render(comp, 0.95)) { distEnd = f.AverageAround(100, 100, 3).RgbDistance(Dark); covEnd = f.ContentPixelCount(Dark); }

        Assert.Greater(distMid, 120, "a meio da vida a partícula está bem visível no centro");
        Assert.Greater(covMid, 0, "há cobertura a meio da vida");
        // fade-out: perto do fim a cor central aproxima-se do fundo (mais fraca) e/ou a cobertura AA diminui
        Assert.Less(distEnd, distMid * 0.7, $"perto do fim a partícula esmorece (meio={distMid:0}, fim={distEnd:0})");
        Assert.Less(covEnd, covMid, $"cobertura AA menor perto do fim (meio={covMid}, fim={covEnd})");
    }

    // ============================================================ TESTE 6 — RETRO-COMPAT
    [KlipTest(10, "retro-compat: Particles=null → render idêntico a hoje (emissor não dispara)",
        Criterion = "camada sem emissor = 1 sprite estável; pixels iguais entre dois renders")]
    public static void NullParticlesIsUnchanged()
    {
        // camada normal, campo Particles ausente (default null) → tem de continuar a ser 1 forma no centro
        var plain = new Layer("plain", MorphTrack.Static(Shapes.Circle(30)), 0xFFFFFFFF);
        Assert.True(plain.Particles == null, "campo Particles é opcional e default null (retro-compat de construção)");

        var comp = new Comp(300, 300, 30, 1.0, DarkBg, new[] { plain });

        Box b1, b2; long h1, h2;
        using (var f = H.Render(comp, 0.3)) { b1 = Bounds(f, Dark); h1 = Checksum(f); }
        using (var f = H.Render(comp, 0.3)) { b2 = Bounds(f, Dark); h2 = Checksum(f); }

        // é UM sprite (Circle raio 30 → ~60px), centrado, não um enxame espalhado
        Assert.InRange(40, 90, b1.W, $"largura de 1 sprite estático (={b1.W})");
        Assert.InRange(40, 90, b1.Hh, $"altura de 1 sprite estático (={b1.Hh})");
        Assert.Less(Math.Abs(b1.Cx - 150), 6, $"centrado em X ({b1.Cx:0})");
        Assert.Less(Math.Abs(b1.Cy - 150), 6, $"centrado em Y ({b1.Cy:0})");
        Assert.True(h1 == h2, "render sem emissor é estável (determinista) tal como sempre foi");
    }

    // ============================================================ TESTE 7 — KEYFRAMÁVEL (verbos uniformes)
    [KlipTest(10, "emissor: parâmetros globais keyframáveis pelo sistema uniforme (sem verbos novos)",
        Criterion = "AddKeyframe/GetValue nos paths particles.* criam o ParticleSpec lazy e animam")]
    public static void EmitterParamsAreUniformKeyframable()
    {
        Layer l = new("x", MorphTrack.Static(Shapes.Circle(6)), 0xFFFFFFFF);

        l = PropRegistry.AddKeyframe(l, "particles.rate", 0, PropValue.Of(0.0));
        l = PropRegistry.AddKeyframe(l, "particles.rate", 1, PropValue.Of(200.0));
        Assert.True(l.Particles != null, "ParticleSpec criado LAZY via path uniforme");
        Assert.Near(100, PropRegistry.GetValue(l, "particles.rate", 0.5).Scalar, 1e-6, "particles.rate anima 0→200 (meio=100)");

        l = PropRegistry.AddKeyframe(l, "particles.gravity", 0, PropValue.Of(600.0));
        Assert.Near(600, PropRegistry.GetValue(l, "particles.gravity", 0.3).Scalar, 1e-6, "particles.gravity = 600");

        l = PropRegistry.AddKeyframe(l, "particles.speed", 0, PropValue.Of(120.0));
        l = PropRegistry.AddKeyframe(l, "particles.spread", 0, PropValue.Of(45.0));
        l = PropRegistry.AddKeyframe(l, "particles.lifetime", 0, PropValue.Of(1.5));
        Assert.Near(120, PropRegistry.GetValue(l, "particles.speed", 0.4).Scalar, 1e-6, "particles.speed = 120");
        Assert.Near(45, PropRegistry.GetValue(l, "particles.spread", 0.4).Scalar, 1e-6, "particles.spread = 45");
        Assert.Near(1.5, PropRegistry.GetValue(l, "particles.lifetime", 0.4).Scalar, 1e-6, "particles.lifetime = 1.5");

        // aliases amigáveis para a IA
        Assert.True(PropRegistry.Canonical("rate") == "particles.rate", "alias rate resolve");
        Assert.True(PropRegistry.Canonical("gravity") == "particles.gravity", "alias gravity resolve");
        Assert.True(PropRegistry.Canonical("emitter_rate") == "particles.rate", "alias emitter_rate resolve");
    }
}
