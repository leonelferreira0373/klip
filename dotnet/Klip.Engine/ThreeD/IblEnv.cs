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
    public static unsafe (uint tex, float maxLod) Create(GL gl, int w = 512, int h = 256)
    {
        var px = new float[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            float v = (y + 0.5f) / h;                 // 0 = baixo, 1 = topo
            for (int x = 0; x < w; x++)
            {
                float u = (x + 0.5f) / w;             // 0..1 azimute (envolve)
                // base: gradiente vertical suave (chão escuro → horizonte → topo) + brilho de horizonte
                float baseL = Lerp(0.015f, 0.09f, Smooth(v))
                            + 0.10f * MathF.Exp(-((v - 0.62f) * (v - 0.62f)) / 0.02f);
                float r = baseL * 0.95f, g = baseL * 0.97f, b = baseL * 1.06f;
                // softboxes: (centro u,v) (meia-largura u,v) intensidade (tint)
                AddBox(ref r, ref g, ref b, u, v, 0.14f, 0.72f, 0.10f, 0.10f, 7.5f, 1f, 0.98f, 0.93f);  // key
                AddBox(ref r, ref g, ref b, u, v, 0.60f, 0.60f, 0.13f, 0.09f, 2.4f, 0.95f, 0.97f, 1f);  // fill
                AddBox(ref r, ref g, ref b, u, v, 0.86f, 0.56f, 0.06f, 0.14f, 4.2f, 1f, 0.99f, 0.96f);  // rim
                AddBox(ref r, ref g, ref b, u, v, 0.50f, 0.94f, 0.55f, 0.06f, 1.5f, 1f, 1f, 1f);        // top
                // fill amplo do lado da câmara (~u 0.75) → sheen suave nas faces frontais planas
                AddBox(ref r, ref g, ref b, u, v, 0.75f, 0.66f, 0.40f, 0.26f, 1.15f, 0.98f, 0.99f, 1f);
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
