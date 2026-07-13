using System;
using Klip.Engine;
using Klip.Model;
using SkiaSharp;

namespace Klip.Tests.Framework;

/// <summary>
/// NÚCLEO REUTILIZÁVEL do harness E2E — exercita o motor DIRETAMENTE:
/// renderiza um <see cref="Comp"/> no tempo t via <see cref="Renderer"/> e devolve
/// os PIXELS reais (RGBA straight) para asserções objetivas. Sem GUI, sem GPU.
///
/// Todas as fases reutilizam isto: sample de pixel, média de região, centróide do
/// conteúdo (não-fundo), cor dominante, e dump PNG opcional para inspeção humana.
/// </summary>
public sealed class RenderHarness
{
    private readonly Renderer _renderer = new();

    /// <summary>Um frame renderizado, já em RGBA8888 UNPREMUL (GetPixel devolve cor direta).</summary>
    public sealed class Frame : IDisposable
    {
        private readonly SKBitmap _bmp;
        public int Width => _bmp.Width;
        public int Height => _bmp.Height;

        internal Frame(SKBitmap bmp) => _bmp = bmp;

        /// <summary>Lê o pixel (x,y) como <see cref="Rgba"/> straight (não pré-multiplicado).</summary>
        public Rgba At(int x, int y)
        {
            x = Math.Clamp(x, 0, _bmp.Width - 1);
            y = Math.Clamp(y, 0, _bmp.Height - 1);
            var c = _bmp.GetPixel(x, y);
            return new Rgba(c.Red, c.Green, c.Blue, c.Alpha);
        }

        /// <summary>Cor no centro do canvas — onde uma camada com transform-identidade é desenhada.</summary>
        public Rgba Center() => At(_bmp.Width / 2, _bmp.Height / 2);

        /// <summary>Média RGBA de um quadrado (2*half+1)² centrado em (cx,cy) — robusto a AA.</summary>
        public Rgba AverageAround(int cx, int cy, int half = 3)
        {
            long r = 0, g = 0, b = 0, a = 0, n = 0;
            for (int y = cy - half; y <= cy + half; y++)
            for (int x = cx - half; x <= cx + half; x++)
            {
                var p = At(x, y);
                r += p.R; g += p.G; b += p.B; a += p.A; n++;
            }
            if (n == 0) return default;
            return new Rgba((byte)(r / n), (byte)(g / n), (byte)(b / n), (byte)(a / n));
        }

        /// <summary>
        /// Centróide X (em px) dos pixels cuja cor dista de <paramref name="background"/>
        /// mais que <paramref name="threshold"/>. Usado para PROVAR movimento (PosX/kf).
        /// Devolve NaN se não houver conteúdo.
        /// </summary>
        public double ContentCentroidX(Rgba background, double threshold = 40)
        {
            double sumX = 0; long n = 0;
            for (int y = 0; y < _bmp.Height; y++)
            for (int x = 0; x < _bmp.Width; x++)
                if (At(x, y).RgbDistance(background) > threshold) { sumX += x; n++; }
            return n == 0 ? double.NaN : sumX / n;
        }

        /// <summary>Conta pixels de conteúdo (distância ao fundo &gt; threshold) — área/cobertura.</summary>
        public long ContentPixelCount(Rgba background, double threshold = 40)
        {
            long n = 0;
            for (int y = 0; y < _bmp.Height; y++)
            for (int x = 0; x < _bmp.Width; x++)
                if (At(x, y).RgbDistance(background) > threshold) n++;
            return n;
        }

        /// <summary>Grava um PNG (debug humano). Não usado nas asserções.</summary>
        public void SavePng(string path)
        {
            using var img = SKImage.FromBitmap(_bmp);
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            using var fs = System.IO.File.OpenWrite(path);
            data.SaveTo(fs);
        }

        public void Dispose() => _bmp.Dispose();
    }

    /// <summary>Renderiza o comp em t e devolve o frame legível. O chamador faz Dispose().</summary>
    public Frame Render(Comp comp, double t)
    {
        using var img = _renderer.RenderFrame(comp, t);
        // Copia para um bitmap UNPREMUL nosso → GetPixel devolve cor STRAIGHT determinística.
        var info = new SKImageInfo(img.Width, img.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var bmp = new SKBitmap(info);
        if (!img.ReadPixels(info, bmp.GetPixels(), info.RowBytes, 0, 0))
        {
            bmp.Dispose();
            throw new InvalidOperationException("ReadPixels falhou — surface Skia inacessível.");
        }
        return new Frame(bmp);
    }

    /// <summary>Amostra a cor central do comp em t (atalho comum).</summary>
    public Rgba SampleCenter(Comp comp, double t)
    {
        using var f = Render(comp, t);
        return f.Center();
    }
}
