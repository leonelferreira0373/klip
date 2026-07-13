using SkiaSharp;

namespace Klip.Engine;

/// <summary>Text → vector outlines (path "d", centered at 0,0) — the base for wordmarks and
/// later for custom-font/glyph editing (outlines are editable paths).</summary>
public static class TextShape
{
    /// <summary>Por NOME de família: resolvido pelo FontRegistry (sistema, disco, ou carregada/baixada pela IA).</summary>
    public static string? TextPathD(string text, float size, string family = "Segoe UI", bool bold = true)
        => TextPathD(text, size, FontRegistry.Shared.Resolve(family, bold));

    /// <summary>Por SKTypeface direto. NÃO dispõe a typeface (o FontRegistry é dono da vida dela).</summary>
    public static string? TextPathD(string text, float size, SKTypeface typeface)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Normalize(System.Text.NormalizationForm.FormC);   // NFD (e+´) → NFC (é) → 1 glifo
        var tf = typeface ?? SKTypeface.Default;
        using var font = new SKFont(tf, size);
        using var path = font.GetTextPath(text, new SKPoint(0, 0));
        if (path is null || path.IsEmpty) return null;
        var b = path.Bounds;
        path.Transform(SKMatrix.CreateTranslation(-b.MidX, -b.MidY));  // center at 0,0
        return path.ToSvgPathData();
    }
}
