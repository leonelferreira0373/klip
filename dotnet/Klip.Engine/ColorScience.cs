using System;

namespace Klip.Engine;

/// <summary>
/// Conversões de cor com rigor (sRGB ↔ linear ↔ XYZ ↔ Lab, ΔE2000, mistura em OkLab).
///
/// NÃO substitui <c>ColorMath.Lerp</c>: essa tem teste de aceitação a fixar o comportamento
/// (sRGB-linear premultiplicado). Aqui vive a parte "ciência da cor" — a que responde a
/// "estas duas cores são iguais aos olhos de um impressor?" e "que PANTONE é este pixel?".
///
/// Toda esta classe trabalha em D65, que é o branco do sRGB/ecrã. Os livros PANTONE do Corel
/// vêm em D50 (branco da indústria gráfica): quem comparar contra eles tem de manter os DOIS
/// lados no mesmo iluminante, senão o ΔE mente por 2-3 unidades sem dar erro nenhum.
/// </summary>
public static class ColorScience
{
    // Ponto branco D65 com Y=1 — os mesmos números que a matriz sRGB→XYZ abaixo assume.
    private const double Xn = 0.95047, Yn = 1.00000, Zn = 1.08883;

    // Limiar do troço linear do Lab: (6/29)^3. Abaixo disto a raiz cúbica tem derivada infinita
    // e os pretos ficariam ruidosos — daí o troço recto.
    private const double LabEps = 216.0 / 24389.0;   // ≈ 0.008856
    private const double LabKap = 24389.0 / 27.0;    // ≈ 903.3

    /// <summary>sRGB empacotado → L*a*b* (D65).</summary>
    public static (double L, double a, double b) SrgbToLab(uint argb)
    {
        double r = S2L(((argb >> 16) & 0xFF) / 255.0);
        double g = S2L(((argb >> 8) & 0xFF) / 255.0);
        double b = S2L((argb & 0xFF) / 255.0);

        // sRGB primaries → XYZ (D65). Estes coeficientes são os da norma IEC 61966-2-1.
        double X = 0.4124564 * r + 0.3575761 * g + 0.1804375 * b;
        double Y = 0.2126729 * r + 0.7151522 * g + 0.0721750 * b;
        double Z = 0.0193339 * r + 0.1191920 * g + 0.9503041 * b;

        double fx = LabF(X / Xn), fy = LabF(Y / Yn), fz = LabF(Z / Zn);
        return (116.0 * fy - 16.0, 500.0 * (fx - fy), 200.0 * (fy - fz));
    }

    /// <summary>L*a*b* (D65) → sRGB empacotado (alpha 0xFF).</summary>
    public static uint LabToSrgb(double L, double a, double b)
    {
        double fy = (L + 16.0) / 116.0;
        double fx = fy + a / 500.0;
        double fz = fy - b / 200.0;

        double X = Xn * LabFInv(fx);
        // Y tem um caso próprio: perto do preto usa-se L directamente, não f(y)³ — é o que
        // fecha o ciclo Lab→sRGB→Lab sem derrapar nos tons escuros.
        double Y = Yn * (L > LabKap * LabEps ? Cube(fy) : L / LabKap);
        double Z = Zn * LabFInv(fz);

        double r = 3.2404542 * X - 1.5371385 * Y - 0.4985314 * Z;
        double g = -0.9692660 * X + 1.8760108 * Y + 0.0415560 * Z;
        double bl = 0.0556434 * X - 0.2040259 * Y + 1.0572252 * Z;

        return 0xFF000000u
             | ((uint)Byte8(L2S(r)) << 16)
             | ((uint)Byte8(L2S(g)) << 8)
             | (uint)Byte8(L2S(bl));
    }

    /// <summary>
    /// Diferença de cor CIEDE2000 — a métrica que corresponde ao que o olho vê.
    ///
    /// Implementação segundo Sharma, Wu &amp; Dalal (2005). Os três sítios onde quase toda a
    /// gente erra estão marcados em baixo: (1) o ângulo médio quando um dos cromas é zero,
    /// (2) o embrulho do Δh a ±180°, (3) graus vs radianos nos cossenos do T.
    /// Ambos os lados TÊM de vir do mesmo iluminante; esta função não tem como saber.
    /// </summary>
    public static double DeltaE2000((double L, double a, double b) x, (double L, double a, double b) y)
    {
        const double kL = 1.0, kC = 1.0, kH = 1.0;

        double L1 = x.L, a1 = x.a, b1 = x.b;
        double L2 = y.L, a2 = y.a, b2 = y.b;

        double C1 = Math.Sqrt(a1 * a1 + b1 * b1);
        double C2 = Math.Sqrt(a2 * a2 + b2 * b2);
        double Cbar = (C1 + C2) * 0.5;

        // G estica o eixo a* nos cinzentos, para o ΔE não subestimar diferenças perto do neutro.
        double Cbar7 = Pow7(Cbar);
        double G = 0.5 * (1.0 - Math.Sqrt(Cbar7 / (Cbar7 + Pow7(25.0))));

        double a1p = (1.0 + G) * a1;
        double a2p = (1.0 + G) * a2;
        double C1p = Math.Sqrt(a1p * a1p + b1 * b1);
        double C2p = Math.Sqrt(a2p * a2p + b2 * b2);

        double h1p = HueDeg(a1p, b1);
        double h2p = HueDeg(a2p, b2);

        double dLp = L2 - L1;
        double dCp = C2p - C1p;

        // (1)+(2) Δh: se um dos cromas é zero o matiz não existe — a diferença de matiz é 0,
        // não o lixo que o atan2 devolveu. E o embrulho é a ±180°, não a ±360°.
        double dhp;
        if (C1p * C2p == 0.0) dhp = 0.0;
        else
        {
            dhp = h2p - h1p;
            if (dhp > 180.0) dhp -= 360.0;
            else if (dhp < -180.0) dhp += 360.0;
        }
        double dHp = 2.0 * Math.Sqrt(C1p * C2p) * Math.Sin(Rad(dhp) * 0.5);

        double Lbarp = (L1 + L2) * 0.5;
        double Cbarp = (C1p + C2p) * 0.5;

        // (1) matiz médio: com croma zero soma-se (h1+h2) SEM dividir — o outro é 0 e a média
        // partiria o valor ao meio. É a correcção explícita do artigo do Sharma.
        double hbarp;
        if (C1p * C2p == 0.0) hbarp = h1p + h2p;
        else if (Math.Abs(h1p - h2p) <= 180.0) hbarp = (h1p + h2p) * 0.5;
        else if (h1p + h2p < 360.0) hbarp = (h1p + h2p + 360.0) * 0.5;
        else hbarp = (h1p + h2p - 360.0) * 0.5;

        // (3) T em GRAUS por dentro do cos — os deslocamentos (30, 6, 63) são graus.
        double T = 1.0
                 - 0.17 * Math.Cos(Rad(hbarp - 30.0))
                 + 0.24 * Math.Cos(Rad(2.0 * hbarp))
                 + 0.32 * Math.Cos(Rad(3.0 * hbarp + 6.0))
                 - 0.20 * Math.Cos(Rad(4.0 * hbarp - 63.0));

        double SL = 1.0 + (0.015 * (Lbarp - 50.0) * (Lbarp - 50.0))
                        / Math.Sqrt(20.0 + (Lbarp - 50.0) * (Lbarp - 50.0));
        double SC = 1.0 + 0.045 * Cbarp;
        double SH = 1.0 + 0.015 * Cbarp * T;

        // Rotação: só morde nos azuis muito saturados (perto de 275°), onde croma e matiz
        // deixam de ser independentes. Fora dessa janela, exp() apaga-a.
        double dTheta = 30.0 * Math.Exp(-Sq((hbarp - 275.0) / 25.0));
        double Cbarp7 = Pow7(Cbarp);
        double RC = 2.0 * Math.Sqrt(Cbarp7 / (Cbarp7 + Pow7(25.0)));
        double RT = -Math.Sin(Rad(2.0 * dTheta)) * RC;

        double tl = dLp / (kL * SL);
        double tc = dCp / (kC * SC);
        double th = dHp / (kH * SH);

        // O termo cruzado pode ser negativo; o radicando não pode. Nas cores reais nunca desce
        // abaixo de zero, mas um Max(0) evita um NaN silencioso a envenenar o Nearest().
        double v = tl * tl + tc * tc + th * th + RT * tc * th;
        return Math.Sqrt(Math.Max(0.0, v));
    }

    /// <summary>Atalho: ΔE2000 entre duas cores de ecrã (converte ambas em D65).</summary>
    public static double DeltaE2000(uint argb1, uint argb2)
        => DeltaE2000(SrgbToLab(argb1), SrgbToLab(argb2));

    /// <summary>
    /// Mistura perceptualmente uniforme (OkLab) — rampas sem a "lama" do sRGB.
    ///
    /// Diferente do <c>ColorMath.Lerp</c> de propósito: aquele é linear-premultiplicado (correcto
    /// para compor pixels), este é perceptual (correcto para desenhar um degradê que ao olho
    /// avança a passo constante). Azul→amarelo aqui não passa por cinzento.
    /// </summary>
    public static uint OkLabLerp(uint a, uint b, double e)
    {
        e = Math.Clamp(e, 0.0, 1.0);

        double aA = ((a >> 24) & 0xFF) / 255.0, aB = ((b >> 24) & 0xFF) / 255.0;
        var (l1, m1, s1) = SrgbToOkLab(a);
        var (l2, m2, s2) = SrgbToOkLab(b);

        double L = l1 + (l2 - l1) * e;
        double A = m1 + (m2 - m1) * e;
        double B = s1 + (s2 - s1) * e;
        double al = aA + (aB - aA) * e;   // o alpha não tem "perceptual" — interpola direito

        uint rgb = OkLabToSrgb(L, A, B);
        return ((uint)Byte8(al) << 24) | (rgb & 0x00FFFFFFu);
    }

    /// <summary>sRGB empacotado → OkLab (Björn Ottosson, 2020).</summary>
    public static (double L, double a, double b) SrgbToOkLab(uint argb)
    {
        double r = S2L(((argb >> 16) & 0xFF) / 255.0);
        double g = S2L(((argb >> 8) & 0xFF) / 255.0);
        double bb = S2L((argb & 0xFF) / 255.0);

        double l = 0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * bb;
        double m = 0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * bb;
        double s = 0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * bb;

        // Cbrt (e não Pow(1/3)) porque estes valores podem sair ligeiramente negativos por
        // arredondamento e Pow rebentava com NaN.
        double l_ = Math.Cbrt(l), m_ = Math.Cbrt(m), s_ = Math.Cbrt(s);

        return (0.2104542553 * l_ + 0.7936177850 * m_ - 0.0040720468 * s_,
                1.9779984951 * l_ - 2.4285922050 * m_ + 0.4505937099 * s_,
                0.0259040371 * l_ + 0.7827717662 * m_ - 0.8086757660 * s_);
    }

    /// <summary>OkLab → sRGB empacotado (alpha 0xFF).</summary>
    public static uint OkLabToSrgb(double L, double a, double b)
    {
        double l_ = L + 0.3963377774 * a + 0.2158037573 * b;
        double m_ = L - 0.1055613458 * a - 0.0638541728 * b;
        double s_ = L - 0.0894841775 * a - 1.2914855480 * b;

        double l = l_ * l_ * l_, m = m_ * m_ * m_, s = s_ * s_ * s_;

        double r = +4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s;
        double g = -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s;
        double bl = -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s;

        return 0xFF000000u
             | ((uint)Byte8(L2S(r)) << 16)
             | ((uint)Byte8(L2S(g)) << 8)
             | (uint)Byte8(L2S(bl));
    }

    // ── auxiliares ────────────────────────────────────────────────────────────────────────

    /// <summary>sRGB → linear. O troço recto abaixo de 0.04045 não é decoração: um Pow(2.2)
    /// cego escurece os tons baixos e faz o ΔE dos quase-pretos disparar.</summary>
    private static double S2L(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    /// <summary>linear → sRGB, com o mesmo troço recto do lado de lá.</summary>
    private static double L2S(double c)
    {
        c = Math.Clamp(c, 0.0, 1.0);   // gamut: Lab cobre cores que o ecrã não faz
        return c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1.0 / 2.4) - 0.055;
    }

    private static double LabF(double t) => t > LabEps ? Math.Cbrt(t) : (LabKap * t + 16.0) / 116.0;

    private static double LabFInv(double f)
    {
        double f3 = Cube(f);
        return f3 > LabEps ? f3 : (116.0 * f - 16.0) / LabKap;
    }

    /// <summary>Matiz em graus [0,360). Com a=b=0 o atan2 devolve 0 e o ângulo não tem
    /// significado — quem chama já trata esse caso pelo croma.</summary>
    private static double HueDeg(double a, double b)
    {
        if (a == 0.0 && b == 0.0) return 0.0;
        double h = Math.Atan2(b, a) * 180.0 / Math.PI;
        return h < 0.0 ? h + 360.0 : h;
    }

    private static double Rad(double deg) => deg * Math.PI / 180.0;
    private static double Sq(double v) => v * v;
    private static double Cube(double v) => v * v * v;

    /// <summary>25^7 e Cbar^7 aparecem em toda a fórmula; sete multiplicações batem Pow em
    /// exactidão (Pow(x,7) arredonda de forma diferente e desalinha do artigo à 4ª casa).</summary>
    private static double Pow7(double v)
    {
        double v2 = v * v, v4 = v2 * v2;
        return v4 * v2 * v;
    }

    private static byte Byte8(double v) => (byte)Math.Clamp(Math.Round(v * 255.0), 0, 255);
}
