using System;
using Klip.Engine;
using Klip.Model;
using Klip.Tests.Framework;
using SkiaSharp;

namespace Klip.Tests.Phase6_Morph;

/// <summary>
/// Fase 6 — MORPH REAL de paths (quadrado→círculo e afins). Estes testes PROVAM que a
/// interpolação é uma forma-A→forma-B genuína (reamostragem por comprimento de arco +
/// correspondência cíclica + lerp ponto-a-ponto), e NÃO o antigo fake "colapsar-e-florescer"
/// (a forma encolhe até ~zero a meio e volta a crescer) nem um cross-fade de opacidade.
///
/// Geometria de referência (quadrado de meio-lado 150 vs círculo de raio 150, ambos centrados):
///   • área do quadrado ≈ 300×300 = 90000 px
///   • área do círculo  ≈ π·150² ≈ 70686 px
///   • a forma intermédia (t=0.5) é um "quadrado arredondado" com área ENTRE as duas,
///     e o seu canto move-se para dentro (fica a meio caminho entre o vértice do quadrado,
///     raio ≈ 212 na diagonal, e o alvo no círculo, raio 150 → raio ≈ 181 a t=0.5).
/// </summary>
public static class MorphRealTests
{
    private static readonly RenderHarness H = new();

    // Canvas 600×600, formas centradas em (300,300). Formas comparáveis em tamanho p/ o morph ler bem.
    private const int W = 600, Hgt = 600;
    private const double Cx = 300, Cy = 300;
    private static readonly uint Fill = 0xFF101010;      // quase-preto opaco (contrasta com fundo branco)
    private static readonly Rgba Bg = Rgba.White;

    private static Comp SquareToCircle() => new(
        W, Hgt, 30, 1.0, Bg.ToArgb(),
        new[]
        {
            new Layer("morph",
                new MorphTrack(new[]
                {
                    new MorphKey(0.0, Shapes.Rect(150, 150), Easing.Linear),   // t=0 → quadrado 300×300
                    new MorphKey(1.0, Shapes.Circle(150),   Easing.Linear),   // t=1 → círculo r=150
                }),
                Fill),
        });

    // -----------------------------------------------------------------------------------------
    // TESTE 1 — a t=0.5 a forma é MESMO intermédia: área entre quadrado e círculo, e NÃO colapsa.
    // -----------------------------------------------------------------------------------------
    [KlipTest(6, "morph quadrado→círculo: área a t=0.5 é intermédia e NÃO colapsa (rejeita fake)",
        Criterion = "ContentPixelCount(0.5) entre círculo e quadrado, longe de zero")]
    public static void MidFrameIsGenuinelyIntermediate()
    {
        var comp = SquareToCircle();
        long a0, a5, a1;
        using (var f = H.Render(comp, 0.0)) a0 = f.ContentPixelCount(Bg);
        using (var f = H.Render(comp, 0.5)) a5 = f.ContentPixelCount(Bg);
        using (var f = H.Render(comp, 1.0)) a1 = f.ContentPixelCount(Bg);

        // Sanidade dos extremos: quadrado é maior que círculo (áreas geométricas conhecidas).
        Assert.Greater(a0, 80000, $"t=0 desenha o quadrado cheio (~90000 px), obtive {a0}");
        Assert.InRange(62000, 78000, a1, $"t=1 desenha o círculo (~70686 px), obtive {a1}");
        Assert.Greater(a0, a1, $"quadrado ({a0}) tem mais área que o círculo ({a1})");

        long lo = Math.Min(a0, a1), hi = Math.Max(a0, a1);

        // A ESSÊNCIA anti-fake: no "colapsar-e-florescer" a área a meio despencaria para perto de zero.
        // Aqui exigimos que fique perto das pontas — muito acima de qualquer colapso.
        Assert.Greater(a5, 0.80 * lo,
            $"t=0.5 NÃO colapsa: área {a5} tem de ficar >80% do mínimo das pontas ({lo}) — fake colapsaria p/ ~0");

        // E que seja genuinamente INTERMÉDIA: entre círculo e quadrado (com folga p/ AA/arredondamento).
        Assert.InRange(lo * 0.95, hi * 1.02, a5,
            $"t=0.5 fica ENTRE círculo ({a1}) e quadrado ({a0}) — obtive {a5}");
    }

    // -----------------------------------------------------------------------------------------
    // TESTE 2 — a forma NÃO desaparece a t=0.5: centróide e bounds mantêm-se estáveis e centrados.
    // -----------------------------------------------------------------------------------------
    [KlipTest(6, "morph: a t=0.5 a forma continua centrada e presente (centróide/bounds estáveis)",
        Criterion = "centróide ≈ centro do canvas e caixa envolvente cheia, sem sumiço")]
    public static void MidFrameStaysCenteredAndPresent()
    {
        var comp = SquareToCircle();
        using var f = H.Render(comp, 0.5);

        var b = ContentBounds(f, Bg);
        Assert.Greater(b.Count, 60000, $"a forma a t=0.5 está bem presente (obtive {b.Count} px de conteúdo)");

        // Centróide colado ao centro do canvas nos dois eixos (a forma é simétrica e não deriva).
        Assert.Near(Cx, b.CentroidX, 6, "centróide X a t=0.5 ≈ centro do canvas");
        Assert.Near(Cy, b.CentroidY, 6, "centróide Y a t=0.5 ≈ centro do canvas");

        // A caixa envolvente é grande e ~centrada — a forma ocupa a região esperada (não é um ponto/linha).
        double bw = b.MaxX - b.MinX, bh = b.MaxY - b.MinY;
        Assert.InRange(280, 320, bw, $"largura da caixa a t=0.5 ~ 300px (quadrado-arredondado), obtive {bw}");
        Assert.InRange(280, 320, bh, $"altura da caixa a t=0.5 ~ 300px, obtive {bh}");
        Assert.Near(Cx, (b.MinX + b.MaxX) / 2.0, 6, "caixa centrada em X");
        Assert.Near(Cy, (b.MinY + b.MaxY) / 2.0, 6, "caixa centrada em Y");
    }

    // -----------------------------------------------------------------------------------------
    // TESTE 3 — suavidade: amostrar t=0,.25,.5,.75,1 e provar que a área evolui sem VALE abrupto
    // no meio (o fake colapsar-e-florescer faz um V profundo: alto→~0→alto).
    // -----------------------------------------------------------------------------------------
    [KlipTest(6, "morph: área evolui suave de A→B (t=0..1) sem vale de colapso a meio",
        Criterion = "sem queda abrupta no meio; cada amostra intermédia dentro da banda dos vizinhos")]
    public static void AreaEvolvesSmoothlyNoCollapseValley()
    {
        var comp = SquareToCircle();
        double[] ts = { 0.0, 0.25, 0.5, 0.75, 1.0 };
        var area = new long[ts.Length];
        for (int i = 0; i < ts.Length; i++)
            using (var f = H.Render(comp, ts[i])) area[i] = f.ContentPixelCount(Bg);

        long lo = Math.Min(area[0], area[4]);   // menor das pontas (= círculo)
        long hi = Math.Max(area[0], area[4]);   // maior das pontas (= quadrado)

        // Nenhuma amostra pode cair para perto de zero: um colapso a meio daria área ínfima.
        for (int i = 0; i < area.Length; i++)
            Assert.Greater(area[i], 0.80 * lo,
                $"amostra t={ts[i]:0.##} (área={area[i]}) nunca colapsa abaixo de 80% do mínimo das pontas ({lo})");

        // Todas as amostras dentro da banda geométrica [círculo·0.9 .. quadrado·1.05].
        for (int i = 0; i < area.Length; i++)
            Assert.InRange(lo * 0.9, hi * 1.05, area[i],
                $"amostra t={ts[i]:0.##} dentro da banda esperada, obtive {area[i]}");

        // SUAVIDADE local: cada amostra interior fica entre os vizinhos (± folga) — nem vale nem pico.
        for (int i = 1; i <= 3; i++)
        {
            long lohi = Math.Min(area[i - 1], area[i + 1]);
            long hihi = Math.Max(area[i - 1], area[i + 1]);
            Assert.InRange(lohi * 0.93, hihi * 1.03, area[i],
                $"t={ts[i]:0.##} (área={area[i]}) fica entre os vizinhos [{area[i - 1]}, {area[i + 1]}] — sem vale/pico");
        }
    }

    // -----------------------------------------------------------------------------------------
    // TESTE 4 — prova POSITIVA de "intermédio real": o canto move-se para dentro e a forma a t=0.5
    // é DIFERENTE de ambos os extremos (não é o quadrado, não é o círculo, não é um cross-fade dos dois).
    // -----------------------------------------------------------------------------------------
    [KlipTest(6, "morph: a t=0.5 o canto migrou — forma distinta do quadrado E do círculo",
        Criterion = "ponto na diagonal preenchido no quadrado e a t=0.5, mas fundo no círculo")]
    public static void CornerActuallyMovesInward()
    {
        var comp = SquareToCircle();

        // Ponto de teste na diagonal, raio 165 do centro (local 116.7,116.7 → canvas 416.7,416.7).
        //   • quadrado (t=0): raio 165 < 212 (vértice) → PREENCHIDO
        //   • intermédio (t=0.5): canto a ~raio 181 na diagonal → 165 < 181 → PREENCHIDO
        //   • círculo (t=1): raio 165 > 150 → FUNDO (o círculo não chega lá)
        const int px = 417, py = 417;

        bool Filled(double t)
        {
            using var f = H.Render(comp, t);
            return f.AverageAround(px, py, 2).RgbDistance(Bg) > 40;
        }

        Assert.True(Filled(0.0), "diagonal raio 165 está DENTRO do quadrado a t=0");
        Assert.True(Filled(0.5), "diagonal raio 165 ainda está DENTRO da forma intermédia a t=0.5 (canto migrou p/ ~181)");
        Assert.False(Filled(1.0), "diagonal raio 165 está FORA do círculo a t=1 (r=150) — logo t=0.5 ≠ círculo");

        // Corolário: como t=0.5 preenche onde o círculo é fundo, a forma intermédia NÃO é o círculo
        // nem um cross-fade (que a essa opacidade já não pintaria além do raio do círculo de forma sólida).
    }

    // -----------------------------------------------------------------------------------------
    // TESTE 5 — MULTI-CONTORNO: um anel (exterior + BURACO) a morphar para um quadrado sólido tem
    // de MANTER o furo durante a transição (o v1 single-contour perdia o buraco silenciosamente).
    // Prova o suporte a formas com furos: letras "O/A/B", donuts, anéis.
    // -----------------------------------------------------------------------------------------

    /// <summary>Anel centrado na origem: exterior CW (raio out) + buraco CCW (raio in) → furo sob winding.</summary>
    private static string Ring(float outerR, float innerR)
    {
        using var p = new SKPath();
        p.AddCircle(0, 0, outerR, SKPathDirection.Clockwise);
        p.AddCircle(0, 0, innerR, SKPathDirection.CounterClockwise);
        return p.ToSvgPathData();
    }

    [KlipTest(6, "morph com BURACO: anel→quadrado preserva o furo durante a transição (multi-contorno)",
        Criterion = "centro no furo (fundo) em t=0 e t=0.5; só enche quando o furo fecha em t=1")]
    public static void HolePreservedThroughMorph()
    {
        var comp = new Comp(
            W, Hgt, 30, 1.0, Bg.ToArgb(),
            new[]
            {
                new Layer("ring",
                    new MorphTrack(new[]
                    {
                        new MorphKey(0.0, Ring(150, 70), Easing.Linear),          // anel: exterior r150 + furo r70
                        new MorphKey(1.0, Shapes.Rect(150, 150), Easing.Linear),  // quadrado sólido (sem furo)
                    }),
                    Fill),
            });

        // O centro do canvas cai no BURACO enquanto o anel existe; só enche quando o furo fecha (t→1).
        bool CenterFilled(double t)
        {
            using var f = H.Render(comp, t);
            return f.AverageAround((int)Cx, (int)Cy, 3).RgbDistance(Bg) > 40;
        }

        Assert.False(CenterFilled(0.0), "t=0: o centro está no furo do anel → fundo (buraco aberto)");
        Assert.False(CenterFilled(0.5), "t=0.5: o furo continua aberto — multi-contorno preservado, não fundido");
        Assert.True(CenterFilled(1.0),  "t=1: quadrado sólido → o furo fechou, centro preenchido");

        // Sanidade: o anel desenha mesmo o exterior (não é frame vazio) e tem MENOS área que um disco cheio.
        using (var f = H.Render(comp, 0.0))
        {
            long ringArea = f.ContentPixelCount(Bg);
            Assert.Greater(ringArea, 40000, $"t=0: o anel desenha o exterior ({ringArea} px)");
            Assert.Less(ringArea, 68000, $"t=0: o anel tem furo → área < disco cheio (~70686), obtive {ringArea}");
        }
    }

    // ---- helper: caixa envolvente + centróide do conteúdo (o harness só expõe centróide X) --------------
    private readonly record struct Bounds(long Count, double CentroidX, double CentroidY,
                                           int MinX, int MinY, int MaxX, int MaxY);

    private static Bounds ContentBounds(RenderHarness.Frame f, Rgba bg, double threshold = 40)
    {
        long n = 0; double sx = 0, sy = 0;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        for (int y = 0; y < f.Height; y++)
        for (int x = 0; x < f.Width; x++)
        {
            if (f.At(x, y).RgbDistance(bg) <= threshold) continue;
            n++; sx += x; sy += y;
            if (x < minX) minX = x; if (x > maxX) maxX = x;
            if (y < minY) minY = y; if (y > maxY) maxY = y;
        }
        if (n == 0) return new Bounds(0, double.NaN, double.NaN, 0, 0, 0, 0);
        return new Bounds(n, sx / n, sy / n, minX, minY, maxX, maxY);
    }
}
