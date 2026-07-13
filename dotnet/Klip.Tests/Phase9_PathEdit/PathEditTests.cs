using System;
using System.Collections.Generic;
using System.Text;
using Klip.Engine;
using Klip.Tests.Framework;
using SkiaSharp;

namespace Klip.Tests.Phase9_PathEdit;

/// <summary>Fase 9 — edição de NÓS de path (SVG editável). Geometria PURA: round-trip, subdivisão
/// de Casteljau que NÃO deforma, mover/apagar nós, espelho de handles, RDP.</summary>
public static class PathEditTests
{
    private static List<SKPoint> Sample(string d, int n)
    {
        using var p = SKPath.ParseSvgPathData(d);
        using var m = new SKPathMeasure(p, false);
        float len = m.Length;
        var pts = new List<SKPoint>(n);
        for (int i = 0; i < n; i++) { m.GetPosition(len * i / (n - 1), out var pt); pts.Add(pt); }
        return pts;
    }
    private static float Dist(SKPoint a, SKPoint b) { float dx = a.X - b.X, dy = a.Y - b.Y; return MathF.Sqrt(dx * dx + dy * dy); }

    [KlipTest(9, "path-edit: round-trip Parse↔ToSvgPathData preserva a forma (quadrado = 4 nós)")]
    public static void RoundTripPreservesShape()
    {
        var e = PathEdit.Parse(Shapes.Rect(150, 150));
        Assert.Equal(4, e.NodeCount, "quadrado → 4 nós");
        Assert.True(e.Contours.Count == 1 && e.Contours[0].Closed, "1 contorno fechado");
        var e2 = PathEdit.Parse(e.ToSvgPathData());
        Assert.Equal(4, e2.NodeCount, "re-parse mantém 4 nós (round-trip estável)");
    }

    [KlipTest(9, "path-edit: InsertNode (de Casteljau) subdivide SEM deformar a curva")]
    public static void InsertNodeDeCasteljauPreservesCurve()
    {
        var e = PathEdit.Parse("M -150 0 C -150 -83 -83 -150 0 -150");
        Assert.Equal(2, e.NodeCount, "curva cubic = 2 nós");
        var before = Sample(e.ToSvgPathData(), 128);
        e.InsertNode(0, 0.5);
        Assert.Equal(3, e.NodeCount, "InsertNode → 3 nós");
        var after = Sample(e.ToSvgPathData(), 128);
        float maxD = 0;
        for (int i = 0; i < before.Count; i++) maxD = MathF.Max(maxD, Dist(before[i], after[i]));
        Assert.Less(maxD, 1.0, $"subdividir NÃO deforma (desvio máx {maxD:0.###}px)");
    }

    [KlipTest(9, "path-edit: InsertNode num segmento RETO = ponto-médio exato (handles 0)")]
    public static void InsertNodeOnLineIsExactMidpoint()
    {
        var e = PathEdit.Parse(Shapes.Rect(100, 100));   // M-100-100 L100-100 L100 100 L-100 100 Z
        e.InsertNode(0, 0.5);   // meio da aresta node0(-100,-100)→node1(100,-100) = (0,-100)
        var nodes = e.Enumerate();
        bool found = false;
        foreach (var (_, _, nd) in nodes)
            if (Dist(nd.Point, new SKPoint(0, -100)) < 0.01f) { found = true; Assert.Less(MathF.Sqrt(nd.HandleOut.X * nd.HandleOut.X + nd.HandleOut.Y * nd.HandleOut.Y), 0.01, "handle out ≈ 0 (fica L)"); }
        Assert.True(found, "novo nó no ponto-médio exato (0,-100)");
        Assert.Equal(5, e.NodeCount, "4 → 5 nós");
    }

    [KlipTest(9, "path-edit: MoveNode desloca SÓ o nó certo")]
    public static void MoveNodeShiftsOnlyThatNode()
    {
        var e = PathEdit.Parse(Shapes.Rect(100, 100));
        var p0 = e.Enumerate()[0].node.Point;
        var p1 = e.Enumerate()[1].node.Point;
        e.MoveNode(0, 40, 40);
        var q0 = e.Enumerate()[0].node.Point;
        var q1 = e.Enumerate()[1].node.Point;
        Assert.Near(p0.X + 40, q0.X, 0.01, "nó 0 moveu +40 em X");
        Assert.Near(p0.Y + 40, q0.Y, 0.01, "nó 0 moveu +40 em Y");
        Assert.Near(p1.X, q1.X, 0.01, "nó 1 inalterado");
        Assert.Near(p1.Y, q1.Y, 0.01, "nó 1 inalterado");
    }

    [KlipTest(9, "path-edit: DeleteNode remove o nó e mantém os restantes")]
    public static void DeleteNodeDropsAnchorKeepsRest()
    {
        var e = PathEdit.Parse("M0 -100 L95 -31 L59 81 L-59 81 L-95 -31 Z");   // pentágono, 5 nós
        Assert.Equal(5, e.NodeCount, "pentágono = 5 nós");
        var keep = e.Enumerate()[3].node.Point;
        e.DeleteNode(2);
        Assert.Equal(4, e.NodeCount, "após delete = 4 nós");
        bool still = false;
        foreach (var (_, _, nd) in e.Enumerate()) if (Dist(nd.Point, keep) < 0.01f) still = true;
        Assert.True(still, "os nós não-apagados permanecem");
    }

    [KlipTest(9, "path-edit: SetHandle num nó Smooth espelha o handle oposto (colinear)")]
    public static void SetHandleSmoothMirrors()
    {
        var e = PathEdit.Parse("M -100 0 C -100 -55 -55 -100 0 -100 C 55 -100 100 -55 100 0");
        // nó do meio (índice 1) tem handleIn e handleOut; torna-o smooth e mexe no out
        e.SetNodeType(1, NodeType.Smooth);
        e.SetHandle(1, outgoing: true, 30, -10);
        var nd = e.Enumerate()[1].node;
        float cross = nd.HandleOut.X * nd.HandleIn.Y - nd.HandleOut.Y * nd.HandleIn.X;
        float dot = nd.HandleOut.X * nd.HandleIn.X + nd.HandleOut.Y * nd.HandleIn.Y;
        Assert.Less(MathF.Abs(cross), 5.0, "handles colineares (cross≈0)");
        Assert.Less(dot, 0, "handles em sentidos OPOSTOS (dot<0)");
    }

    [KlipTest(9, "path-edit: Simplify (RDP) reduz nós redundantes mantendo a forma")]
    public static void SimplifyReducesNodesKeepsArea()
    {
        // quadrado 'denso': 50 pontos por aresta
        var sb = new StringBuilder("M -100 -100 ");
        void Edge(float x0, float y0, float x1, float y1)
        { for (int i = 1; i <= 50; i++) { float t = i / 50f; sb.Append("L ").Append(x0 + (x1 - x0) * t).Append(' ').Append(y0 + (y1 - y0) * t).Append(' '); } }
        Edge(-100, -100, 100, -100); Edge(100, -100, 100, 100); Edge(100, 100, -100, 100); Edge(-100, 100, -100, -100);
        sb.Append('Z');
        var e = PathEdit.Parse(sb.ToString());
        Assert.Greater(e.NodeCount, 150, "polilinha densa tem muitos nós");
        e.Simplify(2.0);
        Assert.Less(e.NodeCount, 12, $"RDP colapsa para poucos nós (obtive {e.NodeCount})");
        Assert.Greater(e.NodeCount, 3, "mas mantém os cantos");
    }
}
