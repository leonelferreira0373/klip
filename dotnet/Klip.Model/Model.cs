using System;
using System.Collections.Generic;

namespace Klip.Model;

/// <summary>Easing applied on the segment leaving a keyframe. Pure math, no deps.</summary>
public enum Easing { Linear, Hold, EaseIn, EaseOut, EaseInOut, EaseOutBack, EaseInOutBack }

public static class Easings
{
    public static double Apply(Easing e, double u)
    {
        u = Math.Clamp(u, 0.0, 1.0);
        const double c1 = 1.70158;   // "back" overshoot
        const double c2 = c1 * 1.525;
        const double c3 = c1 + 1.0;
        return e switch
        {
            Easing.Hold        => 0.0,
            Easing.EaseIn      => u * u * u,
            Easing.EaseOut     => 1.0 - Math.Pow(1.0 - u, 3),
            Easing.EaseInOut   => u < 0.5 ? 4 * u * u * u : 1.0 - Math.Pow(-2 * u + 2, 3) / 2.0,
            Easing.EaseOutBack => 1.0 + c3 * Math.Pow(u - 1, 3) + c1 * Math.Pow(u - 1, 2),   // cartoonish overshoot
            Easing.EaseInOutBack => u < 0.5
                ? Math.Pow(2 * u, 2) * ((c2 + 1) * 2 * u - c2) / 2.0
                : (Math.Pow(2 * u - 2, 2) * ((c2 + 1) * (u * 2 - 2) + c2) + 2) / 2.0,
            _ => u, // Linear
        };
    }
}

/// <summary>A scalar keyframe. <paramref name="Bez"/> = optional cubic-bezier [x1,y1,x2,y2]
/// (CSS-style) that overrides <paramref name="Ease"/> — advanced keyframing.</summary>
public sealed record Keyframe(double Time, double Value, Easing Ease = Easing.Linear, double[]? Bez = null);

/// <summary>Expression kinds that reshape how a Track evaluates (the "expressions engine").
/// Spring = overshoot/bounce on keyframe arrival (the Apple-style secret). Wiggle = procedural life.</summary>
public enum ExprKind { None, Spring, Wiggle, Code }

/// <summary>A track-level expression. Spring: P1=freq(bounciness), P2=decay(settle). Wiggle: P1=freq(Hz), P2=amp.
/// Code: <paramref name="Code"/> = expressão JavaScript estilo After Effects (value, time, wiggle, loopOut, linear…).</summary>
public sealed record TrackExpr(ExprKind Kind, double P1, double P2, string? Code = null);

/// <summary>An animatable scalar channel. Static value = a track with 1 keyframe (or null layer field).</summary>
public sealed record Track(IReadOnlyList<Keyframe> Keys, TrackExpr? Expr = null)
{
    /// <summary>Hook do motor de EXPRESSÕES (Jint) injectado pelo Engine — o Model fica puro.
    /// (code, thisTrack, time) → valor. Null = expressões-código indisponíveis (fallback aos keyframes).</summary>
    public static Func<string, Track, double, double>? CodeEval;

    /// <summary>Valor SÓ dos keyframes (sem expressão) em t — usado pelo motor de expressões (value/valueAtTime).</summary>
    public double KeyframesAt(double t) => EvalKeyframes(t);

    public double Eval(double t)
    {
        if (Expr is { Kind: ExprKind.Code, Code: { Length: > 0 } code } && CodeEval is not null)
        {
            try { return CodeEval(code, this, t); } catch { return EvalKeyframes(t); }
        }
        double baseVal = EvalKeyframes(t);
        if (Expr is { Kind: ExprKind.Wiggle } w)
            return baseVal + w.P2 * Wiggle1D(w.P1 * t + 0.123);
        return baseVal;
    }

    private double EvalKeyframes(double t)
    {
        if (Keys.Count == 0) return 0.0;
        if (t <= Keys[0].Time) return Keys[0].Value;
        var last = Keys[^1];
        if (t >= last.Time) return last.Value;
        bool spring = Expr is { Kind: ExprKind.Spring };
        for (int i = 0; i < Keys.Count - 1; i++)
        {
            var a = Keys[i];
            var b = Keys[i + 1];
            if (t >= a.Time && t <= b.Time)
            {
                double span = b.Time - a.Time;
                double u = span <= 0 ? 0 : (t - a.Time) / span;
                double e = spring
                    ? SpringEase(u, Expr!.P1, Expr.P2)
                    : a.Bez is { Length: 4 } bz
                        ? CubicBezier(u, bz[0], bz[1], bz[2], bz[3])
                        : Easings.Apply(a.Ease, u);
                return a.Value + (b.Value - a.Value) * e;
            }
        }
        return last.Value;
    }

    /// <summary>Damped-spring easing 0→1 that OVERSHOOTS past 1 and settles — the bounce.
    /// freq = bounciness (more = more oscillations), decay = settle speed (more = calmer).</summary>
    public static double SpringEase(double u, double freq, double decay)
    {
        if (u <= 0) return 0;
        if (u >= 1) return 1;
        if (freq <= 0) freq = 18;
        if (decay <= 0) decay = 8;
        return 1.0 - Math.Exp(-decay * u) * Math.Cos(freq * u);
    }

    // deterministic smooth 1-D value noise in [-1,1] for wiggle
    private static double Wiggle1D(double x)
    {
        int i = (int)Math.Floor(x);
        double f = x - i;
        double a = Hash(i), b = Hash(i + 1);
        double s = f * f * (3 - 2 * f);
        return a + (b - a) * s;
    }
    private static double Hash(int n)
    {
        n = (n << 13) ^ n;
        return 1.0 - ((n * (n * n * 15731 + 789221) + 1376312589) & 0x7fffffff) / 1073741824.0;
    }

    /// <summary>CSS-style cubic-bezier(x1,y1,x2,y2): resolve t para x=u (Newton) e devolve y(t).</summary>
    private static double CubicBezier(double u, double x1, double y1, double x2, double y2)
    {
        static double C(double a, double b, double s)
        { double inv = 1 - s; return 3 * inv * inv * s * a + 3 * inv * s * s * b + s * s * s; }
        double s = u;
        for (int i = 0; i < 8; i++)
        {
            double x = C(x1, x2, s) - u;
            double dx = 3 * (1 - s) * (1 - s) * x1 + 6 * (1 - s) * s * (x2 - x1) + 3 * s * s * (1 - x2);
            if (Math.Abs(dx) < 1e-8) break;
            s = Math.Clamp(s - x / dx, 0, 1);
        }
        return C(y1, y2, s);
    }

    public static Track Const(double v) => new(new[] { new Keyframe(0, v) });
    public static Track Of(params Keyframe[] keys) => new(keys);
}

/// <summary>A shape keyframe: an SVG path "d" (centered at local 0,0) at <paramref name="Time"/>.</summary>
public sealed record MorphKey(double Time, string PathD, Easing Ease = Easing.Linear);

/// <summary>A shape-morph channel (evaluated to an interpolated path by the engine).</summary>
public sealed record MorphTrack(IReadOnlyList<MorphKey> Keys)
{
    public static MorphTrack Static(string d) => new(new[] { new MorphKey(0, d) });
}

/// <summary>Motion trail: N fading echoes of the layer rendered at earlier times.</summary>
public sealed record TrailSpec(int Count, double SpacingSeconds, double FadeTo,
                               double ExtraBlur = 0, uint? ColorArgb = null);

/// <summary>
/// One animated layer. Shapes are authored at local (0,0); the transform tracks place them.
/// Optional tracks default to identity (Pos 0, Rot 0, Scale 1, Blur 0, Opacity 1). FillArgb = 0xAARRGGBB.
/// </summary>
public sealed record Layer(
    string Name,
    MorphTrack Shape,
    uint FillArgb,
    Track? PosX = null,
    Track? PosY = null,
    Track? Rotation = null,
    Track? Scale = null,        // uniform (fallback for both axes)
    Track? ScaleX = null,       // horizontal scale — foreshortening for faux-3D turns
    Track? ScaleY = null,       // vertical scale
    Track? SkewX = null,        // horizontal skew — shear for 2.5D perspective
    Track? BlurRadius = null,
    Track? Opacity = null,
    TrailSpec? Trail = null,
    uint? FillArgb2 = null,      // if set → gradient FillArgb→FillArgb2
    bool FillRadial = false,     // gradient shape (radial vs linear top→bottom)
    bool Shadow = false,         // soft drop shadow (grounding / depth)
    double SpecularStrength = 0, // 0..1 top glossy highlight overlay
    string? ClipD = null,        // PowerClip: content clipped inside this canvas-space path
    uint? StrokeArgb = null,     // stroke (contorno) — motion-graphics line work
    double StrokeWidth = 0,
    Track? TrimStart = null,     // trim-path 0..1 — o clássico "linha a desenhar-se"
    Track? TrimEnd = null,
    Extrude3D? ThreeD = null,    // camada 3D REAL (extrude+bevel, iluminada, via câmara do comp)
    string? ImagePath = null,    // camada de IMAGEM raster (png/jpg…) — Shape serve de bounds
    double GradAngle = 90,       // direção do gradiente linear em graus (90 = topo→fundo)
    double GradMid = 0.5,        // ponto médio da transição 0..1
    double GradSpread = 1.0,     // "velocidade": 1 = suave no span todo; 0.2 = transição rápida
    string? RivePath = null,     // camada RIVE (.riv) — animada pelo runtime C# custom
    string? RiveAnim = null,     // nome da animação Rive a tocar (null = a 1ª)
    double RiveW = 400, double RiveH = 400,   // caixa de render da camada Rive
    string? LottiePath = null,   // camada LOTTIE (.json bodymovin) — runtime C# custom
    double LottieW = 400, double LottieH = 400,   // caixa de render da camada Lottie
    double AnchorX = 0, double AnchorY = 0,       // ponto-âncora (pivô de rotação/escala), coords locais
    string? Parent = null,       // PARENTING: nome da camada-mãe (transform relativo à mãe)
    bool Controller = false);    // NULL object: só transform (controlador), não desenha

/// <summary>Extrusão 3D de uma camada: profundidade + bevel (unidades de mundo ~ px/220).
/// A rotação Y usa o track Rotation da camada; luz/câmara vêm do Comp.</summary>
public sealed record Extrude3D(double Depth = 0.5, double Bevel = 0.07);

/// <summary>Câmara 3D REAL e animável do comp — posição, alvo e FOV são Tracks keyframáveis.
/// Defaults: eye (0,0,5.2), target (0,0,0), fov 34°.</summary>
public sealed record CameraRig(
    Track? X = null, Track? Y = null, Track? Z = null,
    Track? Tx = null, Track? Ty = null, Track? Tz = null,
    Track? Fov = null);

/// <summary>A composition: canvas + timing + layers. BackgroundArgb = 0xAARRGGBB.</summary>
public sealed record Comp(
    int Width,
    int Height,
    double Fps,
    double Duration,
    uint BackgroundArgb,
    IReadOnlyList<Layer> Layers,
    uint? BackgroundArgb2 = null,    // if set → vertical gradient background
    CameraRig? Camera = null);       // câmara 3D animável (para camadas ThreeD)
