using System;
using Klip.Model;
using SkiaSharp;

namespace Klip.Engine;

/// <summary>Renders a comp to encoded image bytes on the CPU (no GL context needed) — the safe
/// boundary for hosts (e.g. the Avalonia editor) that must not share our SkiaSharp with theirs.</summary>
public static class EngineExport
{
    public static byte[] RenderPng(Comp comp, double t) => RenderPng(comp, t, 1.0);

    /// <summary>PNG a uma resolução (scale) — 4K/qualquer zoom sem perda (vetores re-rasterizados).</summary>
    public static byte[] RenderPng(Comp comp, double t, double scale)
    {
        using var img = new Renderer().RenderFrame(comp, t, scale);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>Render do EDITOR: whiteboard infinito — viewport vw×vh, artboard desenhado com a
    /// transformação de vista (scale s, offset ox,oy). Re-render por zoom = VETORES SEMPRE NÍTIDOS.</summary>
    /// <summary>Guia de construção (só no editor, nunca no export): 'c'=círculo(a=cx,b=cy,r),
    /// 'h'=linha horizontal(a=y), 'v'=linha vertical(a=x). Coords do canvas (0,0=canto sup-esq).</summary>
    public readonly record struct Guide(char Kind, float A, float B, float R);

    /// <summary>Desenha a VISTA do editor (whiteboard branco + sombra do artboard + comp + guias) num
    /// canvas qualquer. Usado tanto pelo caminho rápido (direto p/ o bitmap do ecrã, SEM PNG) como pelo diagnóstico.</summary>
    public static void DrawView(SKCanvas c, Comp comp, double t, float s, float ox, float oy,
                                IReadOnlyList<Guide>? guides = null, bool fast = false)
    {
        c.Clear(new SKColor(0xFFFFFFFF));                       // canvas BRANCO (frames dão o cenário)
        c.Save();
        c.Translate(ox, oy);
        c.Scale(s);
        // sombra do artboard — o blur é caro; durante interação (fast) usa uma sombra chapada
        using (var sh = new SKPaint { Color = new SKColor(fast ? 0x22000000u : 0x33000000u), IsAntialias = true,
                                      ImageFilter = fast ? null : SKImageFilter.CreateBlur(14 / s, 14 / s) })
            c.DrawRect(0, 6 / s, comp.Width, comp.Height, sh);
        Renderer.DrawComp(c, comp, t);

        if (guides is { Count: > 0 })
        {
            using var gp = new SKPaint
            {
                IsAntialias = true, Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.1f / s, Color = new SKColor(0x8830A5C8),
            };
            foreach (var g in guides)
            {
                if (g.Kind == 'c') c.DrawCircle(g.A, g.B, g.R, gp);
                else if (g.Kind == 'h') c.DrawLine(-4000, g.A, comp.Width + 4000, g.A, gp);
                else if (g.Kind == 'v') c.DrawLine(g.A, -4000, g.A, comp.Height + 4000, gp);
            }
        }
        c.Restore();
    }

    public static byte[] RenderViewPng(Comp comp, double t, int vw, int vh, float s, float ox, float oy,
                                       IReadOnlyList<Guide>? guides = null)
    {
        var info = new SKImageInfo(Math.Max(1, vw), Math.Max(1, vh), SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        DrawView(surface.Canvas, comp, t, s, ox, oy, guides);
        surface.Canvas.Flush();
        using var img = surface.Snapshot();
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>Export SVG VERDADEIRO (vetores, não raster) via o backend SVG do Skia.</summary>
    public static void ExportSvg(Comp comp, double t, string path)
    {
        using var stream = new SKFileWStream(path);
        using var canvas = SKSvgCanvas.Create(new SKRect(0, 0, comp.Width, comp.Height), stream);
        Renderer.DrawComp(canvas, comp, t);
        canvas.Flush();
    }

    /// <summary>Hit-test PRECISO: o ponto (comp-space) está DENTRO da forma real da camada (não só da bbox)?
    /// Isto deixa clicar "através" da bbox de uma forma grande para uma que está atrás.</summary>
    public static bool HitLayer(Layer l, double px, double py, int compW, int compH)
    {
        if (l.Controller || l.Shape.Keys.Count == 0) return false;
        using var p = SKPath.ParseSvgPathData(l.Shape.Keys[0].PathD);
        if (p is null || p.IsEmpty) return false;
        double sx = l.ScaleX?.Eval(0) ?? l.Scale?.Eval(0) ?? 1.0;
        double sy = l.ScaleY?.Eval(0) ?? l.Scale?.Eval(0) ?? 1.0;
        if (Math.Abs(sx) < 1e-6 || Math.Abs(sy) < 1e-6) return false;
        double cx = compW / 2.0 + (l.PosX?.Eval(0) ?? 0);
        double cy = compH / 2.0 + (l.PosY?.Eval(0) ?? 0);
        double dx = px - cx, dy = py - cy;
        double rot = l.Rotation?.Eval(0) ?? 0;
        if (Math.Abs(rot) > 0.0001)
        {
            double r = -rot * Math.PI / 180.0, cs = Math.Cos(r), sn = Math.Sin(r);
            (dx, dy) = (dx * cs - dy * sn, dx * sn + dy * cs);
        }
        double lx = dx / sx + l.AnchorX, ly = dy / sy + l.AnchorY;
        // imagens/rive/lottie usam a forma como bbox proxy → path.Contains dá o comportamento certo
        return p.Contains((float)lx, (float)ly);
    }

    /// <summary>Canvas-space bounding box (x,y,w,h) of a layer at t=0, for editor hit-testing. Null if empty.</summary>
    public static (double x, double y, double w, double h)? LayerBounds(Layer l, int compW, int compH)
    {
        if (l.Shape.Keys.Count == 0) return null;
        using var p = SKPath.ParseSvgPathData(l.Shape.Keys[0].PathD);
        if (p is null) return null;
        var b = p.Bounds;
        double sx = l.ScaleX?.Eval(0) ?? l.Scale?.Eval(0) ?? 1.0;
        double sy = l.ScaleY?.Eval(0) ?? l.Scale?.Eval(0) ?? 1.0;
        double cx = compW / 2.0 + (l.PosX?.Eval(0) ?? 0);
        double cy = compH / 2.0 + (l.PosY?.Eval(0) ?? 0);
        return (cx + b.Left * sx, cy + b.Top * sy, b.Width * sx, b.Height * sy);
    }
}
