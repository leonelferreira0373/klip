using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SkiaSharp;

namespace Klip.Engine;

public enum NodeType { Corner, Smooth }

/// <summary>Um nó de path: ponto on-curve + handles bezier como VETORES-OFFSET relativos ao ponto
/// (control absoluto = Point+Handle). Reto ⇔ HandleOut(A)≈0 && HandleIn(B)≈0.</summary>
public struct PathNode
{
    public SKPoint Point, HandleIn, HandleOut;
    public NodeType Type;
}

public sealed class PathContour { public List<PathNode> Nodes = new(); public bool Closed; }

/// <summary>
/// Modelo EDITÁVEL de um path SVG ao nível dos NÓS (Illustrator-style). Parse de um "d" (a forma
/// canónica de uma MorphKey) → nós; edita (mover/inserir/apagar/handles/simplificar) → re-serializa.
/// Puro (SkiaSharp+System). Tudo normalizado para cubic-bezier internamente.
/// </summary>
public sealed class PathEdit
{
    public List<PathContour> Contours = new();
    private const float EPS = 1e-3f;

    public int NodeCount { get { int n = 0; foreach (var c in Contours) n += c.Nodes.Count; return n; } }

    // ---- vetor helpers (sem depender dos operadores de SKPoint) ----
    private static SKPoint Add(SKPoint a, SKPoint b) => new(a.X + b.X, a.Y + b.Y);
    private static SKPoint Sub(SKPoint a, SKPoint b) => new(a.X - b.X, a.Y - b.Y);
    private static SKPoint Mul(SKPoint a, float s) => new(a.X * s, a.Y * s);
    private static SKPoint Lerp(SKPoint a, SKPoint b, float t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
    private static float Len(SKPoint a) => MathF.Sqrt(a.X * a.X + a.Y * a.Y);
    private static float Dist(SKPoint a, SKPoint b) => Len(Sub(a, b));
    private static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private (int c, int local) Locate(int i)
    {
        for (int c = 0; c < Contours.Count; c++)
        {
            if (i < Contours[c].Nodes.Count) return (c, i);
            i -= Contours[c].Nodes.Count;
        }
        throw new ArgumentOutOfRangeException(nameof(i), "índice de nó fora de alcance");
    }

    // ================= PARSE =================
    public static PathEdit Parse(string d)
    {
        var edit = new PathEdit();
        using var p = SKPath.ParseSvgPathData(d);
        if (p is null) return edit;
        using var it = p.CreateRawIterator();
        var pts = new SKPoint[4];
        PathContour? cur = null;
        SKPathVerb v;
        while ((v = it.Next(pts)) != SKPathVerb.Done)
        {
            switch (v)
            {
                case SKPathVerb.Move:
                    cur = new PathContour();
                    edit.Contours.Add(cur);
                    cur.Nodes.Add(new PathNode { Point = pts[0], Type = NodeType.Corner });
                    break;
                case SKPathVerb.Line:
                    cur?.Nodes.Add(new PathNode { Point = pts[1], Type = NodeType.Corner });
                    break;
                case SKPathVerb.Quad:
                    if (cur is not null)
                    {
                        var c1 = Add(pts[0], Mul(Sub(pts[1], pts[0]), 2f / 3f));
                        var c2 = Add(pts[2], Mul(Sub(pts[1], pts[2]), 2f / 3f));
                        AddCubic(cur, c1, c2, pts[2]);
                    }
                    break;
                case SKPathVerb.Conic:
                    if (cur is not null)
                    {
                        var quads = SKPath.ConvertConicToQuads(pts[0], pts[1], pts[2], it.ConicWeight(), 1);
                        for (int qi = 0; qi + 2 < quads.Length; qi += 2)
                        {
                            var a = quads[qi]; var ctrl = quads[qi + 1]; var end = quads[qi + 2];
                            AddCubic(cur, Add(a, Mul(Sub(ctrl, a), 2f / 3f)), Add(end, Mul(Sub(ctrl, end), 2f / 3f)), end);
                        }
                    }
                    break;
                case SKPathVerb.Cubic:
                    if (cur is not null) AddCubic(cur, pts[1], pts[2], pts[3]);
                    break;
                case SKPathVerb.Close:
                    if (cur is not null) { cur.Closed = true; MergeCloseDup(cur); }
                    break;
            }
        }
        return edit;
    }

    private static void AddCubic(PathContour c, SKPoint c1, SKPoint c2, SKPoint end)
    {
        var last = c.Nodes[^1];
        last.HandleOut = Sub(c1, last.Point);
        c.Nodes[^1] = last;
        c.Nodes.Add(new PathNode { Point = end, HandleIn = Sub(c2, end), Type = NodeType.Corner });
    }

    // funde o nó final duplicado (linha explícita de volta ao início) com o inicial
    private static void MergeCloseDup(PathContour c)
    {
        if (c.Nodes.Count > 1 && Dist(c.Nodes[^1].Point, c.Nodes[0].Point) < EPS)
        {
            var first = c.Nodes[0];
            first.HandleIn = c.Nodes[^1].HandleIn;
            c.Nodes[0] = first;
            c.Nodes.RemoveAt(c.Nodes.Count - 1);
        }
    }

    // ================= SERIALIZE =================
    public string ToSvgPathData()
    {
        var sb = new StringBuilder();
        foreach (var c in Contours)
        {
            if (c.Nodes.Count == 0) continue;
            sb.Append('M').Append(F(c.Nodes[0].Point.X)).Append(' ').Append(F(c.Nodes[0].Point.Y)).Append(' ');
            int n = c.Nodes.Count, segs = c.Closed ? n : n - 1;
            for (int s = 0; s < segs; s++)
            {
                var A = c.Nodes[s];
                var B = c.Nodes[(s + 1) % n];
                if (Len(A.HandleOut) < EPS && Len(B.HandleIn) < EPS)
                    sb.Append('L').Append(F(B.Point.X)).Append(' ').Append(F(B.Point.Y)).Append(' ');
                else
                {
                    var c1 = Add(A.Point, A.HandleOut); var c2 = Add(B.Point, B.HandleIn);
                    sb.Append('C').Append(F(c1.X)).Append(' ').Append(F(c1.Y)).Append(' ')
                      .Append(F(c2.X)).Append(' ').Append(F(c2.Y)).Append(' ')
                      .Append(F(B.Point.X)).Append(' ').Append(F(B.Point.Y)).Append(' ');
                }
            }
            if (c.Closed) sb.Append("Z ");
        }
        return sb.ToString().Trim();
    }

    // ================= OPERAÇÕES =================
    public PathEdit MoveNode(int i, double dx, double dy)
    {
        var (ci, li) = Locate(i);
        var node = Contours[ci].Nodes[li];
        node.Point = new SKPoint(node.Point.X + (float)dx, node.Point.Y + (float)dy);
        Contours[ci].Nodes[li] = node;
        return this;
    }

    /// <summary>Subdivide o segmento que SAI do nó afterI em t∈(0,1) — de Casteljau, SEM deformar.</summary>
    public PathEdit InsertNode(int afterI, double t)
    {
        var (ci, li) = Locate(afterI);
        var c = Contours[ci];
        int n = c.Nodes.Count;
        int bi = li + 1;
        if (bi >= n) { if (!c.Closed) throw new InvalidOperationException("não há segmento a seguir ao último nó de um contorno aberto"); bi = 0; }
        float ft = (float)Math.Clamp(t, 1e-4, 1 - 1e-4);
        var A = c.Nodes[li]; var B = c.Nodes[bi];

        if (Len(A.HandleOut) < EPS && Len(B.HandleIn) < EPS)   // segmento reto → ponto-médio limpo
        {
            var mid = Lerp(A.Point, B.Point, ft);
            c.Nodes.Insert(bi == 0 ? n : bi, new PathNode { Point = mid, Type = NodeType.Corner });
            return this;
        }

        SKPoint p0 = A.Point, p1 = Add(A.Point, A.HandleOut), p2 = Add(B.Point, B.HandleIn), p3 = B.Point;
        SKPoint a1 = Lerp(p0, p1, ft), b1 = Lerp(p1, p2, ft), c1 = Lerp(p2, p3, ft);
        SKPoint dd = Lerp(a1, b1, ft), ee = Lerp(b1, c1, ft), ff = Lerp(dd, ee, ft);

        A.HandleOut = Sub(a1, p0); c.Nodes[li] = A;
        B.HandleIn = Sub(c1, p3); c.Nodes[bi] = B;
        var mid2 = new PathNode { Point = ff, HandleIn = Sub(dd, ff), HandleOut = Sub(ee, ff), Type = NodeType.Smooth };
        c.Nodes.Insert(bi == 0 ? n : bi, mid2);
        return this;
    }

    public PathEdit DeleteNode(int i)
    {
        var (ci, li) = Locate(i);
        var c = Contours[ci];
        c.Nodes.RemoveAt(li);
        int min = c.Closed ? 3 : 2;
        if (c.Nodes.Count < min) Contours.RemoveAt(ci);
        if (NodeCount == 0) throw new InvalidOperationException("apagar deixaria o path vazio");
        return this;
    }

    public PathEdit SetHandle(int i, bool outgoing, double dx, double dy, bool? mirror = null)
    {
        var (ci, li) = Locate(i);
        var node = Contours[ci].Nodes[li];
        var vec = new SKPoint((float)dx, (float)dy);
        if (outgoing) node.HandleOut = vec; else node.HandleIn = vec;
        bool doMirror = mirror ?? (node.Type == NodeType.Smooth);
        if (doMirror)
        {
            var opp = outgoing ? node.HandleIn : node.HandleOut;
            float oppLen = Len(opp);
            if (oppLen > EPS && Len(vec) > EPS)
            {
                var dir = Mul(vec, -1f / Len(vec));
                var newOpp = Mul(dir, oppLen);
                if (outgoing) node.HandleIn = newOpp; else node.HandleOut = newOpp;
            }
        }
        Contours[ci].Nodes[li] = node;
        return this;
    }

    public PathEdit SetNodeType(int i, NodeType type)
    {
        var (ci, li) = Locate(i);
        var node = Contours[ci].Nodes[li];
        if (type == NodeType.Smooth && node.Type == NodeType.Corner)
        {
            // força colinearidade média dos dois handles
            var outv = node.HandleOut; var inv = node.HandleIn;
            if (Len(outv) > EPS && Len(inv) > EPS)
            {
                var avg = new SKPoint((outv.X - inv.X) / 2f, (outv.Y - inv.Y) / 2f);
                float al = Len(avg);
                if (al > EPS)
                {
                    var dir = Mul(avg, 1f / al);
                    node.HandleOut = Mul(dir, Len(outv));
                    node.HandleIn = Mul(dir, -Len(inv));
                }
            }
        }
        node.Type = type;
        Contours[ci].Nodes[li] = node;
        return this;
    }

    /// <summary>Ramer-Douglas-Peucker sobre os pontos on-curve — limpa o output denso do trace/roto.</summary>
    public PathEdit Simplify(double tolerance)
    {
        float tol = (float)Math.Max(0.01, tolerance);
        foreach (var c in Contours)
        {
            var pts = new List<SKPoint>(c.Nodes.Count);
            foreach (var nd in c.Nodes) pts.Add(nd.Point);
            List<SKPoint> kept;
            if (c.Closed && pts.Count > 3)
            {
                // âncora = ponto mais distante do primeiro; parte em dois arcos
                int far = 0; float best = -1;
                for (int k = 1; k < pts.Count; k++) { float dd = Dist(pts[0], pts[k]); if (dd > best) { best = dd; far = k; } }
                var arc1 = pts.GetRange(0, far + 1);
                var arc2 = pts.GetRange(far, pts.Count - far); arc2.Add(pts[0]);
                var k1 = Rdp(arc1, tol); var k2 = Rdp(arc2, tol);
                kept = new List<SKPoint>(k1);
                for (int k = 1; k < k2.Count - 1; k++) kept.Add(k2[k]);   // evita duplicar âncora/fecho
            }
            else kept = Rdp(pts, tol);

            c.Nodes.Clear();
            foreach (var pt in kept) c.Nodes.Add(new PathNode { Point = pt, Type = NodeType.Corner });
        }
        return this;
    }

    private static List<SKPoint> Rdp(List<SKPoint> pts, float tol)
    {
        if (pts.Count < 3) return new List<SKPoint>(pts);
        SKPoint a = pts[0], b = pts[^1];
        float dx = b.X - a.X, dy = b.Y - a.Y; float segLen = MathF.Sqrt(dx * dx + dy * dy);
        int idx = -1; float maxD = -1;
        for (int k = 1; k < pts.Count - 1; k++)
        {
            float d = segLen < 1e-6f ? Dist(pts[k], a)
                : MathF.Abs(dy * (pts[k].X - a.X) - dx * (pts[k].Y - a.Y)) / segLen;
            if (d > maxD) { maxD = d; idx = k; }
        }
        if (maxD <= tol) return new List<SKPoint> { a, b };
        var left = Rdp(pts.GetRange(0, idx + 1), tol);
        var right = Rdp(pts.GetRange(idx, pts.Count - idx), tol);
        var outp = new List<SKPoint>(left);
        for (int k = 1; k < right.Count; k++) outp.Add(right[k]);
        return outp;
    }

    public IReadOnlyList<(int i, int contour, PathNode node)> Enumerate()
    {
        var list = new List<(int, int, PathNode)>();
        int i = 0;
        for (int c = 0; c < Contours.Count; c++)
            foreach (var nd in Contours[c].Nodes) list.Add((i++, c, nd));
        return list;
    }
}
