using System;

namespace Klip.Model;

/// <summary>
/// Interpolação de cor ARGB em espaço sRGB-LINEAR PREMULTIPLICADO — evita o "cinza-lama"
/// do lerp sRGB ingénuo e o halo do lerp não-premultiplicado. É a matemática de cor correcta
/// para motion graphics. red→blue a meio ≈ (188,0,188).
/// </summary>
public static class ColorMath
{
    public static uint Lerp(uint a, uint b, double e)
    {
        e = Math.Clamp(e, 0.0, 1.0);
        var (aa, ar, ag, ab) = Unpack(a);
        var (ba, br, bg, bb) = Unpack(b);
        // sRGB → linear
        double alr = S2L(ar), alg = S2L(ag), alb = S2L(ab);
        double blr = S2L(br), blg = S2L(bg), blb = S2L(bb);
        // premultiplica pelo alpha
        double par = alr * aa, pag = alg * aa, pab = alb * aa;
        double pbr = blr * ba, pbg = blg * ba, pbb = blb * ba;
        // lerp linear premult
        double oa = aa + (ba - aa) * e;
        double pr = par + (pbr - par) * e, pg = pag + (pbg - pag) * e, pb = pab + (pbb - pab) * e;
        // unpremult
        double ur = oa > 1e-6 ? pr / oa : 0, ug = oa > 1e-6 ? pg / oa : 0, ub = oa > 1e-6 ? pb / oa : 0;
        // linear → sRGB
        return Pack(oa, L2S(ur), L2S(ug), L2S(ub));
    }

    private static (double a, double r, double g, double b) Unpack(uint c)
        => (((c >> 24) & 0xFF) / 255.0, ((c >> 16) & 0xFF) / 255.0, ((c >> 8) & 0xFF) / 255.0, (c & 0xFF) / 255.0);

    private static uint Pack(double a, double r, double g, double b)
    {
        static byte B(double x) => (byte)Math.Clamp(Math.Round(x * 255.0), 0, 255);
        return ((uint)B(a) << 24) | ((uint)B(r) << 16) | ((uint)B(g) << 8) | B(b);
    }
    private static double S2L(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    private static double L2S(double c) => c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1.0 / 2.4) - 0.055;
}
