using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Klip.Model;

namespace Klip.App;

/// <summary>
/// PAINEL 3D (Product Studio): rodar / posicionar / materializar o OBJETO e conduzir a CÂMARA,
/// com keyframe (◆) em cada propriedade animável. Construído em código — a app é code-behind puro.
/// Slider a arrastar = preview vivo sem sujar o histórico; ao largar, commit único (undo limpo).
/// </summary>
public partial class MainWindow : Window
{
    private bool _p3dOpen, _p3dBuilt, _p3dSync;
    private readonly Dictionary<string, Slider> _p3dSl = new();
    private readonly Dictionary<string, TextBlock> _p3dVal = new();
    private TextBlock? _p3dWho;
    private Button? _p3dMakeBtn;

    private static readonly IBrush BrLabel = new SolidColorBrush(Color.Parse("#6B6B68"));
    private static readonly IBrush BrValue = new SolidColorBrush(Color.Parse("#3A3A38"));
    private static readonly IBrush BrHead = new SolidColorBrush(Color.Parse("#9A9A97"));
    private static readonly IBrush BrAccent = new SolidColorBrush(Color.Parse("#6D5EF6"));

    private void OnToggle3D(object? s, RoutedEventArgs e)
    {
        _p3dOpen = !_p3dOpen;
        if (_p3dOpen && !_p3dBuilt) { Build3DPanel(); _p3dBuilt = true; }
        Panel3D.IsVisible = _p3dOpen;
        P3DBtn.Foreground = new SolidColorBrush(Color.Parse(_p3dOpen ? "#6D5EF6" : "#5E5E5B"));
        if (_p3dOpen) Sync3DPanel();
    }

    // ---------- construção ----------
    private void Build3DPanel()
    {
        var st = Stack3D.Children;
        st.Clear();

        st.Add(Head3D("OBJETO 3D"));
        _p3dWho = new TextBlock { Text = "(sem seleção)", FontSize = 10.5, Foreground = BrHead, Margin = new Thickness(0, 0, 0, 3) };
        st.Add(_p3dWho);
        _p3dMakeBtn = new Button { Content = "Tornar 3D", FontSize = 10.5, Height = 22, Padding = new Thickness(8, 0), CornerRadius = new CornerRadius(6) };
        _p3dMakeBtn.Click += OnMake3D;
        st.Add(_p3dMakeBtn);

        st.Add(Row3D("rotation.x", "Rot X", -180, 180, kf: true));
        st.Add(Row3D("rotation.y", "Rot Y", -180, 180, kf: true));
        st.Add(Row3D("rotation.z", "Rot Z", -180, 180, kf: true));
        st.Add(Row3D("position.z", "Pos Z", -600, 600, kf: true));
        st.Add(Row3D("depth", "Profund.", 0.02, 2.0));
        st.Add(Row3D("bevel", "Bisel", 0.0, 0.30));

        st.Add(Head3D("MATERIAL"));
        st.Add(Row3D("rough", "Aspereza", 0.04, 1.0));
        st.Add(Row3D("metal", "Metal", 0.0, 1.0));
        var presets = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Margin = new Thickness(0, 2, 0, 0) };
        foreach (var (nome, r, m) in new[] { ("Cromado", 0.05, 1.0), ("Ouro", 0.32, 1.0), ("Plástico", 0.22, 0.0), ("Mate", 0.75, 0.0) })
        {
            var b = new Button { Content = nome, FontSize = 9.5, Height = 20, Padding = new Thickness(5, 0), CornerRadius = new CornerRadius(5) };
            b.Click += (_, _) => ApplyMaterialPreset(r, m);
            presets.Children.Add(b);
        }
        st.Add(presets);
        var tex = new Button { Content = "Textura da face…", FontSize = 10.5, Height = 22, Padding = new Thickness(8, 0), CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 3, 0, 0) };
        tex.Click += OnPickFaceTexture;
        st.Add(tex);

        st.Add(Head3D("CÂMARA"));
        st.Add(Row3D("cam.x", "Pos X", -8, 8, kf: true, cam: true));
        st.Add(Row3D("cam.y", "Pos Y", -8, 8, kf: true, cam: true));
        st.Add(Row3D("cam.z", "Distância", 0.5, 20, kf: true, cam: true));
        st.Add(Row3D("cam.tx", "Alvo X", -8, 8, kf: true, cam: true));
        st.Add(Row3D("cam.ty", "Alvo Y", -8, 8, kf: true, cam: true));
        st.Add(Row3D("cam.fov", "FOV", 8, 100, kf: true, cam: true));
        var camReset = new Button { Content = "Repor câmara", FontSize = 10.5, Height = 22, Padding = new Thickness(8, 0), CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 3, 0, 0) };
        camReset.Click += (_, _) => { _camTracks.Clear(); Refresh(); Sync3DPanel(); };
        st.Add(camReset);
    }

    private static TextBlock Head3D(string t) => new()
    {
        Text = t, FontSize = 9.5, FontWeight = FontWeight.Bold, Foreground = BrHead,
        Margin = new Thickness(0, 7, 0, 2),
    };

    /// <summary>Linha: rótulo · slider · valor · ◆(keyframe).</summary>
    private Control Row3D(string key, string label, double min, double max, bool kf = false, bool cam = false)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("46,*,32,18"), Height = 22 };

        var lb = new TextBlock { Text = label, FontSize = 10.5, Foreground = BrLabel, VerticalAlignment = VerticalAlignment.Center };
        Avalonia.Controls.Grid.SetColumn(lb, 0); g.Children.Add(lb);

        var sl = new Slider
        {
            Minimum = min, Maximum = max, Height = 22, Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0),
        };
        Avalonia.Controls.Grid.SetColumn(sl, 1); g.Children.Add(sl);
        _p3dSl[key] = sl;

        var vt = new TextBlock { FontSize = 10, Foreground = BrValue, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
        Avalonia.Controls.Grid.SetColumn(vt, 2); g.Children.Add(vt);
        _p3dVal[key] = vt;

        // preview vivo enquanto arrasta (sem histórico)
        sl.PropertyChanged += (_, ev) =>
        {
            if (ev.Property.Name != "Value" || _p3dSync) return;
            vt.Text = Fmt3D(key, sl.Value);
            Live3D(key, sl.Value, cam);
        };
        // ao largar → um commit só (undo limpo)
        sl.AddHandler(InputElement.PointerReleasedEvent, (_, _) => Commit3D(),
                      RoutingStrategies.Tunnel | RoutingStrategies.Bubble);

        if (kf)
        {
            var kb = new Button
            {
                Content = "◆", FontSize = 9, Width = 18, Height = 18, Padding = new Thickness(0),
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = BrHead,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            ToolTip.SetTip(kb, "Keyframe aqui (no tempo atual da timeline)");
            kb.Click += (_, _) => Keyframe3D(key, sl.Value, cam, kb);
            Avalonia.Controls.Grid.SetColumn(kb, 3);
            g.Children.Add(kb);
        }
        return g;
    }

    private static string Fmt3D(string key, double v) =>
        key is "rotation.x" or "rotation.y" or "rotation.z" or "cam.fov" ? Math.Round(v).ToString("0", CultureInfo.InvariantCulture)
        : key == "position.z" ? Math.Round(v).ToString("0", CultureInfo.InvariantCulture)
        : v.ToString("0.00", CultureInfo.InvariantCulture);

    // ---------- aplicar ----------
    private string? Sel3DKey() => _selected >= 0 && _selected < _layers.Count ? _layers[_selected].Key : null;

    /// <summary>Preview vivo: muda o modelo e re-renderiza, SEM tocar no histórico.</summary>
    private void Live3D(string key, double v, bool cam)
    {
        try
        {
            if (cam)
            {
                _camTracks[key.Substring(4)] = Track.Const(v);      // "cam.z" → "z"
            }
            else
            {
                if (_selected < 0 || _selected >= _layers.Count) return;
                var l = _layers[_selected];
                _layers[_selected] = key switch
                {
                    "depth" => l with { ThreeD = (l.ThreeD ?? new Extrude3D()) with { Depth = v } },
                    "bevel" => l with { ThreeD = (l.ThreeD ?? new Extrude3D()) with { Bevel = v } },
                    "rough" => l with { ThreeD = (l.ThreeD ?? new Extrude3D()) with { Rough = v } },
                    "metal" => l with { ThreeD = (l.ThreeD ?? new Extrude3D()) with { Metal = v } },
                    _ => PropRegistry.SetStatic(l, key, PropValue.Of(v)),
                };
            }
            InvalidateComp();
            RenderView(_previewT);
        }
        catch { /* preview nunca deve partir a UI */ }
    }

    /// <summary>Fim do arrasto: um único ponto de undo + refresh completo.</summary>
    private void Commit3D()
    {
        EnsureIds();
        PushHistory();
        Refresh();
    }

    private void Keyframe3D(string key, double v, bool cam, Button kb)
    {
        try
        {
            if (cam) ApiCameraKeyframe(key.Substring(4), _previewT, v, "ease_in_out", null);
            else
            {
                var id = Sel3DKey();
                if (id is null) { SetInspector3D("Seleciona um objeto primeiro."); return; }
                ApiSetKeyframe(id, key, _previewT, v.ToString(CultureInfo.InvariantCulture), "ease_in_out", null);
            }
            kb.Foreground = BrAccent;
            SetInspector3D($"◆ keyframe em {key} @ {_previewT:0.00}s");
        }
        catch (Exception ex) { SetInspector3D("keyframe falhou: " + ex.Message); }
    }

    private void ApplyMaterialPreset(double rough, double metal)
    {
        var id = Sel3DKey();
        if (id is null) { SetInspector3D("Seleciona um objeto primeiro."); return; }
        try { ApiSetMaterial(id, rough, metal); Sync3DPanel(); }
        catch (Exception ex) { SetInspector3D("material: " + ex.Message); }
    }

    private void OnMake3D(object? s, RoutedEventArgs e)
    {
        var id = Sel3DKey();
        if (id is null) { SetInspector3D("Seleciona um objeto primeiro."); return; }
        try
        {
            bool is3d = _layers[_selected].ThreeD is not null;
            ApiSet3D(id, is3d ? 0 : 0.5, 0.07);       // depth<=0 remove
            Sync3DPanel();
        }
        catch (Exception ex) { SetInspector3D("3D: " + ex.Message); }
    }

    private async void OnPickFaceTexture(object? s, RoutedEventArgs e)
    {
        var id = Sel3DKey();
        if (id is null) { SetInspector3D("Seleciona um objeto primeiro."); return; }
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = "Arte da FACE (frente) — png/jpg",
                AllowMultiple = false,
                FileTypeFilter = new[] { new Avalonia.Platform.Storage.FilePickerFileType("Imagem") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.webp" } } },
            });
            var f = files?.FirstOrDefault();
            if (f is null) return;
            var path = f.Path?.LocalPath;
            if (string.IsNullOrEmpty(path)) { SetInspector3D("ficheiro sem caminho local."); return; }
            if (_layers[_selected].ThreeD is null) ApiSet3D(id, 0.06, 0.006);   // cartão fino por defeito
            ApiSetFaceTexture(id, path, null, null);
            SetInspector3D("textura aplicada: " + System.IO.Path.GetFileName(path));
            Sync3DPanel();
        }
        catch (Exception ex) { SetInspector3D("textura: " + ex.Message); }
    }

    private void SetInspector3D(string msg)
    {
        if (Inspector is not null) Inspector.Text = msg;
    }

    // ---------- sincronizar UI ← modelo ----------
    private void Sync3DPanel()
    {
        if (!_p3dOpen || !_p3dBuilt) return;
        _p3dSync = true;
        try
        {
            var l = _selected >= 0 && _selected < _layers.Count ? _layers[_selected] : null;
            var t = l?.ThreeD;
            if (_p3dWho is not null)
                _p3dWho.Text = l is null ? "(sem seleção)" : l.Name + (t is null ? "  ·  2D" : "  ·  3D");
            if (_p3dMakeBtn is not null)
                _p3dMakeBtn.Content = t is null ? "Tornar 3D" : "Remover 3D";

            void Put(string k, double v) { if (_p3dSl.TryGetValue(k, out var sl)) { sl.Value = Math.Clamp(v, sl.Minimum, sl.Maximum); if (_p3dVal.TryGetValue(k, out var vt)) vt.Text = Fmt3D(k, sl.Value); } }

            Put("rotation.x", l?.RotationX?.Eval(_previewT) ?? 0);
            Put("rotation.y", l?.RotationY?.Eval(_previewT) ?? 0);
            Put("rotation.z", l?.RotationZ?.Eval(_previewT) ?? 0);
            Put("position.z", l?.PosZ?.Eval(_previewT) ?? 0);
            Put("depth", t?.Depth ?? 0.5);
            Put("bevel", t?.Bevel ?? 0.07);
            Put("rough", t?.Rough ?? 0.25);
            Put("metal", t?.Metal ?? 0.85);

            double Cam(string k, double def) => _camTracks.TryGetValue(k, out var tr) ? tr.Eval(_previewT) : def;
            Put("cam.x", Cam("x", 0)); Put("cam.y", Cam("y", 0)); Put("cam.z", Cam("z", 5.2));
            Put("cam.tx", Cam("tx", 0)); Put("cam.ty", Cam("ty", 0)); Put("cam.fov", Cam("fov", 34));
        }
        catch { /* nunca partir a UI por causa do painel */ }
        finally { _p3dSync = false; }
    }
}
