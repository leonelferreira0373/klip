using System;

namespace Klip.Engine;

/// <summary>
/// Conversões de cor com rigor (sRGB ↔ linear ↔ XYZ ↔ Lab, ΔE2000, mistura em OkLab).
/// STUB — corpo implementado a seguir.
///
/// NÃO substitui <c>ColorMath.Lerp</c>: essa tem teste de aceitação a fixar o comportamento.
/// </summary>
public static class ColorScience
{
    /// <summary>sRGB empacotado → L*a*b* (D65).</summary>
    public static (double L, double a, double b) SrgbToLab(uint argb) => (0, 0, 0);

    /// <summary>L*a*b* (D65) → sRGB empacotado (alpha 0xFF).</summary>
    public static uint LabToSrgb(double L, double a, double b) => 0xFF000000u;

    /// <summary>Diferença de cor CIEDE2000 — a métrica que corresponde ao que o olho vê.</summary>
    public static double DeltaE2000((double L, double a, double b) x, (double L, double a, double b) y) => 0;

    /// <summary>Mistura perceptualmente uniforme (OkLab) — rampas sem a "lama" do sRGB.</summary>
    public static uint OkLabLerp(uint a, uint b, double e) => a;
}
