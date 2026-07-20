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
        // Fase 7 — índice por Id (matte resolve Id→Name) + fontes de matte consumidas (não se auto-desenham).
        _byId = new Dictionary<string, Layer>(comp.Layers.Count);
        foreach (var l in comp.Layers) if (!string.IsNullOrEmpty(l.Id)) _byId[l.Id!] = l;
        _consumedMattes = new System.Collections.Generic.HashSet<Layer>(System.Collections.Generic.ReferenceEqualityComparer.Instance);
        foreach (var l in comp.Layers) { var ms = ResolveMatte(l); if (ms is not null) _consumedMattes.Add(ms); }

        float cx = comp.Width / 2f, cy = comp.Height / 2f;
        foreach (var layer in comp.Layers)
        {
            if (layer.Controller) continue;   // NULL object: só transform (não desenha)
            if (_consumedMattes is not null && _consumedMattes.Contains(layer)) continue;  // fonte de matte: consumida, não se auto-desenha
            // camada 3D REAL → renderiza no compositor híbrido (câmara do comp) e compõe aqui
            if (layer.ThreeD is not null)
            {
                // a escala real da saída está na matriz do canvas (1 no preview, 2/4 no export) —
                // passá-la faz o 3D nascer já nessa resolução em vez de ser ampliado
                float outScale = Math.Abs(canvas.TotalMatrix.ScaleX);
                if (outScale <= 0.01f || float.IsNaN(outScale)) outScale = 1f;
                var img3d = ThreeD.Hybrid3D.Render(comp, layer, t, outScale);
                if (img3d is not null)
                {
                    using (img3d)
                    {
                        int save = canvas.Save();
                        canvas.Scale((float)comp.Width / img3d.Width, (float)comp.Height / img3d.Height);
                        // mipmap ligado: quando o 3D vem MAIOR que o destino (preview), reduzir sem
                        // mipmap serrilha — foi outra fonte do aspeto "choppy"
                        canvas.DrawImage(img3d, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                        canvas.RestoreToCount(save);
                    }
                    continue;
                }
                // sem GPU → cai no desenho 2D normal
            }
            var matte = ResolveMatte(layer);
            if (matte is not null) { DrawMatted(canvas, layer, matte, t, cx, cy, comp.Fps); continue; }
            DrawLayer(canvas, layer, t, cx, cy, comp.Fps);
        }
    }

    [ThreadStatic] private static Dictionary<string, Layer>? _byName;
    [ThreadStatic] private static Dictionary<string, Layer>? _byId;
    [ThreadStatic] private static System.Collections.Generic.HashSet<Layer>? _consumedMattes;
    [ThreadStatic] private static System.Collections.Generic.List<Particle>? _particleBuf;   // Fase 10 (reutilizado por frame)

    private static void DrawLayer(SKCanvas canvas, Layer layer, double t, float cx, float cy, double fps)
    {
        // Fase 10 — EMISSOR: a camada é inteiramente representada pelas partículas (não desenha a pose base)
        if (layer.Particles is ParticleSpec ps) { DrawParticles(canvas, layer, ps, t, cx, cy); return; }
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
        // Fase 7 — MOTION-BLUR real: amostra o obturador ao longo do MOVIMENTO (não gaussiano estático).
        double shutterFrames = layer.MotionBlur?.Eval(t) ?? 0.0;
        if (shutterFrames > 1e-4 && fps > 0)
        {
            int K = Math.Clamp(layer.MotionBlurSamples, 2, 32);
            DrawMotionBlur(canvas, layer, t, cx, cy, K, shutterFrames / fps);
            return;
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
        // rotação 3D REAL de camada plana (tilt em X/Y com perspetiva) via SK3dView — pivô no centro (0,0)
        float rx = (float)(l.RotationX?.Eval(t) ?? 0), ry = (float)(l.RotationY?.Eval(t) ?? 0);
        if (MathF.Abs(rx) > 0.0001f || MathF.Abs(ry) > 0.0001f)
            m = m.PreConcat(Perspective3D(rx, ry));
        if (MathF.Abs(skx) > 0.0001f) m = m.PreConcat(SKMatrix.CreateSkew(skx, 0));
        if (MathF.Abs(sx - 1) > 0.0001f || MathF.Abs(sy - 1) > 0.0001f) m = m.PreConcat(SKMatrix.CreateScale(sx, sy));
        if (l.AnchorX != 0 || l.AnchorY != 0) m = m.PreConcat(SKMatrix.CreateTranslation(-(float)l.AnchorX, -(float)l.AnchorY));
        return m;
    }

    /// <summary>Rotação 3D de uma camada PLANA (tilt X/Y) projetada com PERSPETIVA → SKMatrix 3x3.
    /// Derivada à mão (rotação do plano z=0 + divisão perspetiva, câmara a d px) — independente da versão do Skia.
    /// rotY: X=x·cosθ, W=1−x·sinθ/d. rotX: Y=y·cosφ, W=1−y·sinφ/d.</summary>
    private static SKMatrix Perspective3D(float rxDeg, float ryDeg)
    {
        const float d = 800f;                       // distância da câmara (px) — perspetiva suave
        float k = MathF.PI / 180f;
        var m = SKMatrix.Identity;
        if (MathF.Abs(ryDeg) > 0.0001f)
        {
            float c = MathF.Cos(ryDeg * k), s = MathF.Sin(ryDeg * k);
            m = m.PreConcat(new SKMatrix { ScaleX = c, ScaleY = 1, Persp2 = 1, Persp0 = -s / d });
        }
        if (MathF.Abs(rxDeg) > 0.0001f)
        {
            float c = MathF.Cos(rxDeg * k), s = MathF.Sin(rxDeg * k);
            m = m.PreConcat(new SKMatrix { ScaleX = 1, ScaleY = c, Persp2 = 1, Persp1 = -s / d });
        }
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
                                double opacityMul, double extraBlur, uint? colorOverride, SKMatrix? worldOverride = null)
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

        uint fill = colorOverride ?? layer.FillColor?.Eval(t) ?? layer.FillArgb;   // cor animável manda; uint = fallback
        byte a = (byte)(((fill >> 24) & 0xFF) * op);   // alpha própria da cor × opacidade da camada
        var bounds = shape.Bounds;

        int save = canvas.Save();
        canvas.Translate(cx, cy);
        if (layer.ClipD is string clipD)       // PowerClip: container fixed in canvas space
        {
            using var cp = SKPath.ParseSvgPathData(clipD);
            if (cp is not null) canvas.ClipPath(cp, SKClipOperation.Intersect, antialias: true);
        }
        // transform de MUNDO (posição/rotação/escala/âncora + cadeia de PARENTING); worldOverride = partícula
        var world = worldOverride ?? WorldMatrix(layer, t);
        canvas.Concat(ref world);

        // compose depth (drop shadow) + blur into one image filter
        SKImageFilter? filter = blur > 0.01 ? SKImageFilter.CreateBlur((float)blur, (float)blur) : null;
        if (layer.Shadow && colorOverride is null)
        {
            float sh = MathF.Max(6f, (float)bounds.Height * 0.06f);
            var ds = SKImageFilter.CreateDropShadow(0, sh, sh, sh, new SKColor(0, 0, 0, (byte)(70 * op)));
            filter = filter is null ? ds : SKImageFilter.CreateCompose(ds, filter);
        }
        // Fase 7 — DROP-SHADOW premium keyframável (offset/blur/opacidade/cor), atrás da forma, na mesma cadeia.
        if (layer.DropShadow is ShadowSpec dsp && colorOverride is null)
        {
            float dx = (float)(dsp.Dx?.Eval(t) ?? 0), dy = (float)(dsp.Dy?.Eval(t) ?? 0);
            float sb = (float)Math.Max(0, dsp.Blur?.Eval(t) ?? 0);
            double so = Math.Clamp(dsp.Opacity?.Eval(t) ?? 1, 0, 1) * op;
            uint scol = dsp.Color?.Eval(t) ?? 0xFF000000u;
            byte salpha = (byte)Math.Clamp(((scol >> 24) & 0xFF) / 255.0 * so * 255.0, 0, 255);
            if (salpha > 0 && (MathF.Abs(dx) > 0.001f || MathF.Abs(dy) > 0.001f || sb > 0.001f))
            {
                var ds2 = SKImageFilter.CreateDropShadow(dx, dy, sb, sb, ((SKColor)scol).WithAlpha(salpha));
                filter = filter is null ? ds2 : SKImageFilter.CreateCompose(ds2, filter);
            }
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
                Klip.Engine.Rive.RiveClip.Draw(canvas, dst, rivePath, layer.RiveAnim, t, layer.RiveMachine, layer.RiveInputs);
                canvas.Restore();
            }
            else
            {
                Klip.Engine.Rive.RiveClip.Draw(canvas, dst, rivePath, layer.RiveAnim, t, layer.RiveMachine, layer.RiveInputs);
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
        bool gradFill = false;   // pintámos com GradientSpec? então o alpha da cor de fill não manda no draw

        // GRADIENTE MULTI-STOP (novo) — tem prioridade sobre o par legado, mas NUNCA em partículas
        // e trails: aí o colorOverride é a cor da própria partícula e um gradiente arruinava o efeito.
        if (layer.FillGradient is GradientSpec gspec && colorOverride is null)
        {
            shader = GradientShader.Build(gspec, bounds, t, (byte)Math.Clamp(op * 255.0, 0, 255));
            if (shader is not null) { paint.Shader = shader; gradFill = true; }
        }

        if (!gradFill && layer.FillArgb2 is uint f2 && colorOverride is null)
        {
            var c1 = ((SKColor)fill).WithAlpha(a);
            var c2 = ((SKColor)(layer.FillColor2?.Eval(t) ?? f2)).WithAlpha(a);   // 2ª cor animável (best-effort)
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
        else if (!gradFill)
        {
            paint.Color = ((SKColor)fill).WithAlpha(a);
        }

        // Com gradiente as paragens trazem o seu próprio alpha: uma camada cuja FillArgb calhe ser
        // transparente continua a ter de desenhar, senão o gradiente nunca aparecia.
        if (a > 0 || gradFill) canvas.DrawPath(shape, paint);
        shader?.Dispose();

        // stroke (contorno) — line-work de motion graphics
        uint? scOpt = layer.StrokeColor is { } stc ? stc.Eval(t) : layer.StrokeArgb;   // cor de stroke animável; uint = fallback
        if (scOpt is uint sc && layer.StrokeWidth > 0 && colorOverride is null)
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

        // Fase 7 — GLOW: bloom ADITIVO (SKBlendMode.Plus) por cima → halo à volta + brilho no bordo.
        if (layer.Glow is GlowSpec gl && colorOverride is null && op > 0.01)
        {
            float gr = (float)Math.Max(0, gl.Radius?.Eval(t) ?? 0);
            double gi = Math.Max(0, gl.Intensity?.Eval(t) ?? 1.0);
            if (gr > 0.01 && gi > 0.001)
            {
                uint gcol = gl.Color?.Eval(t) ?? fill;
                byte ga = (byte)Math.Clamp(((gcol >> 24) & 0xFF) / 255.0 * Math.Min(gi, 1.0) * op * 255.0, 0, 255);
                using var gblur = SKImageFilter.CreateBlur(gr, gr);
                using var gp = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = ((SKColor)gcol).WithAlpha(ga), ImageFilter = gblur, BlendMode = SKBlendMode.Plus };
                int passes = (int)Math.Clamp(Math.Ceiling(gi), 1, 4);   // intensidade>1 → mais luz somada
                for (int p = 0; p < passes; p++) canvas.DrawPath(shape, gp);
            }
        }

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

    // ---- Fase 7 — TRACK-MATTE (uma camada-FONTE define o alpha/luma de outra) -------------------------
    // Matrizes de cor 4x5 (row-major, entradas normalizadas 0..1): A' = luma Rec709 (e 1−luma no invertido).
    private static readonly SKColorFilter LumaToAlpha = SKColorFilter.CreateColorMatrix(new float[]
    { 0,0,0,0,0,  0,0,0,0,0,  0,0,0,0,0,  0.2126f,0.7152f,0.0722f,0,0 });
    private static readonly SKColorFilter InvLumaToAlpha = SKColorFilter.CreateColorMatrix(new float[]
    { 0,0,0,0,0,  0,0,0,0,0,  0,0,0,0,0,  -0.2126f,-0.7152f,-0.0722f,0,1 });

    /// <summary>Resolve a camada-FONTE do matte do alvo (Id→Name, regra estável). Null se sem matte/auto-ref.</summary>
    private static Layer? ResolveMatte(Layer target)
    {
        if (target.Matte == MatteMode.None || string.IsNullOrEmpty(target.MatteSourceId)) return null;
        Layer? src = null;
        if (_byId is not null && _byId.TryGetValue(target.MatteSourceId!, out var byId)) src = byId;
        else if (_byName is not null && _byName.TryGetValue(target.MatteSourceId!, out var byName)) src = byName;
        if (src is null || ReferenceEquals(src, target)) return null;
        return src;
    }

    /// <summary>Desenha o ALVO isolado e recorta-o pelo alpha/luma da FONTE (DstIn/DstOut estilo AE).</summary>
    private static void DrawMatted(SKCanvas canvas, Layer layer, Layer matte, double t, float cx, float cy, double fps)
    {
        int outer = canvas.SaveLayer();                 // offscreen full-canvas → DstIn não apaga o fundo
        DrawLayer(canvas, layer, t, cx, cy, fps);       // alvo completo (trail/motion-blur/glow/sombra dentro do isolamento)
        using var mp = new SKPaint { IsAntialias = true };
        switch (layer.Matte)
        {
            case MatteMode.AlphaNormal: mp.BlendMode = SKBlendMode.DstIn; break;
            case MatteMode.AlphaInvert: mp.BlendMode = SKBlendMode.DstOut; break;
            case MatteMode.LumaNormal:  mp.BlendMode = SKBlendMode.DstIn; mp.ColorFilter = LumaToAlpha; break;
            case MatteMode.LumaInvert:  mp.BlendMode = SKBlendMode.DstIn; mp.ColorFilter = InvLumaToAlpha; break;
        }
        canvas.SaveLayer(mp);
        DrawOne(canvas, matte, t, cx, cy, 1.0, 0, null);   // fonte como STENCIL (sem motion-blur/trail)
        canvas.Restore();
        canvas.RestoreToCount(outer);
    }

    /// <summary>Fase 7 — MOTION-BLUR real: média de K poses ao longo do obturador → borrão DIRECIONAL
    /// (segue posição/rotação/escala reais). Acumulador Plus a 1/K = média-caixa; nunca gaussiano estático.</summary>
    private static void DrawMotionBlur(SKCanvas canvas, Layer layer, double t, float cx, float cy, int K, double shutterSecs)
    {
        int outer = canvas.SaveLayer();                 // acumulador ISOLADO (transparente) → Plus só soma as sub-amostras
        using var sub = new SKPaint
        {
            BlendMode = SKBlendMode.Plus,
            Color = new SKColor(255, 255, 255, (byte)Math.Clamp((int)Math.Round(255.0 / K), 1, 255)),
        };
        for (int k = 0; k < K; k++)
        {
            double frac = (k + 0.5) / K - 0.5;          // ponto médio de cada fatia → simétrico (média dos offsets = 0)
            double tk = t + shutterSecs * frac;
            if (tk < 0) tk = 0;
            canvas.SaveLayer(sub);
            DrawOne(canvas, layer, tk, cx, cy, 1.0, 0, null);
            canvas.Restore();
        }
        canvas.RestoreToCount(outer);
    }

    /// <summary>Fase 10 — EMISSOR: cada partícula viva desenha o Shape da camada (sprite) com transform/opacidade/
    /// cor próprios, REUTILIZANDO DrawOne (colorOverride + worldOverride). Determinístico (ParticleSim puro).</summary>
    private static void DrawParticles(SKCanvas canvas, Layer layer, ParticleSpec ps, double t, float cx, float cy)
    {
        var buf = _particleBuf ??= new System.Collections.Generic.List<Particle>(512);
        uint fill = layer.FillColor?.Eval(t) ?? layer.FillArgb;
        ParticleSim.Emit(ps, t, buf, fill);
        if (buf.Count == 0) return;
        var emitter = WorldMatrix(layer, t);   // base do emissor (pos/rot/escala/parenting) — animável por frame
        const float margin = 200f;
        foreach (var p in buf)
        {
            if (p.Opacity <= 0.001 || p.Scale <= 0.0001) continue;
            var m = emitter;
            m = m.PreConcat(SKMatrix.CreateTranslation((float)p.OffsetX, (float)p.OffsetY));
            if (Math.Abs(p.RotationDeg) > 0.001) m = m.PreConcat(SKMatrix.CreateRotationDegrees((float)p.RotationDeg));
            if (Math.Abs(p.Scale - 1.0) > 0.001) m = m.PreConcat(SKMatrix.CreateScale((float)p.Scale, (float)p.Scale));
            var c0 = m.MapPoint(0, 0);                       // DrawOne faz Translate(cx,cy) + Concat(m)
            float px = cx + c0.X, py = cy + c0.Y;
            if (px < -margin || px > cx * 2 + margin || py < -margin || py > cy * 2 + margin) continue;   // culling
            DrawOne(canvas, layer, t, cx, cy, opacityMul: p.Opacity, extraBlur: 0, colorOverride: p.Color, worldOverride: m);
        }
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
