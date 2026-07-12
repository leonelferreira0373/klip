using System;
using Klip.Model;
using SkiaSharp;

namespace Klip.Engine;

/// <summary>
/// Renders a composition frame at time t. Surface-agnostic (CPU or GPU). Applies per-layer
/// transforms (translate/rotate/scale about the canvas centre) and motion trails, and evaluates
/// shape morphs. Playback and export call the exact same DrawComp.
/// </summary>
public sealed class Renderer
{
    public SKImage RenderFrame(Comp comp, double t) => RenderFrame(comp, t, 1.0);

    /// <summary>Render at a resolution multiplier — VECTORES re-rasterizados na resolução final
    /// (zoom/4K SEM perda de qualidade; nunca esticamos um bitmap).</summary>
    public SKImage RenderFrame(Comp comp, double t, double scale)
    {
        int w = Math.Max(2, (int)Math.Round(comp.Width * scale) & ~1);   // par (yuv420p exige)
        int h = Math.Max(2, (int)Math.Round(comp.Height * scale) & ~1);
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var c = surface.Canvas;
        if (scale != 1.0) c.Scale((float)w / comp.Width, (float)h / comp.Height);
        DrawComp(c, comp, t);
        c.Flush();
        return surface.Snapshot();
    }

    /// <summary>Paints the whole comp at time t onto ANY canvas — CPU or GPU surface, identical code.</summary>
    public static void DrawComp(SKCanvas canvas, Comp comp, double t)
    {
        // fundo do artboard como RECT (não Clear) — compõe sobre o whiteboard infinito do editor
        using (var bgPaint = new SKPaint { IsAntialias = true })
        {
            if (comp.BackgroundArgb2 is uint bg2)
            {
                using var bgShader = SKShader.CreateLinearGradient(
                    new SKPoint(0, 0), new SKPoint(0, comp.Height),
                    new[] { (SKColor)comp.BackgroundArgb, (SKColor)bg2 }, null, SKShaderTileMode.Clamp);
                bgPaint.Shader = bgShader;
            }
            else
            {
                bgPaint.Color = (SKColor)comp.BackgroundArgb;
            }
            canvas.DrawRect(0, 0, comp.Width, comp.Height, bgPaint);
        }

        // índice nome→camada p/ PARENTING (mais recente ganha em nomes repetidos)
        _byName = new Dictionary<string, Layer>(comp.Layers.Count);
        foreach (var l in comp.Layers) if (!string.IsNullOrEmpty(l.Name)) _byName[l.Name] = l;

        float cx = comp.Width / 2f, cy = comp.Height / 2f;
        foreach (var layer in comp.Layers)
        {
            if (layer.Controller) continue;   // NULL object: só transform (não desenha)
            // camada 3D REAL → renderiza no compositor híbrido (câmara do comp) e compõe aqui
            if (layer.ThreeD is not null)
            {
                var img3d = ThreeD.Hybrid3D.Render(comp, layer, t);
                if (img3d is not null)
                {
                    using (img3d)
                    {
                        int save = canvas.Save();
                        canvas.Scale((float)comp.Width / img3d.Width, (float)comp.Height / img3d.Height);
                        canvas.DrawImage(img3d, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
                        canvas.RestoreToCount(save);
                    }
                    continue;
                }
                // sem GPU → cai no desenho 2D normal
            }
            DrawLayer(canvas, layer, t, cx, cy);
        }
    }

    [ThreadStatic] private static Dictionary<string, Layer>? _byName;

    private static void DrawLayer(SKCanvas canvas, Layer layer, double t, float cx, float cy)
    {
        // trail: fading echoes at earlier times, drawn behind the current pose
        if (layer.Trail is { Count: > 0 } tr)
        {
            for (int i = tr.Count; i >= 1; i--)
            {
                double tt = t - i * tr.SpacingSeconds;
                if (tt < 0) continue;
                double fade = tr.FadeTo * (1.0 - (double)(i - 1) / tr.Count); // older = fainter
                DrawOne(canvas, layer, tt, cx, cy, opacityMul: fade, extraBlur: tr.ExtraBlur, colorOverride: tr.ColorArgb);
            }
        }
        DrawOne(canvas, layer, t, cx, cy, opacityMul: 1.0, extraBlur: 0, colorOverride: null);
    }

    /// <summary>Matriz LOCAL da camada no tempo t: T(pos)·R·Skew·S·T(-âncora). Pivô no anchor.</summary>
    private static SKMatrix LocalMatrix(Layer l, double t)
    {
        float px = (float)(l.PosX?.Eval(t) ?? 0), py = (float)(l.PosY?.Eval(t) ?? 0);
        float rot = (float)(l.Rotation?.Eval(t) ?? 0);
        float uni = (float)(l.Scale?.Eval(t) ?? 1.0);
        float sx = (float)(l.ScaleX?.Eval(t) ?? uni), sy = (float)(l.ScaleY?.Eval(t) ?? uni);
        float skx = (float)(l.SkewX?.Eval(t) ?? 0);
        var m = SKMatrix.CreateTranslation(px, py);
        if (MathF.Abs(rot) > 0.0001f) m = m.PreConcat(SKMatrix.CreateRotationDegrees(rot));
        if (MathF.Abs(skx) > 0.0001f) m = m.PreConcat(SKMatrix.CreateSkew(skx, 0));
        if (MathF.Abs(sx - 1) > 0.0001f || MathF.Abs(sy - 1) > 0.0001f) m = m.PreConcat(SKMatrix.CreateScale(sx, sy));
        if (l.AnchorX != 0 || l.AnchorY != 0) m = m.PreConcat(SKMatrix.CreateTranslation(-(float)l.AnchorX, -(float)l.AnchorY));
        return m;
    }

    /// <summary>Matriz de MUNDO = mãe·mãe·…·local (PARENTING). Anda pela cadeia de pais por nome.</summary>
    private static SKMatrix WorldMatrix(Layer l, double t, int depth = 0)
    {
        var local = LocalMatrix(l, t);
        if (depth > 24 || l.Parent is not string pn || _byName is null || !_byName.TryGetValue(pn, out var parent) || ReferenceEquals(parent, l))
            return local;
        return WorldMatrix(parent, t, depth + 1).PreConcat(local);
    }

    /// <summary>Trim-path: extrai [t0..t1] (0..1) do comprimento total — o "desenhar-se".</summary>
    private static SKPath TrimPath(SKPath src, float t0, float t1)
    {
        var dst = new SKPath();
        using var measure = new SKPathMeasure(src, false);
        do
        {
            float len = measure.Length;
            if (len <= 0) continue;
            using var seg = new SKPath();
            if (measure.GetSegment(len * t0, len * t1, seg, true))
                dst.AddPath(seg);
        } while (measure.NextContour());
        return dst;
    }

    private static void DrawOne(SKCanvas canvas, Layer layer, double t, float cx, float cy,
                                double opacityMul, double extraBlur, uint? colorOverride)
    {
        using var shape0 = EvalMorph(layer.Shape, t);
        if (shape0 is null) return;

        // trim-path (linha a desenhar-se): aplica antes de tudo
        float tr0 = (float)Math.Clamp(layer.TrimStart?.Eval(t) ?? 0, 0, 1);
        float tr1 = (float)Math.Clamp(layer.TrimEnd?.Eval(t) ?? 1, 0, 1);
        bool trimmed = tr0 > 0.0001f || tr1 < 0.9999f;
        using var trimShape = trimmed ? TrimPath(shape0, tr0, tr1) : null;
        var shape = trimShape ?? shape0;
        if (trimmed && shape.IsEmpty) return;

        double blur = (layer.BlurRadius?.Eval(t) ?? 0) + extraBlur;
        double op = Math.Clamp((layer.Opacity?.Eval(t) ?? 1.0) * opacityMul, 0.0, 1.0);
        if (op <= 0.001) return;

        uint fill = colorOverride ?? layer.FillArgb;
        byte a = (byte)(((fill >> 24) & 0xFF) * op);   // alpha própria da cor × opacidade da camada
        var bounds = shape.Bounds;

        int save = canvas.Save();
        canvas.Translate(cx, cy);
        if (layer.ClipD is string clipD)       // PowerClip: container fixed in canvas space
        {
            using var cp = SKPath.ParseSvgPathData(clipD);
            if (cp is not null) canvas.ClipPath(cp, SKClipOperation.Intersect, antialias: true);
        }
        // transform de MUNDO (posição/rotação/escala/âncora + cadeia de PARENTING)
        var world = WorldMatrix(layer, t);
        canvas.Concat(ref world);

        // compose depth (drop shadow) + blur into one image filter
        SKImageFilter? filter = blur > 0.01 ? SKImageFilter.CreateBlur((float)blur, (float)blur) : null;
        if (layer.Shadow && colorOverride is null)
        {
            float sh = MathF.Max(6f, (float)bounds.Height * 0.06f);
            var ds = SKImageFilter.CreateDropShadow(0, sh, sh, sh, new SKColor(0, 0, 0, (byte)(70 * op)));
            filter = filter is null ? ds : SKImageFilter.CreateCompose(ds, filter);
        }

        // camada RIVE: runtime C# custom desenha o frame animado no tempo t (caixa RiveW×RiveH centrada)
        if (layer.RivePath is string rivePath && colorOverride is null)
        {
            float rw = (float)layer.RiveW, rh = (float)layer.RiveH;
            var dst = new SKRect(-rw / 2, -rh / 2, rw / 2, rh / 2);
            if (op < 0.999)
            {
                using var lp = new SKPaint { Color = new SKColor(255, 255, 255, (byte)(255 * op)) };
                canvas.SaveLayer(lp);
                Klip.Engine.Rive.RiveClip.Draw(canvas, dst, rivePath, layer.RiveAnim, t);
                canvas.Restore();
            }
            else
            {
                Klip.Engine.Rive.RiveClip.Draw(canvas, dst, rivePath, layer.RiveAnim, t);
            }
            filter?.Dispose();
            canvas.RestoreToCount(save);
            return;
        }

        // camada LOTTIE: runtime C# custom desenha o frame animado no tempo t
        if (layer.LottiePath is string lottiePath && colorOverride is null)
        {
            float lw = (float)layer.LottieW, lh = (float)layer.LottieH;
            var dst = new SKRect(-lw / 2, -lh / 2, lw / 2, lh / 2);
            if (op < 0.999)
            {
                using var lp = new SKPaint { Color = new SKColor(255, 255, 255, (byte)(255 * op)) };
                canvas.SaveLayer(lp);
                Klip.Engine.Lottie.LottieClip.Draw(canvas, dst, lottiePath, t);
                canvas.Restore();
            }
            else Klip.Engine.Lottie.LottieClip.Draw(canvas, dst, lottiePath, t);
            filter?.Dispose();
            canvas.RestoreToCount(save);
            return;
        }

        // camada de IMAGEM raster: desenha o bitmap (centrado, tamanho natural) e sai
        if (layer.ImagePath is string imgPath && colorOverride is null)
        {
            var bmp = ImageCache(imgPath);
            if (bmp is not null)
            {
                using var ip = new SKPaint { IsAntialias = true };
                ip.Color = SKColors.White.WithAlpha((byte)(255 * op));
                if (filter is not null) ip.ImageFilter = filter;
                var dst = new SKRect(-bmp.Width / 2f, -bmp.Height / 2f, bmp.Width / 2f, bmp.Height / 2f);
                canvas.DrawBitmap(bmp, dst, ip);
            }
            filter?.Dispose();
            canvas.RestoreToCount(save);
            return;
        }

        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        if (filter is not null) paint.ImageFilter = filter;

        // gradient vs solid fill
        SKShader? shader = null;
        if (layer.FillArgb2 is uint f2 && colorOverride is null)
        {
            var c1 = ((SKColor)fill).WithAlpha(a);
            var c2 = ((SKColor)f2).WithAlpha(a);
            // controlos profundos: direção (ângulo), midpoint e spread ("velocidade" da transição)
            float mid = (float)Math.Clamp(layer.GradMid, 0.0, 1.0);
            float half = (float)Math.Clamp(layer.GradSpread, 0.02, 1.0) / 2f;
            var pos = new[] { Math.Clamp(mid - half, 0f, 1f), Math.Clamp(mid + half, 0f, 1f) };
            if (layer.FillRadial)
            {
                shader = SKShader.CreateRadialGradient(new SKPoint(bounds.MidX, bounds.MidY),
                    MathF.Max(bounds.Width, bounds.Height) * 0.62f, new[] { c1, c2 }, pos, SKShaderTileMode.Clamp);
            }
            else
            {
                double rad = layer.GradAngle * Math.PI / 180.0;
                float dx = (float)Math.Cos(rad), dy = (float)Math.Sin(rad);
                float span = (MathF.Abs(bounds.Width * dx) + MathF.Abs(bounds.Height * dy)) / 2f;
                var ctr = new SKPoint(bounds.MidX, bounds.MidY);
                shader = SKShader.CreateLinearGradient(
                    new SKPoint(ctr.X - dx * span, ctr.Y - dy * span),
                    new SKPoint(ctr.X + dx * span, ctr.Y + dy * span),
                    new[] { c1, c2 }, pos, SKShaderTileMode.Clamp);
            }
            paint.Shader = shader;
        }
        else
        {
            paint.Color = ((SKColor)fill).WithAlpha(a);
        }

        if (a > 0) canvas.DrawPath(shape, paint);
        shader?.Dispose();

        // stroke (contorno) — line-work de motion graphics
        if (layer.StrokeArgb is uint sc && layer.StrokeWidth > 0 && colorOverride is null)
        {
            byte sa = (byte)(((sc >> 24) & 0xFF) * op);
            if (sa > 0)
            {
                using var sp = new SKPaint
                {
                    IsAntialias = true,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = (float)layer.StrokeWidth,
                    StrokeCap = SKStrokeCap.Round,
                    StrokeJoin = SKStrokeJoin.Round,
                    Color = ((SKColor)sc).WithAlpha(sa),
                };
                if (filter is not null) sp.ImageFilter = filter;
                canvas.DrawPath(shape, sp);
            }
        }
        filter?.Dispose();

        // glossy specular: a soft white highlight in the upper third
        if (layer.SpecularStrength > 0 && colorOverride is null && op > 0.01)
        {
            using var spec = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
            using var specShader = SKShader.CreateLinearGradient(
                new SKPoint(bounds.MidX, bounds.Top), new SKPoint(bounds.MidX, bounds.MidY),
                new[]
                {
                    new SKColor(255, 255, 255, (byte)(180 * layer.SpecularStrength * op)),
                    new SKColor(255, 255, 255, 0),
                }, null, SKShaderTileMode.Clamp);
            spec.Shader = specShader;
            canvas.DrawPath(shape, spec);
        }

        canvas.RestoreToCount(save);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SKBitmap?> _imgCache = new();

    private static SKBitmap? ImageCache(string path) => _imgCache.GetOrAdd(path, p =>
    {
        try { return SKBitmap.Decode(p); }
        catch { return null; }
    });

    /// <summary>Evaluate the shape track to a concrete path at time t (interpolating between morph keys).</summary>
    private static SKPath? EvalMorph(MorphTrack track, double t)
    {
        var keys = track.Keys;
        if (keys.Count == 0) return null;
        if (keys.Count == 1 || t <= keys[0].Time) return SKPath.ParseSvgPathData(keys[0].PathD);
        if (t >= keys[^1].Time) return SKPath.ParseSvgPathData(keys[^1].PathD);

        for (int i = 0; i < keys.Count - 1; i++)
        {
            var a = keys[i];
            var b = keys[i + 1];
            if (t >= a.Time && t <= b.Time)
            {
                double span = b.Time - a.Time;
                double u = span <= 0 ? 0 : (t - a.Time) / span;
                float e = (float)Easings.Apply(a.Ease, u);
                using var pa = SKPath.ParseSvgPathData(a.PathD);
                using var pb = SKPath.ParseSvgPathData(b.PathD);
                if (pa is null || pb is null) return null;
                return PathMorph.Interpolate(pa, pb, e);
            }
        }
        return SKPath.ParseSvgPathData(keys[^1].PathD);
    }
}
