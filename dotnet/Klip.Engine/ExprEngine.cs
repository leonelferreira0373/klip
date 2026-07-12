using System;
using System.Runtime.CompilerServices;
using Jint;
using Klip.Model;

namespace Klip.Engine;

/// <summary>
/// Motor de EXPRESSÕES estilo After Effects — as expressões SÃO JavaScript (Jint, puro C#, single-file OK).
/// Cada propriedade pode ter código JS cujo valor final é o resultado, avaliado por-frame. Scope AE:
///   value, time, wiggle(freq,amp), valueAtTime(t), loopOut(), linear(...), ease/easeIn/easeOut(...),
///   clamp(v,min,max), random(), degreesToRadians/radiansToDegrees, + Math.* nativo.
/// Ligado a Track.CodeEval por um ModuleInitializer — nada a fazer no arranque.
/// </summary>
public static class ExprEngine
{
    [ModuleInitializer]
    internal static void Init() => Track.CodeEval = Eval;

    [ThreadStatic] private static Jint.Engine? _engine;
    [ThreadStatic] private static Random? _rng;

    /// <summary>Avalia a expressão da propriedade em t. Erros → cai no valor dos keyframes (nunca rebenta).</summary>
    public static double Eval(string code, Track track, double t)
    {
        var e = _engine ??= new Jint.Engine(o => o.TimeoutInterval(TimeSpan.FromMilliseconds(50)).LimitRecursion(64));
        _rng ??= new Random(0x5EED);
        double value = track.KeyframesAt(t);

        e.SetValue("time", t);
        e.SetValue("value", value);
        e.SetValue("valueAtTime", (Func<double, double>)(x => track.KeyframesAt(x)));
        e.SetValue("wiggle", (Func<double, double, double>)((freq, amp) => value + amp * Noise(freq * t + 0.123)));
        e.SetValue("clamp", (Func<double, double, double, double>)((v, lo, hi) => Math.Clamp(v, lo, hi)));
        e.SetValue("linear", (Func<double, double, double, double, double, double>)Linear);
        e.SetValue("ease", (Func<double, double, double, double, double, double>)Ease);
        e.SetValue("easeIn", (Func<double, double, double, double, double, double>)EaseIn);
        e.SetValue("easeOut", (Func<double, double, double, double, double, double>)EaseOut);
        e.SetValue("random", (Func<double>)(() => _rng!.NextDouble()));
        e.SetValue("degreesToRadians", (Func<double, double>)(d => d * Math.PI / 180.0));
        e.SetValue("radiansToDegrees", (Func<double, double>)(r => r * 180.0 / Math.PI));
        e.SetValue("loopOut", (Func<double>)(() => LoopOut(track, t)));
        e.SetValue("loopIn", (Func<double>)(() => LoopIn(track, t)));

        var r = e.Evaluate(code);
        return r.IsNumber() ? r.AsNumber() : value;
    }

    // AE: linear(t, tMin, tMax, value1, value2) — mapeia t∈[tMin,tMax] → [v1,v2] (clamped).
    private static double Linear(double t, double tMin, double tMax, double v1, double v2)
    {
        if (tMax == tMin) return t < tMin ? v1 : v2;
        double u = Math.Clamp((t - tMin) / (tMax - tMin), 0, 1);
        return v1 + (v2 - v1) * u;
    }

    private static double Ease(double t, double tMin, double tMax, double v1, double v2)
        => Shape(t, tMin, tMax, v1, v2, u => u * u * (3 - 2 * u));            // smoothstep (in-out)
    private static double EaseIn(double t, double tMin, double tMax, double v1, double v2)
        => Shape(t, tMin, tMax, v1, v2, u => u * u);                          // quad-in
    private static double EaseOut(double t, double tMin, double tMax, double v1, double v2)
        => Shape(t, tMin, tMax, v1, v2, u => 1 - (1 - u) * (1 - u));          // quad-out

    private static double Shape(double t, double tMin, double tMax, double v1, double v2, Func<double, double> f)
    {
        if (tMax == tMin) return t < tMin ? v1 : v2;
        double u = Math.Clamp((t - tMin) / (tMax - tMin), 0, 1);
        return v1 + (v2 - v1) * f(u);
    }

    private static double LoopOut(Track track, double t)
    {
        var k = track.Keys;
        if (k.Count < 2) return track.KeyframesAt(t);
        double t0 = k[0].Time, t1 = k[^1].Time, span = t1 - t0;
        if (span <= 0 || t <= t1) return track.KeyframesAt(t);
        return track.KeyframesAt(t0 + (t - t0) % span);
    }

    private static double LoopIn(Track track, double t)
    {
        var k = track.Keys;
        if (k.Count < 2) return track.KeyframesAt(t);
        double t0 = k[0].Time, t1 = k[^1].Time, span = t1 - t0;
        if (span <= 0 || t >= t0) return track.KeyframesAt(t);
        double d = (t0 - t) % span;
        return track.KeyframesAt(t1 - d);
    }

    // ruído de valor suave em [-1,1] (para wiggle) — determinístico
    private static double Noise(double x)
    {
        int i = (int)Math.Floor(x);
        double f = x - i, s = f * f * (3 - 2 * f);
        return Hash(i) + (Hash(i + 1) - Hash(i)) * s;
    }
    private static double Hash(int n)
    {
        n = (n << 13) ^ n;
        return 1.0 - ((n * (n * n * 15731 + 789221) + 1376312589) & 0x7fffffff) / 1073741824.0;
    }
}
