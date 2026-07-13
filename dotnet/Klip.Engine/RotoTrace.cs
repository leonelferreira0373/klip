using System;
using System.Collections.Generic;
using Klip.Model;
using SkiaSharp;

namespace Klip.Engine;

/// <summary>Resultado do rotoscoping: o "d" do recorte + nº de contornos + tempo do modelo.</summary>
public sealed record RotoResult(string D, int Contours, long Ms);

/// <summary>
/// Rotoscoping: isola o sujeito e traça-o num recorte vetorial editável. A parte PURA (máscara→path)
/// é testável; o isolamento por BgRemover (u2netp ONNX) é MODEL-GATED — o erro PROPAGA-SE (não swallow)
/// para a App devolver "modelo em falta". O resultado liga-se ao track-matte da Fase 7.
/// </summary>
public static class RotoTrace
{
    /// <summary>PURO: PNG (com alpha) → path vetorial (via BitmapTrace).</summary>
    public static string MaskToPath(string pngPath, byte threshold = 128, double simplify = 1.5)
    {
        using var bmp = SKBitmap.Decode(pngPath) ?? throw new InvalidOperationException("máscara ilegível: " + pngPath);
        return BitmapTrace.AlphaToPath(bmp, threshold, simplify, useLuma: false);
    }

    /// <summary>PURO: bitmap em memória → path.</summary>
    public static string MaskToPath(SKBitmap mask, byte threshold = 128, double simplify = 1.5)
        => BitmapTrace.AlphaToPath(mask, threshold, simplify, useLuma: false);

    /// <summary>MODEL-GATED: isola o sujeito (BgRemover ONNX) → alpha → path. Lança se o modelo faltar.</summary>
    public static RotoResult FromImage(string imagePath, byte threshold = 128, double simplify = 1.5)
    {
        var (png, ms) = BgRemover.Remove(imagePath);   // pode lançar (modelo/native/decode) — PROPAGA
        var d = MaskToPath(png, threshold, simplify);
        return new RotoResult(d, BitmapTrace.ContourCount(d), ms);
    }

    /// <summary>Rotoscoping TEMPORAL: uma sequência de máscaras → MorphTrack (contorno animado por frame).
    /// A interpolação entre keys usa o morph real (PathMorph) da Fase 6.</summary>
    public static MorphTrack TraceSequence(IReadOnlyList<string> maskPngs, double fps, byte threshold = 128, double simplify = 1.5)
    {
        double f = Math.Max(1.0, fps);
        var keys = new List<MorphKey>();
        for (int i = 0; i < maskPngs.Count; i++)
        {
            var d = MaskToPath(maskPngs[i], threshold, simplify);
            if (!string.IsNullOrEmpty(d)) keys.Add(new MorphKey(i / f, d, Easing.Linear));
        }
        return new MorphTrack(keys);
    }
}
