using System.Linq;
using Klip.Engine;
using Klip.Tests.Framework;
using SkiaSharp;

namespace Klip.Tests.Phase9_PathEdit;

/// <summary>Fase 9 — import de SVG: &lt;path&gt; + transforms (translate/scale) + fill + recentragem.</summary>
public static class ImportSvgTests
{
    [KlipTest(9, "svg-import: parseia paths, aplica transform (2x+offset) e lê fill; CenterAll recentra")]
    public static void ParsesPathsTransformFill()
    {
        const string svg =
            "<svg><path d=\"M0 0 L10 0 L10 10 Z\" fill=\"#FF8800\"/>" +
            "<path d=\"M0 0 L5 0\" transform=\"translate(10,20) scale(2)\"/></svg>";
        var paths = SvgImport.ImportPaths(svg);
        Assert.Equal(2, paths.Count, "2 paths importados");
        Assert.True(paths[0].FillArgb == 0xFFFF8800u, $"fill #FF8800 → 0xFFFF8800 (obtive {paths[0].FillArgb:X8})");

        // ponto (5,0) → scale(2)=(10,0) → translate(10,20)=(20,20)
        using var p = SKPath.ParseSvgPathData(paths[1].D);
        var pts = p.Points;
        var last = pts[pts.Length - 1];
        Assert.Near(20, last.X, 0.5, "5*2+10 = 20 em X");
        Assert.Near(20, last.Y, 0.5, "0*2+20 = 20 em Y");

        var centered = SvgImport.CenterAll(paths);
        var b = SvgImport.UnionBounds(centered.Select(x => x.D));
        Assert.Near(0, (b.Left + b.Right) / 2f, 0.6, "CenterAll → centro X ≈ 0");
        Assert.Near(0, (b.Top + b.Bottom) / 2f, 0.6, "CenterAll → centro Y ≈ 0");
    }
}
