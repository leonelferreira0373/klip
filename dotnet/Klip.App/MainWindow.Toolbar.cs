using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Klip.Model;

namespace Klip.App;

/// <summary>
/// BARRA CONTEXTUAL estilo Canva: flutua no topo da tela e mostra APENAS o que faz sentido
/// para o que está selecionado (texto → fonte/tamanho/cor · forma → preenchimento/contorno ·
/// camada 3D → material). Sem seleção, desaparece — é isso que a faz sentir contextual.
/// Reconstruída em cada Refresh (via UpdateInspector), como o resto da app.
/// </summary>
public partial class MainWindow : Window
{
    private static readonly uint[] CtxSwatches =
    {
        0xFF232326, 0xFFFFFFFF, 0xFF6D5EF6, 0xFFFF5A5F, 0xFFF5B82E,
        0xFF12B5A5, 0xFF0A84FF, 0xFFE0245E, 0xFF8A8A87, 0xFFE8C36A,
    };
    private static readonly string[] CtxFonts =
    {
        "Segoe UI", "Arial", "Georgia", "Times New Roman", "Impact",
        "Consolas", "Verdana", "Trebuchet MS", "Manrope",
    };

    private void SyncCtxBar()
    {
        if (CtxBar is null || CtxStack is null) return;
        try
        {
            if (_selected < 0 || _selected >= _layers.Count) { CtxBar.IsVisible = false; return; }
            var l = _layers[_selected];
            string id = l.Key;
            bool isText = _textMeta.ContainsKey(l.Name);
            bool is3d = l.ThreeD is not null;

            CtxStack.Children.Clear();

            // ---- TEXTO: fonte + tamanho ----
            if (isText)
            {
                CtxStack.Children.Add(FontPicker(id));
                CtxStack.Children.Add(Sep());
                CtxStack.Children.Add(Stepper("Tamanho", () =>
                {
                    double s = l.Scale?.Eval(_previewT) ?? 1.0;
                    return s;
                }, v => SafeCtx(() => ApiSetProp(id, "scale", v.ToString("0.###", CultureInfo.InvariantCulture))), 0.1, 0.1, 8));
                CtxStack.Children.Add(Sep());
            }

            // ---- COR (todas as camadas desenhadas) ----
            CtxStack.Children.Add(ColorButton("Cor", l.FillColor?.Eval(_previewT) ?? l.FillArgb,
                argb => SafeCtx(() => ApiSetFill(id, Hex(argb), null, false))));

            if (!is3d)
            {
                CtxStack.Children.Add(ColorButton("Contorno", l.StrokeArgb ?? 0xFF232326,
                    argb => SafeCtx(() => ApiSetStroke(id, Hex(argb), l.StrokeWidth > 0 ? l.StrokeWidth : 4))));
            }

            // ---- 3D: presets de material + atalho para o painel ----
            if (is3d)
            {
                CtxStack.Children.Add(Sep());
                foreach (var (nome, r, m) in new[] { ("Cromado", 0.05, 1.0), ("Ouro", 0.32, 1.0), ("Plástico", 0.22, 0.0), ("Mate", 0.75, 0.0) })
                    CtxStack.Children.Add(Chip(nome, () => SafeCtx(() => ApiSetMaterial(id, r, m))));
                CtxStack.Children.Add(Chip("Painel 3D →", () => ShowTab("3d")));
            }

            // ---- opacidade ----
            CtxStack.Children.Add(Sep());
            CtxStack.Children.Add(Stepper("Opacidade", () => l.Opacity?.Eval(_previewT) ?? 1.0,
                v => SafeCtx(() => ApiSetProp(id, "opacity", v.ToString("0.##", CultureInfo.InvariantCulture))), 0.1, 0, 1));

            // ---- aponta-e-instrui + sugestões inteligentes ----
            AddSuggestions(l);

            // ---- ações rápidas ----
            CtxStack.Children.Add(Sep());
            CtxStack.Children.Add(IconBtn("⧉", "Duplicar", () => OnDuplicate(null, new RoutedEventArgs())));
            CtxStack.Children.Add(IconBtn("↑", "Trazer para a frente", () => OnLayerUp(null, new RoutedEventArgs())));
            CtxStack.Children.Add(IconBtn("↓", "Enviar para trás", () => OnLayerDown(null, new RoutedEventArgs())));
            CtxStack.Children.Add(IconBtn("🗑", "Apagar", () => OnDelete(null, new RoutedEventArgs())));

            CtxBar.IsVisible = true;
        }
        catch { try { CtxBar.IsVisible = false; } catch { } }
    }

    private void SafeCtx(Action act)
    {
        try { act(); }
        catch (Exception ex) { if (Inspector is not null) Inspector.Text = ex.Message; }
    }

    private static string Hex(uint argb) => "#" + (argb & 0x00FFFFFF).ToString("X6");

    // ---------- peças ----------
    private static Control Sep() => new Border
    {
        Width = 1, Height = 18, Background = new SolidColorBrush(Color.Parse("#ECECEA")),
        Margin = new Thickness(3, 0), VerticalAlignment = VerticalAlignment.Center,
    };

    private static Button BaseBtn(string content, string tip)
    {
        var b = new Button
        {
            Content = content, FontSize = 11, Height = 26, Padding = new Thickness(8, 0),
            CornerRadius = new CornerRadius(7), Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), Foreground = new SolidColorBrush(Color.Parse("#3A3A38")),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        if (tip.Length > 0) ToolTip.SetTip(b, tip);
        return b;
    }

    private static Control IconBtn(string glyph, string tip, Action act)
    {
        var b = BaseBtn(glyph, tip);
        b.Width = 28; b.Padding = new Thickness(0);
        b.HorizontalContentAlignment = HorizontalAlignment.Center;
        b.Click += (_, _) => act();
        return b;
    }

    private static Control Chip(string label, Action act)
    {
        var b = BaseBtn(label, "");
        b.FontSize = 10.5;
        b.Background = new SolidColorBrush(Color.Parse("#F4F4F2"));
        b.Click += (_, _) => act();
        return b;
    }

    /// <summary>Botão de cor: mostra a cor atual e abre um flyout de amostras.</summary>
    private Control ColorButton(string label, uint argb, Action<uint> pick)
    {
        var swatch = new Border
        {
            Width = 16, Height = 16, CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.FromUInt32(argb)),
            BorderBrush = new SolidColorBrush(Color.Parse("#DDDDDA")), BorderThickness = new Thickness(1),
        };
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(swatch);
        content.Children.Add(new TextBlock { Text = label, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });

        var btn = BaseBtn("", label);
        btn.Content = content;

        var grid = new Avalonia.Controls.Primitives.UniformGrid { Columns = 5, Width = 150, Margin = new Thickness(4) };
        foreach (var c in CtxSwatches)
        {
            var cell = new Button
            {
                Width = 26, Height = 26, Margin = new Thickness(2), CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Color.FromUInt32(c)), BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.Parse("#DDDDDA")), Padding = new Thickness(0),
            };
            uint captured = c;
            cell.Click += (_, _) => { pick(captured); btn.Flyout?.Hide(); };
            grid.Children.Add(cell);
        }
        btn.Flyout = new Flyout { Content = grid, Placement = PlacementMode.Bottom };
        return btn;
    }

    /// <summary>Escolher fonte — re-bake do texto para outlines via ApiSetFont.</summary>
    private Control FontPicker(string id)
    {
        var btn = BaseBtn(_defaultFamily, "Tipo de letra");
        btn.Background = new SolidColorBrush(Color.Parse("#F4F4F2"));
        var st = new StackPanel { Width = 190, Margin = new Thickness(3), Spacing = 1 };
        foreach (var fam in CtxFonts)
        {
            var it = new Button
            {
                Content = fam, FontSize = 12, Height = 28, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left, Background = Brushes.Transparent,
                BorderThickness = new Thickness(0), Padding = new Thickness(8, 0), CornerRadius = new CornerRadius(6),
                FontFamily = new FontFamily(fam),
            };
            string captured = fam;
            it.Click += (_, _) =>
            {
                SafeCtx(() => ApiSetFont(id, captured));
                btn.Content = captured;
                btn.Flyout?.Hide();
            };
            st.Children.Add(it);
        }
        btn.Flyout = new Flyout { Content = new ScrollViewer { MaxHeight = 260, Content = st }, Placement = PlacementMode.Bottom };
        return btn;
    }

    /// <summary>Rótulo + − valor + — passos discretos, sem arrastar (é uma barra, não um painel).</summary>
    private static Control Stepper(string label, Func<double> get, Action<double> set, double step, double min, double max)
    {
        var val = new TextBlock
        {
            Text = get().ToString("0.##", CultureInfo.InvariantCulture), FontSize = 11,
            MinWidth = 30, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.Parse("#3A3A38")),
        };
        var st = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        st.Children.Add(new TextBlock
        {
            Text = label, FontSize = 11, Margin = new Thickness(2, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.Parse("#6B6B68")),
        });

        void Bump(double dir)
        {
            double v = Math.Clamp(Math.Round((get() + dir * step) / step) * step, min, max);
            val.Text = v.ToString("0.##", CultureInfo.InvariantCulture);
            set(v);
        }
        var minus = BaseBtn("−", ""); minus.Width = 24; minus.Padding = new Thickness(0);
        minus.HorizontalContentAlignment = HorizontalAlignment.Center;
        minus.Click += (_, _) => Bump(-1);
        var plus = BaseBtn("+", ""); plus.Width = 24; plus.Padding = new Thickness(0);
        plus.HorizontalContentAlignment = HorizontalAlignment.Center;
        plus.Click += (_, _) => Bump(+1);

        st.Children.Add(minus); st.Children.Add(val); st.Children.Add(plus);
        return st;
    }
}
