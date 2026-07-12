using SkiaSharp;

namespace Klip.Engine;

/// <summary>Text → vector outlines (path "d", centered at 0,0) — the base for wordmarks and
/// later for custom-font/glyph editing (outlines are editable paths).</summary>
public static class TextShape
{
    public static string? TextPathD(string text, float size, string family = "Segoe UI", bool bold = true)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        using var tf = SKTypeface.FromFamilyName(family,
            bold ? SKFontStyle.Bold : SKFontStyle.Normal) ?? SKTypeface.Default;
        using var font = new SKFont(tf, size);
        using var path = font.GetTextPath(text, new SKPoint(0, 0));
        if (path is null || path.IsEmpty) return null;
        var b = path.Bounds;
        path.Transform(SKMatrix.CreateTranslation(-b.MidX, -b.MidY));  // center at 0,0
        return path.ToSvgPathData();
    }
}
