using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Klip.Model;

namespace Klip.App;

/// <summary>
/// APONTA-E-INSTRUI + SUGESTÕES INTELIGENTES.
///
/// Tocas num elemento → a barra contextual mostra (a) um campo para falar com a IA JÁ ancorado
/// nesse elemento, e (b) até 3 sugestões de 1 clique próprias do tipo dele.
///
/// As sugestões lêem o ESTADO, não são uma lista fixa: não te propõe "Cromado" se já está
/// cromado, nem "Contorno" se já tem. É isso que as torna úteis em vez de ruído.
/// </summary>
public partial class MainWindow : Window
{
    private sealed record Sug(string Label, string Tip, Action Do);

    /// <summary>Até 3 sugestões, escolhidas pelo tipo do elemento E pelo que lhe falta.</summary>
    private List<Sug> SuggestFor(Layer l)
    {
        string id = l.Key;
        var s = new List<Sug>();

        // ---------- OBJETO 3D ----------
        if (l.ThreeD is { } t3)
        {
            if (t3.Metal < 0.5)
                s.Add(new("Cromado", "Metal espelhado", () => ApiSetMaterial(id, 0.05, 1.0)));
            else if (t3.Rough > 0.2)
                s.Add(new("Espelho", "Baixar a aspereza ao máximo", () => ApiSetMaterial(id, 0.05, 1.0)));
            else
                s.Add(new("Ouro", "Metal quente acetinado", () => ApiSetMaterial(id, 0.32, 1.0)));

            if (string.IsNullOrEmpty(t3.FrontTex))
                s.Add(new("Arte na face", "Pôr uma imagem na face do objeto",
                    () => OnPickFaceTexture(null, new Avalonia.Interactivity.RoutedEventArgs())));

            bool spins = l.RotationY is { Keys.Count: > 1 };
            if (!spins)
                s.Add(new("Girar 360°", "Uma volta completa ao longo da timeline", () =>
                {
                    ApiSetKeyframe(id, "rotation.y", 0, "0", "linear", null);
                    ApiSetKeyframe(id, "rotation.y", Math.Max(1, _motionDur), "360", "linear", null);
                }));
            else
                s.Add(new("Mais profundidade", "Empurrar para trás na cena",
                    () => ApiSetProp(id, "position.z", "-120")));
            return Cap(s);
        }

        // ---------- IMAGEM ----------
        if (!string.IsNullOrEmpty(l.ImagePath))
        {
            s.Add(new("Remover fundo", "Recorta o motivo (ONNX, local)", () => ApiRemoveBackground(id)));
            s.Add(new("Extrair paleta", "Tira as cores dominantes para a tela",
                () => ApiExtractPalette(id, 0, 0)));
            if (l.DropShadow is null)
                s.Add(new("Sombra", "Descola a imagem do fundo",
                    () => { ApiSetProp(id, "shadow.blur", "26"); ApiSetProp(id, "shadow.dy", "10"); }));
            else
                s.Add(new("Em 3D", "Transformar em objeto com espessura", () => ApiSet3D(id, 0.06, 0.006)));
            return Cap(s);
        }

        // ---------- TEXTO ----------
        if (_textMeta.ContainsKey(l.Name))
        {
            double sc = l.Scale?.Eval(_previewT) ?? 1.0;
            s.Add(new("Maior", "Aumenta 25%",
                () => ApiSetProp(id, "scale", (sc * 1.25).ToString("0.###", CultureInfo.InvariantCulture))));
            if (l.StrokeWidth <= 0)
                s.Add(new("Contorno", "Destaca o texto do fundo", () => ApiSetStroke(id, "#232326", 5)));
            else
                s.Add(new("Sem contorno", "Tirar o contorno", () => ApiSetStroke(id, "#232326", 0)));
            if (l.Glow is null)
                s.Add(new("Brilho", "Glow suave por trás", () => ApiSetProp(id, "glow.radius", "18")));
            else
                s.Add(new("Cor de destaque", "Pintar com o roxo da marca",
                    () => ApiSetFill(id, "#6D5EF6", null, false)));
            return Cap(s);
        }

        // ---------- FORMA (default) ----------
        if (l.FillArgb2 is null)
            s.Add(new("Gradiente", "Duas cores em vez de uma",
                () => ApiSetFill(id, "#" + (l.FillArgb & 0x00FFFFFF).ToString("X6"), "#6D5EF6", false)));
        else
            s.Add(new("Radial", "Gradiente do centro para fora",
                () => ApiSetFill(id, "#" + (l.FillArgb & 0x00FFFFFF).ToString("X6"),
                                 "#" + ((l.FillArgb2 ?? 0) & 0x00FFFFFF).ToString("X6"), true)));
        if (l.Glow is null)
            s.Add(new("Brilho", "Glow aditivo", () => ApiSetProp(id, "glow.radius", "22")));
        s.Add(new("Em 3D", "Extrudir com bevel e material PBR", () => ApiSet3D(id, 0.5, 0.07)));
        return Cap(s);
    }

    private static List<Sug> Cap(List<Sug> s) => s.Count > 3 ? s.GetRange(0, 3) : s;

    /// <summary>Junta à barra: o campo de falar com a IA ancorado no elemento + as sugestões.</summary>
    private void AddSuggestions(Layer l)
    {
        if (CtxStack is null) return;
        string id = l.Key;

        CtxStack.Children.Add(Sep());
        CtxStack.Children.Add(AskHere(id));

        foreach (var sug in SuggestFor(l))
        {
            var b = new Button
            {
                Content = sug.Label, FontSize = 10.5, Height = 26, Padding = new Thickness(9, 0),
                CornerRadius = new CornerRadius(13), BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.Parse("#E4E4E1")),
                Background = new SolidColorBrush(Color.Parse("#FBFBFA")),
                Foreground = new SolidColorBrush(Color.Parse("#3A3A38")),
            };
            ToolTip.SetTip(b, sug.Tip);
            var act = sug.Do;
            b.Click += (_, _) => SafeCtx(act);
            CtxStack.Children.Add(b);
        }
    }

    /// <summary>✦ — escreves ali e a IA recebe o pedido JÁ referido a este elemento.</summary>
    private Control AskHere(string id)
    {
        var btn = new Button
        {
            Content = "✦ pedir aqui", FontSize = 10.5, Height = 26, Padding = new Thickness(9, 0),
            CornerRadius = new CornerRadius(13), BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Color.Parse("#6D5EF6")), Foreground = Brushes.White,
        };
        ToolTip.SetTip(btn, "Falar com a IA sobre ESTE elemento");

        var box = new TextBox
        {
            Watermark = "o que queres neste elemento?", FontSize = 12, Width = 260, Height = 30,
            CornerRadius = new CornerRadius(8),
        };
        var send = new Button
        {
            Content = "Enviar", FontSize = 11, Height = 26, Margin = new Thickness(0, 6, 0, 0),
            CornerRadius = new CornerRadius(8), HorizontalAlignment = HorizontalAlignment.Right,
        };
        var panel = new StackPanel { Width = 262, Margin = new Thickness(4), Spacing = 2 };
        panel.Children.Add(new TextBlock
        {
            Text = "Sobre «" + id + "»", FontSize = 10, Margin = new Thickness(2, 0, 0, 4),
            Foreground = new SolidColorBrush(Color.Parse("#9A9A97")),
        });
        panel.Children.Add(box);
        panel.Children.Add(send);

        void Go()
        {
            var txt = box.Text?.Trim();
            if (string.IsNullOrEmpty(txt)) return;
            btn.Flyout?.Hide();
            box.Text = "";
            // ancorar o pedido no elemento é o que faz disto "aponta-e-instrui"
            ChatInput.Text = $"No elemento «{id}»: {txt}";
            ShowTab("chat");
            OnSendChat(null, new Avalonia.Interactivity.RoutedEventArgs());
        }
        send.Click += (_, _) => Go();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Go(); };

        btn.Flyout = new Flyout { Content = panel, Placement = PlacementMode.Bottom };
        return btn;
    }
}
