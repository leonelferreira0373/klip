using System;

namespace Klip.Engine.Rive;

/// <summary>
/// Advances a RiveArtboard's LinearAnimation to time t (seconds) by writing interpolated keyframe
/// values back into each keyed object's property bag, so RiveRenderer draws the animated frame.
/// Supports hold/linear/cubic interpolation and oneShot/loop/pingPong.
/// </summary>
public sealed class RivePlayer
{
    private readonly RiveArtboard _ab;
    public RivePlayer(RiveArtboard ab) => _ab = ab;

    public RiveAnimation? Find(string? name)
    {
        if (_ab.Animations.Count == 0) return null;
        if (string.IsNullOrEmpty(name)) return _ab.Animations[0];
        foreach (var a in _ab.Animations)
            if (string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) return a;
        return _ab.Animations[0];
    }

    public void Apply(RiveAnimation anim, double seconds)
    {
        double frame = ResolveFrame(anim, seconds);
        foreach (var ko in anim.KeyedObjects)
        {
            if (ko.ObjectId < 0 || ko.ObjectId >= _ab.Objects.Count) continue;
            var target = _ab.Objects[ko.ObjectId];
            foreach (var kp in ko.Properties)
            {
                if (kp.KeyFrames.Count == 0) continue;
                target.Props[kp.PropertyKey] = Sample(kp, frame);
            }
        }
    }

    private static double ResolveFrame(RiveAnimation a, double seconds)
    {
        double frame = seconds * a.Fps * a.Speed;
        double dur = a.DurationFrames;
        if (dur <= 0) return 0;
        switch (a.LoopValue)
        {
            case 1: // loop
                frame %= dur; if (frame < 0) frame += dur; return frame;
            case 2: // pingPong
                double m = frame % (2 * dur); if (m < 0) m += 2 * dur;
                return m <= dur ? m : 2 * dur - m;
            default: // oneShot
                return Math.Clamp(frame, 0, dur);
        }
    }

    private static object Sample(RiveKeyedProperty kp, double frame)
    {
        var kfs = kp.KeyFrames;
        if (frame <= kfs[0].Frame) return ValueOf(kfs[0]);
        if (frame >= kfs[^1].Frame) return ValueOf(kfs[^1]);

        int i = 0;
        while (i < kfs.Count - 1 && kfs[i + 1].Frame <= frame) i++;
        var a = kfs[i]; var b = kfs[i + 1];
        double span = b.Frame - a.Frame;
        double u = span <= 0 ? 0 : (frame - a.Frame) / span;

        // interpolation shaping (a.InterpolationType governs the a→b segment)
        double e = a.InterpolationType switch
        {
            0 => 0,                                    // hold
            2 when a.Cubic is { Length: 4 } c => CubicBezier(u, c[0], c[1], c[2], c[3]),
            _ => u,                                    // linear
        };

        if (a.IsColor && b.IsColor)
            return LerpColor(a.ColorValue, b.ColorValue, e);
        return a.Value + (b.Value - a.Value) * e;
    }

    private static object ValueOf(RiveKeyFrame k) => k.IsColor ? (object)k.ColorValue : k.Value;

    private static uint LerpColor(uint c0, uint c1, double t)
    {
        byte L(int sh) => (byte)Math.Round(((c0 >> sh) & 0xFF) + (((c1 >> sh) & 0xFF) - (double)((c0 >> sh) & 0xFF)) * t);
        return ((uint)L(24) << 24) | ((uint)L(16) << 16) | ((uint)L(8) << 8) | L(0);
    }

    /// <summary>Cubic-bezier easing y for progress u, control points (x1,y1),(x2,y2) — Newton solve for x.</summary>
    private static double CubicBezier(double u, double x1, double y1, double x2, double y2)
    {
        if (u <= 0) return 0; if (u >= 1) return 1;
        double t = u;
        for (int i = 0; i < 8; i++)
        {
            double x = Bez(t, x1, x2) - u;
            double dx = BezD(t, x1, x2);
            if (Math.Abs(x) < 1e-5) break;
            if (Math.Abs(dx) < 1e-9) break;
            t -= x / dx;
            t = Math.Clamp(t, 0, 1);
        }
        return Bez(t, y1, y2);
    }
    private static double Bez(double t, double a, double b)
    { double mt = 1 - t; return 3 * mt * mt * t * a + 3 * mt * t * t * b + t * t * t; }
    private static double BezD(double t, double a, double b)
    { double mt = 1 - t; return 3 * mt * mt * a + 6 * mt * t * (b - a) + 3 * t * t * (1 - b); }
}
