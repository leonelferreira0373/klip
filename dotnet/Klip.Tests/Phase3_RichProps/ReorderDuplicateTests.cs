using Klip.Engine;
using Klip.Model;
using Klip.Tests.Framework;

namespace Klip.Tests.Phase3_RichProps;

/// <summary>Fase 3 — z-index (ordem = composição) + duplicar-com-keyframes (record with preserva a animação).
/// Cor/kf e scale.x/scale.y já vêm da Fase 1 (sistema uniforme). Aqui provamos o que falta.</summary>
public static class ReorderDuplicateTests
{
    private static readonly RenderHarness H = new();

    [KlipTest(3, "z-index: a ordem das camadas muda a composição no pixel central")]
    public static void OrderAffectsCompositing()
    {
        var red = new Layer("r", MorphTrack.Static(Shapes.Circle(70)), Rgba.Red.ToArgb());
        var blue = new Layer("b", MorphTrack.Static(Shapes.Circle(70)), Rgba.Blue.ToArgb());
        // fim da lista = desenhado por cima
        var redTop = new Comp(200, 200, 30, 1.0, Rgba.White.ToArgb(), new[] { blue, red });
        var blueTop = new Comp(200, 200, 30, 1.0, Rgba.White.ToArgb(), new[] { red, blue });
        Assert.Greater(H.SampleCenter(redTop, 0).R, 200, "red no fim → centro vermelho");
        Assert.Greater(H.SampleCenter(blueTop, 0).B, 200, "blue no fim → centro azul");
    }

    [KlipTest(3, "duplicar: o clone (record with) preserva TODOS os keyframes")]
    public static void DuplicatePreservesKeyframes()
    {
        var l = new Layer("a", MorphTrack.Static(Shapes.Circle(30)), 0xFF112233,
            Opacity: Track.Of(new Keyframe(0, 0), new Keyframe(1, 1)),
            PosX: Track.Of(new Keyframe(0, -50), new Keyframe(1, 50)));
        var dup = l with { Name = "a-copy", Id = "ly_test" };
        Assert.True(dup.Name != l.Name && dup.Id != l.Id, "clone tem nome/id distintos");
        Assert.Near(l.Opacity!.Eval(0.5), dup.Opacity!.Eval(0.5), 1e-9, "opacity keyframada preservada");
        Assert.Near(l.PosX!.Eval(0.5), dup.PosX!.Eval(0.5), 1e-9, "posX keyframada preservada");
        Assert.Near(0.0, dup.PosX!.Eval(0.5), 1e-9, "posX a meio = 0 (interpola -50→50)");
    }

    [KlipTest(3, "cor/scale por keyframe (Fase 1) continuam a servir a Fase 3")]
    public static void ColorAndNonUniformScaleFromPhase1()
    {
        // scale.x não-uniforme via o registo uniforme
        var l = PropRegistry.AddKeyframe(new Layer("s", MorphTrack.Static(Shapes.Rect(40, 40)), 0xFF6D5EF6), "scale.x", 0, PropValue.Of(1.0));
        l = PropRegistry.AddKeyframe(l, "scale.x", 1, PropValue.Of(3.0));
        Assert.Near(2.0, PropRegistry.GetValue(l, "scale.x", 0.5).Scalar, 1e-6, "scale.x anima 1→3 (meio=2)");
        // cor por keyframe
        var c = PropRegistry.AddKeyframe(l, "color.fill", 1, PropValue.Of(Rgba.Blue.ToArgb()));
        Assert.True((PropRegistry.GetValue(c, "color.fill", 1).Argb & 0xFF) > 200, "color.fill chega a azul");
    }
}
