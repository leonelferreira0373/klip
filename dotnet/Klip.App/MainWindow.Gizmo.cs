using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Klip.Engine;
using Klip.Model;

namespace Klip.App;

/// <summary>
/// GIZMO 3D NO PRÓPRIO ELEMENTO — como no Blender: tocas no objeto e rodas ALI, sem ir a painel
/// nenhum. Três anéis concêntricos sobre o objeto: vermelho=X, verde=Y, azul=Z.
///
/// O Overlay não recebe cliques (é só pintura), por isso o hit-test é MATEMÁTICO: mede-se a
/// distância do rato ao centro e vê-se em que banda caiu. Sai mais preciso do que hit-testing de
/// controlos e não estraga a selecção normal.
///
/// Arrastar = pré-visualização viva sem sujar o histórico; ao largar, um único ponto de undo —
/// o mesmo contrato dos sliders do painel 3D.
/// </summary>
public partial class MainWindow : Window
{
    private const double GizZ = 1.00, GizY = 0.78, GizX = 0.56;   // raios relativos
    private const double GizBand = 11.0;                          // tolerância em píxeis de ecrã

    private int _gizAxis = -1;             // 0=X 1=Y 2=Z
    private double _gizStartAngle, _gizStartValue;
    private Ellipse?[] _gizRings = new Ellipse?[3];

    private static readonly string[] GizColor = { "#E0245E", "#3FBF6F", "#3D7BFF" };
    private static readonly string[] GizProp = { "rotation.x", "rotation.y", "rotation.z" };

    /// <summary>Centro e raio do gizmo em coordenadas de ECRÃ, ou null se não há objeto 3D selecionado.</summary>
    private (Point c, double r)? GizmoGeom()
    {
        if (_selected < 0 || _selected >= _layers.Count) return null;
        var l = _layers[_selected];
        if (l.ThreeD is null) return null;
        if (EngineExport.LayerBounds(l, W, H) is not { } r) return null;
        var tl = FromCanvas(r.x, r.y);
        var br = FromCanvas(r.x + r.w, r.y + r.h);
        var c = new Point((tl.X + br.X) / 2, (tl.Y + br.Y) / 2);
        double rad = Math.Max(34, Math.Max(Math.Abs(br.X - tl.X), Math.Abs(br.Y - tl.Y)) * 0.62);
        return (c, rad);
    }

    /// <summary>Desenha/actualiza os anéis. Chamado do UpdateOverlay.</summary>
    private void DrawGizmo()
    {
        var g = GizmoGeom();
        for (int i = 0; i < 3; i++)
        {
            if (_gizRings[i] is null)
            {
                var el = new Ellipse
                {
                    Stroke = new SolidColorBrush(Color.Parse(GizColor[i])),
                    StrokeThickness = 2.0, Fill = Brushes.Transparent, IsVisible = false, Opacity = 0.85,
                };
                _gizRings[i] = el;
                Overlay.Children.Add(el);
            }
            var ring = _gizRings[i]!;
            if (g is null) { ring.IsVisible = false; continue; }
            double rel = i == 0 ? GizX : i == 1 ? GizY : GizZ;
            double d = g.Value.r * rel * 2;
            ring.Width = d; ring.Height = d;
            ring.StrokeThickness = _gizAxis == i ? 3.4 : 2.0;      // o eixo em uso engrossa
            ring.Opacity = _gizAxis < 0 || _gizAxis == i ? 0.9 : 0.25;
            Avalonia.Controls.Canvas.SetLeft(ring, g.Value.c.X - d / 2);
            Avalonia.Controls.Canvas.SetTop(ring, g.Value.c.Y - d / 2);
            ring.IsVisible = true;
        }
    }

    /// <summary>Press: caiu em cima de um anel? Devolve true se começou a rodar (e a selecção não muda).</summary>
    private bool GizmoPress(Point screen)
    {
        _gizAxis = -1;
        if (GizmoGeom() is not { } g) return false;
        double dx = screen.X - g.c.X, dy = screen.Y - g.c.Y;
        double dist = Math.Sqrt(dx * dx + dy * dy);

        int best = -1; double bestErr = GizBand;
        for (int i = 0; i < 3; i++)
        {
            double rel = i == 0 ? GizX : i == 1 ? GizY : GizZ;
            double err = Math.Abs(dist - g.r * rel);
            if (err < bestErr) { bestErr = err; best = i; }
        }
        if (best < 0) return false;

        _gizAxis = best;
        _gizStartAngle = Math.Atan2(dy, dx) * 180.0 / Math.PI;
        var l = _layers[_selected];
        _gizStartValue = best switch
        {
            0 => l.RotationX?.Eval(_previewT) ?? 0,
            1 => l.RotationY?.Eval(_previewT) ?? 0,
            _ => l.RotationZ?.Eval(_previewT) ?? 0,
        };
        return true;
    }

    /// <summary>Move: roda o eixo agarrado. True se consumiu o evento.</summary>
    private bool GizmoMove(Point screen)
    {
        if (_gizAxis < 0) return false;
        if (GizmoGeom() is not { } g) return false;
        double ang = Math.Atan2(screen.Y - g.c.Y, screen.X - g.c.X) * 180.0 / Math.PI;
        double delta = ang - _gizStartAngle;
        while (delta > 180) delta -= 360;
        while (delta < -180) delta += 360;

        double v = _gizStartValue + delta;
        if (_snap) v = Math.Round(v / 15.0) * 15.0;              // com snap ligado, de 15 em 15 graus
        Live3D(GizProp[_gizAxis], v, cam: false);
        DrawGizmo();
        if (Inspector is not null)
            Inspector.Text = $"{GizProp[_gizAxis]}  {v:0}°" + (_snap ? "   (snap 15°)" : "");
        return true;
    }

    /// <summary>Largou: um só ponto de undo.</summary>
    private void GizmoRelease()
    {
        if (_gizAxis < 0) return;
        _gizAxis = -1;
        Commit3D();
        DrawGizmo();
    }
}
