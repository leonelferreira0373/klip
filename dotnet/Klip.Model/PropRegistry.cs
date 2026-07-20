using System;
using System.Collections.Generic;
using System.Linq;

namespace Klip.Model;

/// <summary>Uma linha por propriedade animável — o "property bag" realizado como TABELA ESTÁTICA
/// (índice virtual). Get*/Set* fecham sobre os campos tipados do Layer e reconstroem via `with`.</summary>
public sealed record PropDescriptor(
    string Path, ChannelKind Kind,
    Func<Layer, Track?>? GetScalar, Func<Layer, Track?, Layer>? SetScalar,
    Func<Layer, ColorTrack?>? GetColor, Func<Layer, ColorTrack?, Layer>? SetColor,
    Func<Layer, uint>? ColorSeed, double ScalarDefault = 0);

/// <summary>Gerador de IDs estáveis de camada (prefixo ly_ → nunca colide com Names externos).</summary>
public static class Ids
{
    public static string Next() => "ly_" + Guid.NewGuid().ToString("N")[..8];
}

/// <summary>
/// Sistema de propriedades UNIFORME: toda propriedade animável (incl. COR) endereçada por
/// (layer, propPath) e mutada da MESMA maneira. Puro Klip.Model — sem Skia/Avalonia. É a
/// fundação da paridade AE, do controlo por IA e do aponta-e-instrui.
/// </summary>
public static class PropRegistry
{
    private static PropDescriptor Scal(string p, Func<Layer, Track?> g, Func<Layer, Track?, Layer> s, double def = 0)
        => new(p, ChannelKind.Scalar, g, s, null, null, null, def);
    private static PropDescriptor Col(string p, Func<Layer, ColorTrack?> g, Func<Layer, ColorTrack?, Layer> s, Func<Layer, uint> seed)
        => new(p, ChannelKind.Color, null, null, g, s, seed);

    /// <summary>Gradiente da camada, criado LAZY a partir do par legado — é o que permite keyframar
    /// uma paragem numa camada que ainda nunca teve gradiente nenhum.</summary>
    private static GradientSpec Grad(Layer l)
        => l.FillGradient ?? GradientSpec.Seed(l.FillArgb, l.FillArgb2, l.FillRadial, l.GradAngle);

    private static PropDescriptor StopCol(int i) => Col($"gradient.stop{i}.color",
        l => l.FillGradient is { } g && i < g.Stops.Count ? g.Stops[i].Color : null,
        (l, c) => l with { FillGradient = Grad(l).WithStopColor(i, c) },
        l => l.FillGradient is { } g && i < g.Stops.Count ? g.Stops[i].EvalArgb(0) : l.FillArgb);

    private static PropDescriptor StopPos(int i, double def) => Scal($"gradient.stop{i}.pos",
        l => l.FillGradient is { } g && i < g.Stops.Count ? g.Stops[i].Offset : null,
        (l, t) => l with { FillGradient = Grad(l).WithStopOffset(i, t) }, def);

    /// <summary>Superset de TODAS as props animáveis (corrige o gap onde scale_x/scale_y/skew_x não davam kf).</summary>
    public static readonly IReadOnlyDictionary<string, PropDescriptor> Descriptors = new Dictionary<string, PropDescriptor>
    {
        ["position.x"] = Scal("position.x", l => l.PosX, (l, t) => l with { PosX = t }),
        ["position.y"] = Scal("position.y", l => l.PosY, (l, t) => l with { PosY = t }),
        ["rotation"]   = Scal("rotation",   l => l.Rotation, (l, t) => l with { Rotation = t }),
        ["rotation.x"] = Scal("rotation.x", l => l.RotationX, (l, t) => l with { RotationX = t }),   // tilt 3D perspetiva
        ["rotation.y"] = Scal("rotation.y", l => l.RotationY, (l, t) => l with { RotationY = t }),
        // ---- Product Studio: profundidade + roll, para camadas 3D REAIS (Extrude3D) ----
        ["position.z"] = Scal("position.z", l => l.PosZ, (l, t) => l with { PosZ = t }),             // profundidade (perspetiva/DoF)
        ["rotation.z"] = Scal("rotation.z", l => l.RotationZ, (l, t) => l with { RotationZ = t }),   // roll no plano da face
        ["scale"]      = Scal("scale",      l => l.Scale, (l, t) => l with { Scale = t }, 1),
        ["scale.x"]    = Scal("scale.x",    l => l.ScaleX, (l, t) => l with { ScaleX = t }, 1),
        ["scale.y"]    = Scal("scale.y",    l => l.ScaleY, (l, t) => l with { ScaleY = t }, 1),
        ["skew.x"]     = Scal("skew.x",     l => l.SkewX, (l, t) => l with { SkewX = t }),
        ["blur"]       = Scal("blur",       l => l.BlurRadius, (l, t) => l with { BlurRadius = t }),
        ["opacity"]    = Scal("opacity",    l => l.Opacity, (l, t) => l with { Opacity = t }, 1),
        ["trim.start"] = Scal("trim.start", l => l.TrimStart, (l, t) => l with { TrimStart = t }),
        ["trim.end"]   = Scal("trim.end",   l => l.TrimEnd, (l, t) => l with { TrimEnd = t }, 1),
        ["anchor.x"]   = Scal("anchor.x",   l => null, (l, _) => l),   // âncora estática hoje (unifica na Fase 5)
        ["color.fill"]   = Col("color.fill",   l => l.FillColor,   (l, c) => l with { FillColor = c },   l => l.FillColor?.Eval(0) ?? l.FillArgb),
        ["color.stroke"] = Col("color.stroke", l => l.StrokeColor, (l, c) => l with { StrokeColor = c }, l => l.StrokeColor?.Eval(0) ?? l.StrokeArgb ?? 0xFF000000u),
        ["color.fill2"]  = Col("color.fill2",  l => l.FillColor2,  (l, c) => l with { FillColor2 = c },  l => l.FillColor2?.Eval(0) ?? l.FillArgb2 ?? l.FillArgb),
        // ---- Gradiente multi-stop: geometria E cada paragem endereçáveis e keyframáveis ----
        ["gradient.angle"]    = Scal("gradient.angle",    l => l.FillGradient?.Angle,   (l, t) => l with { FillGradient = Grad(l) with { Angle = t } }, 90),
        ["gradient.center.x"] = Scal("gradient.center.x", l => l.FillGradient?.CenterX, (l, t) => l with { FillGradient = Grad(l) with { CenterX = t } }, 0.5),
        ["gradient.center.y"] = Scal("gradient.center.y", l => l.FillGradient?.CenterY, (l, t) => l with { FillGradient = Grad(l) with { CenterY = t } }, 0.5),
        ["gradient.radius"]   = Scal("gradient.radius",   l => l.FillGradient?.Radius,  (l, t) => l with { FillGradient = Grad(l) with { Radius = t } }, 0.62),
        ["gradient.stop0.color"] = StopCol(0), ["gradient.stop0.pos"] = StopPos(0, 0.00),
        ["gradient.stop1.color"] = StopCol(1), ["gradient.stop1.pos"] = StopPos(1, 1.00),
        ["gradient.stop2.color"] = StopCol(2), ["gradient.stop2.pos"] = StopPos(2, 0.50),
        ["gradient.stop3.color"] = StopCol(3), ["gradient.stop3.pos"] = StopPos(3, 0.50),
        ["gradient.stop4.color"] = StopCol(4), ["gradient.stop4.pos"] = StopPos(4, 0.50),
        ["gradient.stop5.color"] = StopCol(5), ["gradient.stop5.pos"] = StopPos(5, 0.50),
        ["gradient.stop6.color"] = StopCol(6), ["gradient.stop6.pos"] = StopPos(6, 0.50),
        ["gradient.stop7.color"] = StopCol(7), ["gradient.stop7.pos"] = StopPos(7, 0.50),
        // ---- Fase 7: efeitos premium (spec-mãe criado LAZY ao 1º keyframe, como AddKeyframe semeia Tracks) ----
        ["glow.radius"]    = Scal("glow.radius",    l => l.Glow?.Radius,        (l, t) => l with { Glow = (l.Glow ?? new GlowSpec()) with { Radius = t } }),
        ["glow.intensity"] = Scal("glow.intensity", l => l.Glow?.Intensity,     (l, t) => l with { Glow = (l.Glow ?? new GlowSpec()) with { Intensity = t } }, 1),
        ["shadow.dx"]      = Scal("shadow.dx",      l => l.DropShadow?.Dx,      (l, t) => l with { DropShadow = (l.DropShadow ?? new ShadowSpec()) with { Dx = t } }),
        ["shadow.dy"]      = Scal("shadow.dy",      l => l.DropShadow?.Dy,      (l, t) => l with { DropShadow = (l.DropShadow ?? new ShadowSpec()) with { Dy = t } }),
        ["shadow.blur"]    = Scal("shadow.blur",    l => l.DropShadow?.Blur,    (l, t) => l with { DropShadow = (l.DropShadow ?? new ShadowSpec()) with { Blur = t } }),
        ["shadow.opacity"] = Scal("shadow.opacity", l => l.DropShadow?.Opacity, (l, t) => l with { DropShadow = (l.DropShadow ?? new ShadowSpec()) with { Opacity = t } }, 1),
        ["motion.blur"]    = Scal("motion.blur",    l => l.MotionBlur,          (l, t) => l with { MotionBlur = t }),
        ["glow.color"]   = Col("glow.color",   l => l.Glow?.Color,       (l, c) => l with { Glow = (l.Glow ?? new GlowSpec()) with { Color = c } },             l => l.Glow?.Color?.Eval(0) ?? l.FillColor?.Eval(0) ?? l.FillArgb),
        ["shadow.color"] = Col("shadow.color", l => l.DropShadow?.Color, (l, c) => l with { DropShadow = (l.DropShadow ?? new ShadowSpec()) with { Color = c } }, l => l.DropShadow?.Color?.Eval(0) ?? 0xFF000000u),
        // ---- Fase 10: emissor de partículas (keyframar cria o ParticleSpec LAZY, molde glow.*/shadow.*) ----
        ["particles.rate"]         = Scal("particles.rate",         l => l.Particles?.Rate,          (l, t) => l with { Particles = (l.Particles ?? new ParticleSpec()) with { Rate = t } }, 30),
        ["particles.lifetime"]     = Scal("particles.lifetime",     l => l.Particles?.Lifetime,      (l, t) => l with { Particles = (l.Particles ?? new ParticleSpec()) with { Lifetime = t } }, 1.0),
        ["particles.speed"]        = Scal("particles.speed",        l => l.Particles?.Speed,         (l, t) => l with { Particles = (l.Particles ?? new ParticleSpec()) with { Speed = t } }, 120),
        ["particles.gravity"]      = Scal("particles.gravity",      l => l.Particles?.Gravity,       (l, t) => l with { Particles = (l.Particles ?? new ParticleSpec()) with { Gravity = t } }, 0),
        ["particles.spread"]       = Scal("particles.spread",       l => l.Particles?.SpreadDeg,     (l, t) => l with { Particles = (l.Particles ?? new ParticleSpec()) with { SpreadDeg = t } }, 30),
        ["particles.spawn_radius"] = Scal("particles.spawn_radius", l => l.Particles?.SpawnRadius,   (l, t) => l with { Particles = (l.Particles ?? new ParticleSpec()) with { SpawnRadius = t } }, 0),
        ["particles.scale"]        = Scal("particles.scale",        l => l.Particles?.ParticleScale, (l, t) => l with { Particles = (l.Particles ?? new ParticleSpec()) with { ParticleScale = t } }, 1),
    };

    /// <summary>Alias legado → path canónico (mantém TODOS os verbos MCP atuais da IA a funcionar).</summary>
    private static readonly IReadOnlyDictionary<string, string> Alias = new Dictionary<string, string>
    {
        ["x"] = "position.x", ["y"] = "position.y", ["pos_x"] = "position.x", ["pos_y"] = "position.y",
        ["scale_x"] = "scale.x", ["scale_y"] = "scale.y", ["skew_x"] = "skew.x",
        ["rotate_x"] = "rotation.x", ["rotate_y"] = "rotation.y", ["rot_x"] = "rotation.x", ["rot_y"] = "rotation.y",
        ["z"] = "position.z", ["pos_z"] = "position.z", ["depth"] = "position.z",
        ["rotate_z"] = "rotation.z", ["rot_z"] = "rotation.z", ["roll"] = "rotation.z",
        ["trim_start"] = "trim.start", ["trim_end"] = "trim.end",
        ["fill"] = "color.fill", ["stroke"] = "color.stroke", ["fill2"] = "color.fill2", ["anchor_x"] = "anchor.x",
        // Fase 7 (NÃO mapear "shadow" nu → colidiria com o bool Shadow legado; usar shadow_blur como knob principal)
        ["glow_radius"] = "glow.radius", ["glow"] = "glow.radius", ["glow_intensity"] = "glow.intensity", ["glow_color"] = "glow.color",
        ["shadow_dx"] = "shadow.dx", ["shadow_dy"] = "shadow.dy", ["shadow_blur"] = "shadow.blur",
        ["shadow_opacity"] = "shadow.opacity", ["shadow_color"] = "shadow.color",
        ["motion_blur"] = "motion.blur", ["mblur"] = "motion.blur", ["shutter"] = "motion.blur",
        // Fase 10 — emissor
        ["rate"] = "particles.rate", ["emitter_rate"] = "particles.rate", ["emit_rate"] = "particles.rate",
        ["lifetime"] = "particles.lifetime", ["life"] = "particles.lifetime",
        ["speed"] = "particles.speed", ["gravity"] = "particles.gravity", ["grav"] = "particles.gravity",
        ["spread"] = "particles.spread", ["spawn_radius"] = "particles.spawn_radius", ["particle_scale"] = "particles.scale",
        // Gradiente ("spread" já está tomado pelas partículas — não reutilizar)
        ["grad_angle"] = "gradient.angle", ["gradient_angle"] = "gradient.angle",
        ["grad_cx"] = "gradient.center.x", ["grad_cy"] = "gradient.center.y", ["grad_radius"] = "gradient.radius",
    };

    public static string Canonical(string p)
        => Descriptors.ContainsKey(p) ? p : (Alias.TryGetValue(p, out var c) ? c : p);

    public static bool TryGet(string pathOrAlias, out PropDescriptor d)
        => Descriptors.TryGetValue(Canonical(pathOrAlias), out d!);

    private static PropDescriptor Req(string path)
        => TryGet(path, out var d) ? d : throw new ArgumentException($"propriedade desconhecida: {path}");

    public static PropValue GetValue(Layer l, string path, double t)
    {
        var d = Req(path);
        return d.Kind == ChannelKind.Scalar
            ? PropValue.Of(d.GetScalar!(l)?.Eval(t) ?? d.ScalarDefault)
            : PropValue.Of(d.GetColor!(l)?.Eval(t) ?? d.ColorSeed!(l));
    }

    public static Layer SetStatic(Layer l, string path, PropValue v)
    {
        var d = Req(path);
        return d.Kind == ChannelKind.Scalar
            ? d.SetScalar!(l, Track.Const(v.Scalar))
            : d.SetColor!(l, ColorTrack.Const(v.Argb));
    }

    public static Layer AddKeyframe(Layer l, string path, double time, PropValue v, Easing ease = Easing.Linear, double[]? bez = null)
    {
        var d = Req(path);
        if (d.Kind == ChannelKind.Scalar)
        {
            var cur = d.GetScalar!(l);
            var keys = cur?.Keys.ToList() ?? new List<Keyframe>();
            Upsert(keys, new Keyframe(time, v.Scalar, ease, bez), k => k.Time);
            return d.SetScalar!(l, new Track(keys, cur?.Expr));
        }
        else
        {
            var cur = d.GetColor!(l);
            var keys = cur?.Keys.ToList() ?? new List<ColorKey>();
            if (keys.Count == 0 && time > 1e-6)
                keys.Add(new ColorKey(0, d.ColorSeed!(l)));   // semeia a 1ª kf da cor atual → sem salto visual
            Upsert(keys, new ColorKey(time, v.Argb, ease, bez), k => k.Time);
            return d.SetColor!(l, new ColorTrack(keys, cur?.Expr));
        }
    }

    public static Layer RemoveKeyframe(Layer l, string path, double time)
    {
        var d = Req(path);
        if (d.Kind == ChannelKind.Scalar)
        {
            var cur = d.GetScalar!(l); if (cur is null) return l;
            var keys = cur.Keys.Where(k => Math.Abs(k.Time - time) > 1e-6).ToList();
            return d.SetScalar!(l, keys.Count > 0 ? new Track(keys, cur.Expr) : null);
        }
        else
        {
            var cur = d.GetColor!(l); if (cur is null) return l;
            var keys = cur.Keys.Where(k => Math.Abs(k.Time - time) > 1e-6).ToList();
            return d.SetColor!(l, keys.Count > 0 ? new ColorTrack(keys, cur.Expr) : null);
        }
    }

    public static Layer SetExpression(Layer l, string path, TrackExpr? expr)
    {
        var d = Req(path);
        if (d.Kind == ChannelKind.Color)
        {
            if (expr is { Kind: ExprKind.Code }) throw new InvalidOperationException("expressões-código de cor chegam na Fase 3");
            var cur = d.GetColor!(l) ?? ColorTrack.Const(d.ColorSeed!(l));
            return d.SetColor!(l, cur with { Expr = expr });
        }
        var t = d.GetScalar!(l) ?? Track.Const(d.ScalarDefault);
        return d.SetScalar!(l, t with { Expr = expr });
    }

    public static IReadOnlyList<(string path, ChannelKind kind, bool animated)> Describe(Layer l)
        => Descriptors.Values.Select(d => (d.Path, d.Kind,
                d.Kind == ChannelKind.Scalar ? (d.GetScalar!(l)?.Keys.Count ?? 0) > 1 : (d.GetColor!(l)?.Keys.Count ?? 0) > 1))
            .ToList();

    /// <summary>Resolve camada por Id (primeiro) ou Name (fallback) — retro-compat total.</summary>
    public static int Find(IReadOnlyList<Layer> layers, string idOrName)
    {
        for (int i = 0; i < layers.Count; i++) if (layers[i].Id == idOrName) return i;
        for (int i = 0; i < layers.Count; i++) if (layers[i].Name == idOrName) return i;
        return -1;
    }

    private static void Upsert<T>(List<T> keys, T kf, Func<T, double> time)
    {
        double kt = time(kf);
        int ix = keys.FindIndex(k => Math.Abs(time(k) - kt) < 1e-6);
        if (ix >= 0) keys[ix] = kf; else keys.Add(kf);
        keys.Sort((a, b) => time(a).CompareTo(time(b)));
    }
}
