using System;
using System.Globalization;
using System.Text;

namespace Klip.Engine;

/// <summary>Builds SVG path "d" strings for primitive shapes, all CENTERED AT LOCAL (0,0)
/// (the layer transform places them). Sizes chosen comparable so morphs read cleanly.</summary>
public static class Shapes
{
    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Poly(IEnumerable<(double x, double y)> pts)
    {
        var sb = new StringBuilder();
        bool first = true;
        foreach (var (x, y) in pts)
        {
            sb.Append(first ? 'M' : 'L').Append(F(x)).Append(' ').Append(F(y)).Append(' ');
            first = false;
        }
        return sb.Append('Z').ToString();
    }

    public static string Star(double outer = 300, double inner = 125, int points = 5)
    {
        var list = new List<(double, double)>();
        int n = points * 2;
        for (int i = 0; i < n; i++)
        {
            double r = (i % 2 == 0) ? outer : inner;
            double ang = -Math.PI / 2 + i * Math.PI / points;
            list.Add((r * Math.Cos(ang), r * Math.Sin(ang)));
        }
        return Poly(list);
    }

    public static string Circle(double r = 230, int seg = 96)
    {
        var list = new List<(double, double)>();
        for (int i = 0; i < seg; i++)
        {
            double ang = 2 * Math.PI * i / seg;
            list.Add((r * Math.Cos(ang), r * Math.Sin(ang)));
        }
        return Poly(list);
    }

    /// <summary>A horizontal capsule (a "line"/bar with rounded ends).</summary>
    public static string Capsule(double halfLen = 300, double radius = 28, int cap = 12)
    {
        var list = new List<(double, double)>();
        // right cap: from top to bottom around the right end
        for (int i = 0; i <= cap; i++)
        {
            double a = -Math.PI / 2 + Math.PI * i / cap;
            list.Add((halfLen + radius * Math.Cos(a), radius * Math.Sin(a)));
        }
        // left cap: from bottom to top around the left end
        for (int i = 0; i <= cap; i++)
        {
            double a = Math.PI / 2 + Math.PI * i / cap;
            list.Add((-halfLen + radius * Math.Cos(a), radius * Math.Sin(a)));
        }
        return Poly(list);
    }

    /// <summary>A plus / cross.</summary>
    public static string Cross(double arm = 250, double thick = 90)
    {
        double a = arm, t = thick;
        return Poly(new (double, double)[]
        {
            (-t, -a), (t, -a), (t, -t), (a, -t), (a, t), (t, t),
            (t, a), (-t, a), (-t, t), (-a, t), (-a, -t), (-t, -t),
        });
    }

    public static string Rect(double halfW, double halfH)
        => Poly(new (double, double)[] { (-halfW, -halfH), (halfW, -halfH), (halfW, halfH), (-halfW, halfH) });

    /// <summary>Superellipse/squircle |x/a|^n + |y/b|^n = 1 (n≈4-5 = the premium Apple look).</summary>
    public static string Superellipse(double a, double b, double n = 4.2, int seg = 96)
    {
        var list = new List<(double, double)>();
        for (int i = 0; i < seg; i++)
        {
            double t = 2 * Math.PI * i / seg;
            double c = Math.Cos(t), s = Math.Sin(t);
            double x = a * Math.Sign(c) * Math.Pow(Math.Abs(c), 2.0 / n);
            double y = b * Math.Sign(s) * Math.Pow(Math.Abs(s), 2.0 / n);
            list.Add((x, y));
        }
        return Poly(list);
    }

    public static string Ellipse(double rx, double ry, int seg = 72)
    {
        var list = new List<(double, double)>();
        for (int i = 0; i < seg; i++)
        {
            double a = 2 * Math.PI * i / seg;
            list.Add((rx * Math.Cos(a), ry * Math.Sin(a)));
        }
        return Poly(list);
    }

    /// <summary>A trapezoid (cup body): wider at top, centered horizontally at 0.</summary>
    public static string Trapezoid(double halfTop, double halfBot, double top, double bot)
        => Poly(new (double, double)[] { (-halfTop, top), (halfTop, top), (halfBot, bot), (-halfBot, bot) });

    /// <summary>A cup handle: a thick "C" opening to the LEFT (toward the cup), centered at 0,0.</summary>
    public static string Handle(double ro = 118, double ri = 74, double spanDeg = 150, int seg = 22)
    {
        double h = spanDeg * Math.PI / 360.0; // half span in rad
        var list = new List<(double, double)>();
        for (int i = 0; i <= seg; i++)      // outer arc, top -> bottom (0 = pointing +x/right)
        {
            double a = -h + 2 * h * i / seg;
            list.Add((ro * Math.Cos(a), ro * Math.Sin(a)));
        }
        for (int i = seg; i >= 0; i--)      // inner arc back
        {
            double a = -h + 2 * h * i / seg;
            list.Add((ri * Math.Cos(a), ri * Math.Sin(a)));
        }
        return Poly(list);
    }

    /// <summary>A cartoon side-view car (facing right) with cabin + two wheel bumps, centered at 0,0.</summary>
    public static string Car(double scale = 1.0)
    {
        var pts = new (double, double)[]
        {
            (-320, 40), (-320, -30), (-215, -30), (-175, -135), (-15, -135), (55, -30),
            (300, -30), (335, 15), (335, 40),
            (250, 40), (250, 92), (205, 112), (160, 92), (160, 40),          // front wheel bump
            (-120, 40), (-120, 92), (-165, 112), (-210, 92), (-210, 40),     // rear wheel bump
        };
        var list = new List<(double, double)>();
        foreach (var (x, y) in pts) list.Add((x * scale, y * scale));
        return Poly(list);
    }
}
