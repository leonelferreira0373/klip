using System;
using Silk.NET.OpenGL;

namespace Klip.Engine.ThreeD;

/// <summary>
/// Ambiente de estúdio PROCEDURAL (equiretangular HDR) para IBL — sem assets externos.
/// Fundo escuro + softboxes brilhantes (key/fill/rim/top) → reflexos de estúdio no metal.
/// Textura RGBA16F com mipmaps (mip alto = reflexo desfocado p/ roughness alta; irradiância difusa).
/// </summary>
public static class IblEnv
{
    /// <summary>
    /// 1024×512 e não 512×256: o reflexo num metal polido é uma cópia do ambiente, e a resolução do
    /// ambiente É a nitidez do reflexo. Custa uns milissegundos uma vez, ganha-se em todos os frames.
    /// </summary>
    public static unsafe (uint tex, float maxLod) Create(GL gl, int w = 1024, int h = 512)
    {
        var px = new float[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            float v = (y + 0.5f) / h;                 // 0 = baixo, 1 = topo
            for (int x = 0; x < w; x++)
            {
                float u = (x + 0.5f) / w;             // 0..1 azimute (envolve)

                // CHÃO CLARO. Antes o ambiente era quase todo preto e o metal, que não tem cor
                // própria e só devolve o que o rodeia, saía cinzento morto. Num estúdio a sério a
                // mesa devolve luz por baixo — é isso que dá "barriga" às peças e as tira do vazio.
                float chao = 0.14f * Smooth(1f - Math.Clamp(v / 0.42f, 0f, 1f));
                float ceu = Lerp(0.05f, 0.30f, Smooth(v));
                float horizonte = 0.16f * MathF.Exp(-((v - 0.52f) * (v - 0.52f)) / 0.010f);
                float baseL = ceu + chao + horizonte;
                float r = baseL * 0.98f, g = baseL * 0.99f, b = baseL * 1.05f;

                // softboxes: (centro u,v) (meia-largura u,v) intensidade (tint)
                // A KEY é LARGA de propósito: uma fonte grande dá um brilho comprido e macio — é a
                // assinatura da fotografia de produto. Uma fonte pequena dá um pontinho de plástico.
                AddBox(ref r, ref g, ref b, u, v, 0.14f, 0.70f, 0.20f, 0.18f, 9.0f, 1f, 0.985f, 0.95f);   // key larga
                AddBox(ref r, ref g, ref b, u, v, 0.60f, 0.58f, 0.17f, 0.13f, 3.0f, 0.95f, 0.975f, 1f);   // fill
                AddBox(ref r, ref g, ref b, u, v, 0.88f, 0.54f, 0.05f, 0.20f, 6.5f, 1f, 0.99f, 0.96f);    // rim estreita e forte
                // banco de luz no tecto a toda a volta: dá ao metal uma faixa contínua para reflectir
                AddBox(ref r, ref g, ref b, u, v, 0.50f, 0.97f, 1.00f, 0.10f, 2.2f, 1f, 1f, 1f);
                AddBox(ref r, ref g, ref b, u, v, 0.75f, 0.64f, 0.45f, 0.30f, 1.5f, 0.98f, 0.99f, 1f);    // fill do lado da câmara
                // devolução do chão, quente e ampla — a luz que sobe da mesa
                AddBox(ref r, ref g, ref b, u, v, 0.30f, 0.10f, 0.60f, 0.16f, 0.9f, 1f, 0.96f, 0.90f);

                int i = (y * w + x) * 4;
                px[i] = r; px[i + 1] = g; px[i + 2] = b; px[i + 3] = 1f;
            }
        }
        uint tex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, tex);
        fixed (float* p = px)
            gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba16f, (uint)w, (uint)h, 0,
                          PixelFormat.Rgba, PixelType.Float, p);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.Repeat);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.LinearMipmapLinear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        gl.GenerateMipmap(TextureTarget.Texture2D);
        float maxLod = MathF.Floor(MathF.Log2(Math.Max(w, h)));
        return (tex, maxLod);
    }

    private static float Smooth(float t) { t = Math.Clamp(t, 0f, 1f); return t * t * (3f - 2f * t); }
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static void AddBox(ref float r, ref float g, ref float b, float u, float v,
                               float cu, float cv, float su, float sv, float I,
                               float cr, float cg, float cb)
    {
        float du = Math.Abs(u - cu); du = Math.Min(du, 1f - du);   // azimute envolve
        float dv = Math.Abs(v - cv);
        float fx = Smooth(1f - Math.Clamp(du / su, 0f, 1f));
        float fy = Smooth(1f - Math.Clamp(dv / sv, 0f, 1f));
        float m = fx * fy * I;
        r += cr * m; g += cg * m; b += cb * m;
    }
}
