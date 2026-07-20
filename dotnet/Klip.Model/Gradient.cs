using System;
using System.Collections.Generic;
using System.Linq;

namespace Klip.Model;

/// <summary>Geometria do gradiente. Serializado como INT pelo STJ — NUNCA reordenar estes valores.</summary>
public enum GradKind { Linear = 0, Radial = 1, Conic = 2 }

/// <summary>
/// Uma paragem do gradiente. Argb/Pos são os valores estáticos (semente + reserva);
/// Color/Offset são as versões ANIMÁVEIS e mandam quando não são null — exactamente a mesma
/// precedência que FillColor tem sobre FillArgb.
/// </summary>
public sealed record GradStop(uint Argb, double Pos, ColorTrack? Color = null, Track? Offset = null)
{
    public uint EvalArgb(double t) => Color?.Eval(t) ?? Argb;
    public double EvalPos(double t) => Math.Clamp(Offset?.Eval(t) ?? Pos, 0.0, 1.0);
}

/// <summary>
/// Gradiente MULTI-STOP do preenchimento. Tudo o que é numérico é Track? → keyframável pelo
/// PropRegistry, tal como glow.* e shadow.*. Null em qualquer Track = valor neutro por omissão.
///
/// O par legado (FillArgb2 + FillRadial + GradAngle + GradMid + GradSpread) continua a funcionar
/// intacto; quando FillGradient existe, é este que manda.
/// </summary>
public sealed record GradientSpec(
    IReadOnlyList<GradStop> Stops,
    GradKind Kind = GradKind.Linear,
    Track? Angle = null,     // graus; null → 90 (topo→fundo). Linear e cónico.
    Track? CenterX = null,   // 0..1 fracção da caixa; null → 0.5. Radial e cónico.
    Track? CenterY = null,
    Track? Radius = null,    // 0..1 de max(w,h); null → 0.62 (igual ao legado)
    int Tile = 0)            // 0 clamp · 1 repeat · 2 mirror
{
    public const int MaxStops = 8;

    public double EvalAngle(double t) => Angle?.Eval(t) ?? 90.0;
    public double EvalCenterX(double t) => CenterX?.Eval(t) ?? 0.5;
    public double EvalCenterY(double t) => CenterY?.Eval(t) ?? 0.5;
    public double EvalRadius(double t) => Math.Max(1e-3, Radius?.Eval(t) ?? 0.62);

    /// <summary>Semente a partir do par legado — usada quando se keyframa um stop numa camada que ainda não tem gradiente.</summary>
    public static GradientSpec Seed(uint fill, uint? fill2, bool radial, double angle) => new(
        new[] { new GradStop(fill, 0.0), new GradStop(fill2 ?? fill, 1.0) },
        radial ? GradKind.Radial : GradKind.Linear,
        Math.Abs(angle - 90.0) < 1e-9 ? null : Track.Const(angle));

    /// <summary>Ordena por posição e limita a MaxStops. Chamar SEMPRE antes de gravar na camada.</summary>
    public GradientSpec Normalized()
    {
        var s = Stops.OrderBy(x => x.Pos).Take(MaxStops).ToList();
        if (s.Count == 0) s.Add(new GradStop(0xFF000000u, 0.0));
        if (s.Count == 1) s.Add(s[0] with { Pos = 1.0 });
        return this with { Stops = s };
    }

    public GradientSpec WithStopColor(int i, ColorTrack? c) => WithStop(i, s => s with { Color = c });
    public GradientSpec WithStopOffset(int i, Track? o) => WithStop(i, s => s with { Offset = o });

    /// <summary>Muda UMA paragem sem tocar nas outras; faz crescer a lista se o índice ainda não existe.</summary>
    public GradientSpec WithStop(int i, Func<GradStop, GradStop> f)
    {
        var list = Stops.ToList();
        while (list.Count <= i && list.Count < MaxStops)
            list.Add(new GradStop(list.Count > 0 ? list[^1].Argb : 0xFF000000u,
                                  list.Count == 0 ? 0.0 : 1.0));
        if (i < 0 || i >= list.Count) return this;
        list[i] = f(list[i]);
        return this with { Stops = list };
    }
}
