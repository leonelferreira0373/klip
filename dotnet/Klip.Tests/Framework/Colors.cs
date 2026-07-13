using System;

namespace Klip.Tests.Framework;

/// <summary>
/// Cor RGBA em canais 0..255 (straight/unpremultiplied) — o que se lê de um pixel.
/// Independente do SkiaSharp para as asserções ficarem legíveis e portáveis entre fases.
/// </summary>
public readonly record struct Rgba(byte R, byte G, byte B, byte A)
{
    /// <summary>Constrói a partir de 0xAARRGGBB (o formato de <c>Layer.FillArgb</c>).</summary>
    public static Rgba FromArgb(uint argb) => new(
        (byte)((argb >> 16) & 0xFF),
        (byte)((argb >> 8) & 0xFF),
        (byte)(argb & 0xFF),
        (byte)((argb >> 24) & 0xFF));

    public uint ToArgb() => ((uint)A << 24) | ((uint)R << 16) | ((uint)G << 8) | B;

    /// <summary>Distância Euclidiana em RGB (ignora alpha) — a métrica de "que cor é este pixel".</summary>
    public double RgbDistance(Rgba o)
    {
        double dr = R - o.R, dg = G - o.G, db = B - o.B;
        return Math.Sqrt(dr * dr + dg * dg + db * db);
    }

    /// <summary>Interpola linearmente em RGB (como o motor de propriedades DEVE fazer p/ cor).</summary>
    public static Rgba LerpRgb(Rgba a, Rgba b, double u)
    {
        u = Math.Clamp(u, 0, 1);
        return new Rgba(
            (byte)Math.Round(a.R + (b.R - a.R) * u),
            (byte)Math.Round(a.G + (b.G - a.G) * u),
            (byte)Math.Round(a.B + (b.B - a.B) * u),
            (byte)Math.Round(a.A + (b.A - a.A) * u));
    }

    public override string ToString() => $"rgba({R},{G},{B},{A})";

    // Cores nomeadas p/ os testes de aceitação
    public static readonly Rgba Red   = new(255, 0, 0, 255);
    public static readonly Rgba Blue  = new(0, 0, 255, 255);
    public static readonly Rgba Purple = new(128, 0, 128, 255);
    public static readonly Rgba White = new(255, 255, 255, 255);
    public static readonly Rgba Black = new(0, 0, 0, 255);
}
