using System;
using Klip.Model;
using SkiaSharp;

namespace Klip.Engine;

/// <summary>
/// Constrói o SKShader de um <see cref="GradientSpec"/> multi-stop.
///
/// Convenções herdadas do par legado (FillArgb2/GradAngle/FillRadial) para que trocar uma camada
/// antiga por um GradientSpec não vire a imagem ao contrário:
///   · linear → 90° = topo→fundo, com o vector (cos θ, sin θ) num eixo y que cresce para BAIXO;
///   · radial → raio = 0.62 × max(w,h) por omissão, o mesmo factor que o ramo antigo tinha à mão.
/// Devolver null faz o Renderer cair no caminho de sempre.
/// </summary>
public static class GradientShader
{
    /// <param name="b">caixa da forma em coordenadas de desenho</param>
    /// <param name="t">tempo, para avaliar os tracks animáveis</param>
    /// <param name="alpha">alpha da camada, já resolvido</param>
    public static SKShader? Build(GradientSpec g, SKRect b, double t, byte alpha)
    {
        if (g is null || g.Stops is null || g.Stops.Count == 0) return null;

        int n = Math.Min(g.Stops.Count, GradientSpec.MaxStops);
        var pos = new float[n];
        var col = new SKColor[n];
        for (int i = 0; i < n; i++)
        {
            var s = g.Stops[i];
            pos[i] = (float)Math.Clamp(s.EvalPos(t), 0.0, 1.0);
            uint argb = s.EvalArgb(t);
            // O alpha da camada é MULTIPLICADOR, não substituto: uma paragem já translúcida
            // (fade para transparente) tem de continuar mais fraca que as vizinhas.
            byte sa = (byte)(((argb >> 24) & 0xFF) * (alpha / 255.0));
            col[i] = ((SKColor)argb).WithAlpha(sa);
        }

        // Ordenar pela posição AVALIADA, não pela estática: dois offsets animados podem cruzar-se a
        // meio da animação, e o Skia com posições fora de ordem não falha — devolve lixo.
        Array.Sort(pos, col);
        for (int i = 1; i < n; i++)
            if (pos[i] < pos[i - 1]) pos[i] = pos[i - 1];

        // O Skia exige >= 2 paragens. Uma só é uma cor chapada: duplicamos em vez de devolver null,
        // senão apagar stops até sobrar um fazia a camada saltar de volta para a cor legada.
        if (n == 1)
        {
            pos = new[] { 0f, 1f };
            col = new[] { col[0], col[0] };
        }

        var tile = g.Tile switch
        {
            1 => SKShaderTileMode.Repeat,
            2 => SKShaderTileMode.Mirror,
            _ => SKShaderTileMode.Clamp,
        };

        float w = b.Width, h = b.Height;

        switch (g.Kind)
        {
            case GradKind.Radial:
            {
                float r = (float)(g.EvalRadius(t) * Math.Max(w, h));
                if (!(r > 0f)) return null;   // caixa degenerada (ou NaN): sem raio não há gradiente
                var c = new SKPoint(b.Left + (float)g.EvalCenterX(t) * w,
                                    b.Top + (float)g.EvalCenterY(t) * h);
                return SKShader.CreateRadialGradient(c, r, col, pos, tile);
            }

            case GradKind.Conic:
            {
                var c = new SKPoint(b.Left + (float)g.EvalCenterX(t) * w,
                                    b.Top + (float)g.EvalCenterY(t) * h);
                // O sweep do Skia arranca sempre às 3 horas. Para o ângulo querer dizer o mesmo que
                // no linear, rodamos o shader à volta do centro em vez de usar start/end angle —
                // assim as posições 0..1 continuam a cobrir a volta INTEIRA, que é o que um cónico
                // tem de fazer (com start/end o Tile passaria a mandar no resto do círculo).
                var m = SKMatrix.CreateRotationDegrees((float)g.EvalAngle(t), c.X, c.Y);
                return SKShader.CreateSweepGradient(c, col, pos, tile, 0f, 360f, m);
            }

            default:
            {
                double rad = g.EvalAngle(t) * Math.PI / 180.0;
                float dx = (float)Math.Cos(rad), dy = (float)Math.Sin(rad);
                // Meia-caixa projectada no eixo do gradiente: cobre de ponta a ponta seja qual for o
                // ângulo. Fórmula copiada à letra do ramo legado — mudar aqui desalinha ficheiros antigos.
                float span = (MathF.Abs(w * dx) + MathF.Abs(h * dy)) / 2f;
                if (!(span > 0f)) return null;
                var ctr = new SKPoint(b.MidX, b.MidY);
                return SKShader.CreateLinearGradient(
                    new SKPoint(ctr.X - dx * span, ctr.Y - dy * span),
                    new SKPoint(ctr.X + dx * span, ctr.Y + dy * span),
                    col, pos, tile);
            }
        }
    }
}
