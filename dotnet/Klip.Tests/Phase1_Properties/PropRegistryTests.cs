using System.Collections.Generic;
using Klip.Engine;
using Klip.Model;
using Klip.Tests.Framework;

namespace Klip.Tests.Phase1_Properties;

/// <summary>Fase 1 — o sistema de propriedades UNIFORME (endereçamento por path, incl. cor).
/// Prova, ao nível do modelo, que toda propriedade se keyframa da mesma maneira, os alias
/// legado continuam válidos, a cor semeia sem salto, e os IDs são estáveis.</summary>
public static class PropRegistryTests
{
    private static Layer L() => new("t", MorphTrack.Static(Shapes.Circle(10)), Rgba.Red.ToArgb());

    [KlipTest(1, "registry: aliases legado resolvem p/ paths canónicos")]
    public static void AliasesResolve()
    {
        (string a, string c)[] cases =
        {
            ("x", "position.x"), ("y", "position.y"), ("scale", "scale"), ("scale_x", "scale.x"),
            ("scale_y", "scale.y"), ("rotation", "rotation"), ("opacity", "opacity"), ("blur", "blur"),
            ("trim_start", "trim.start"), ("trim_end", "trim.end"), ("fill", "color.fill"), ("stroke", "color.stroke"),
        };
        foreach (var (a, c) in cases)
        {
            Assert.True(PropRegistry.Canonical(a) == c, $"{a}→{c} (obtive {PropRegistry.Canonical(a)})");
            Assert.True(PropRegistry.TryGet(a, out _), $"TryGet({a})");
        }
    }

    [KlipTest(1, "registry: keyframe uniforme em cada propriedade escalar anima")]
    public static void ScalarKeyframesUniform()
    {
        string[] paths = { "position.x", "position.y", "rotation", "scale", "scale.x", "scale.y", "skew.x", "blur", "opacity", "trim.start", "trim.end" };
        foreach (var p in paths)
        {
            var l = PropRegistry.AddKeyframe(L(), p, 0, PropValue.Of(0.0));
            l = PropRegistry.AddKeyframe(l, p, 1, PropValue.Of(50.0));
            Assert.Near(0, PropRegistry.GetValue(l, p, 0).Scalar, 1e-6, $"{p}@0");
            Assert.Near(50, PropRegistry.GetValue(l, p, 1).Scalar, 1e-6, $"{p}@1");
            Assert.Greater(PropRegistry.GetValue(l, p, 0.5).Scalar, 0.001, $"{p}@0.5 interpola");
        }
    }

    [KlipTest(1, "registry: cor keyframada semeia a 1ª kf (sem salto) e interpola")]
    public static void ColorKeyframeSeeds()
    {
        // camada vermelha; adiciona SÓ uma kf de azul em t=1 → semeia vermelho em t=0
        var l = PropRegistry.AddKeyframe(L(), "color.fill", 1.0, PropValue.Of(Rgba.Blue.ToArgb()));
        uint at0 = PropRegistry.GetValue(l, "color.fill", 0).Argb;
        uint at1 = PropRegistry.GetValue(l, "color.fill", 1).Argb;
        Assert.True(((at0 >> 16) & 0xFF) > 200 && (at0 & 0xFF) < 70, $"t=0 semeado vermelho (0x{at0:X8})");
        Assert.True((at1 & 0xFF) > 200 && ((at1 >> 16) & 0xFF) < 70, $"t=1 azul (0x{at1:X8})");
    }

    [KlipTest(1, "registry: expressão-código em cor é rejeitada (Fase 3)")]
    public static void ColorCodeRejected()
    {
        bool threw = false;
        try { PropRegistry.SetExpression(L(), "color.fill", new TrackExpr(ExprKind.Code, 0, 0, "value")); }
        catch (System.InvalidOperationException) { threw = true; }
        Assert.True(threw, "color.fill + Code deve lançar erro claro");
    }

    [KlipTest(1, "registry: expressão Wiggle em escalar mantém o motor procedural")]
    public static void WiggleStaysProcedural()
    {
        var l = PropRegistry.AddKeyframe(L(), "position.x", 0, PropValue.Of(0.0));
        l = PropRegistry.SetExpression(l, "position.x", new TrackExpr(ExprKind.Wiggle, 3, 40, null));
        // wiggle procedural → o valor varia ao longo do tempo (não fica preso em 0)
        double v1 = PropRegistry.GetValue(l, "position.x", 0.37).Scalar;
        double v2 = PropRegistry.GetValue(l, "position.x", 0.81).Scalar;
        Assert.True(v1 != 0.0 || v2 != 0.0, "wiggle produz vida procedural (não zero constante)");
    }

    [KlipTest(1, "registry: IDs estáveis distintos + Find por Id e por Name")]
    public static void StableIds()
    {
        string a = Ids.Next(), b = Ids.Next();
        Assert.True(a.Length > 0 && b.Length > 0 && a != b, $"Ids distintos não-vazios ({a},{b})");
        var layers = new List<Layer> { L() with { Id = a, Name = "circulo" }, L() with { Id = b, Name = "quadrado" } };
        Assert.Equal(0, PropRegistry.Find(layers, a), "Find por Id");
        Assert.Equal(1, PropRegistry.Find(layers, "quadrado"), "Find por Name (fallback)");
        Assert.Equal(-1, PropRegistry.Find(layers, "inexistente"), "Find falha → -1");
    }
}
