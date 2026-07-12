using System;
using System.IO;
using System.Linq;
using ImageMagick;
using Klip.Model;

namespace Klip.Engine;

/// <summary>
/// CMYK print-ready do KLIP .NET — conversão via perfil ICC REAL do sistema (Windows traz
/// USWebCoatedSWOP/FOGRA em System32\spool\drivers\color), como a app legacy. Sem perfil →
/// conversão CMYK embutida do ImageMagick. + conversões rápidas CMYK↔RGB para input de cor.
/// </summary>
public static class CmykExport
{
    private static readonly string[] Pref =
        { "uswebcoatedswop", "coatedfogra39", "coatedfogra27", "japancolor2001coated", "euroscalecoated" };

    public static string? FindProfile()
    {
        var env = Environment.GetEnvironmentVariable("KLIP_CMYK_PROFILE");
        if (env is not null && File.Exists(env)) return env;
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                               "System32", "spool", "drivers", "color");
        if (!Directory.Exists(dir)) return null;
        var files = Directory.EnumerateFiles(dir)
            .Where(f => f.EndsWith(".icc", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".icm", StringComparison.OrdinalIgnoreCase)).ToList();
        string Norm(string f) => Path.GetFileName(f).ToLowerInvariant().Replace(" ", "").Replace("-", "").Replace("_", "");
        foreach (var p in Pref)
        {
            var hit = files.FirstOrDefault(f => Norm(f).Contains(p));
            if (hit is not null) return hit;
        }
        return files.FirstOrDefault(f =>
            new[] { "cmyk", "coated", "swop", "fogra" }.Any(k => Norm(f).Contains(k)));
    }

    /// <summary>Renderiza o comp em t e grava TIFF CMYK (ICC real se existir). Devolve o perfil usado.</summary>
    public static string? ExportTiff(Comp comp, double t, string outPath)
    {
        byte[] png = EngineExport.RenderPng(comp, t);
        using var img = new MagickImage(png);
        string? icc = FindProfile();
        if (icc is not null)
        {
            img.SetProfile(ColorProfile.SRGB);
            img.SetProfile(new ColorProfile(File.ReadAllBytes(icc)));   // transforma sRGB → CMYK ICC
        }
        else
        {
            img.ColorSpace = ColorSpace.CMYK;
        }
        img.Write(outPath, MagickFormat.Tiff);
        return icc;
    }

    // conversões rápidas (uncalibrated) para desenhar em CMYK no ecrã
    public static uint CmykToArgb(double c, double m, double y, double k)
    {
        c /= 100; m /= 100; y /= 100; k /= 100;
        byte r = (byte)Math.Round(255 * (1 - c) * (1 - k));
        byte g = (byte)Math.Round(255 * (1 - m) * (1 - k));
        byte b = (byte)Math.Round(255 * (1 - y) * (1 - k));
        return 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;
    }
}
