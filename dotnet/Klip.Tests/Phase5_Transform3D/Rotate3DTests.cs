using Klip.Engine;
using Klip.Model;
using Klip.Tests.Framework;

namespace Klip.Tests.Phase5_Transform3D;

/// <summary>Fase 5 — rotação 3D de camada plana (tilt X/Y com perspetiva via matriz 3x3 à mão em Renderer.Perspective3D).
/// A câmara animável já existia (CameraRig). Aqui provamos a foreshortening real e a keyframabilidade pelo sistema uniforme.</summary>
public static class Rotate3DTests
{
    private static readonly RenderHarness H = new();

    [KlipTest(5, "rotação 3D: tilt em Y foreshortena a camada plana (perspetiva real)")]
    public static void Rotate3DForeshortens()
    {
        var rect = new Layer("r", MorphTrack.Static(Shapes.Rect(60, 20)), 0xFF101010);   // 120x40
        var flat = new Comp(240, 160, 30, 1.0, Rgba.White.ToArgb(), new[] { rect });
        var tilted = new Comp(240, 160, 30, 1.0, Rgba.White.ToArgb(), new[] { rect with { RotationY = Track.Const(70) } });

        long flatPx, tiltPx;
        using (var f = H.Render(flat, 0)) flatPx = f.ContentPixelCount(Rgba.White);
        using (var f = H.Render(tilted, 0)) tiltPx = f.ContentPixelCount(Rgba.White);

        Assert.Greater(flatPx, 500, "camada plana desenha conteúdo");
        Assert.Greater(tiltPx, 0, "camada inclinada ainda desenha");
        Assert.Less(tiltPx, flatPx * 0.75, $"tilt 70° em Y foreshortena a largura (plano={flatPx}, inclinado={tiltPx})");
    }

    [KlipTest(5, "rotação 3D: rotation.y é keyframável pelo sistema uniforme")]
    public static void Rotate3DKeyframable()
    {
        var l = PropRegistry.AddKeyframe(new Layer("r", MorphTrack.Static(Shapes.Rect(30, 30)), 0xFF6D5EF6), "rotation.y", 0, PropValue.Of(0.0));
        l = PropRegistry.AddKeyframe(l, "rotation.y", 1, PropValue.Of(90.0));
        Assert.Near(45, PropRegistry.GetValue(l, "rotation.y", 0.5).Scalar, 1e-6, "rotation.y anima 0→90 (meio=45)");
        Assert.True(l.RotationY != null, "RotationY track criada via path uniforme");
        Assert.True(PropRegistry.Canonical("rotate_y") == "rotation.y", "alias rotate_y resolve");
    }
}
