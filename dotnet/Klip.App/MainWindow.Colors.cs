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
using Avalonia.Media.Imaging;
using Klip.Model;
using SkiaSharp;

namespace Klip.App;

/// <summary>
/// Aba COR: editor de gradiente multi-stop + seletor de cor rico (matiz/saturação, hex, paletas).
///
/// Duas regras atravessam o ficheiro inteiro:
///  · o painel NUNCA escreve no documento fora do contrato Mutate(...) ou dos verbos do bus
///    (ApiSetGradient/ApiSetStop/ApiAddStop/ApiRemoveStop) — assim a IA e a mão fazem o MESMO;
///  · arrastar é pré-visualização viva (Live3D, sem histórico) e largar é UM ponto de undo
///    (Commit3D ou o verbo do bus, que já faz Mutate). Sem isto, arrastar um marcador enchia
///    o histórico com 60 estados por segundo e o Ctrl+Z ficava inútil.
/// </summary>
public partial class MainWindow : Window
{
    private bool _pcBuilt;

    // O painel direito tem 320px; menos margem (10) e padding do Border (8) sobram ~284. 264 deixa
    // folga para a barra de scroll aparecer sem empurrar a rampa para fora.
    private const int PcRampW = 264;
    private const int PcRampH = 26;

    private bool _pcSync;          // guarda de re-entrância: Sync mexe nos sliders, os sliders não podem responder
    private int _pcSelStop;        // paragem em foco (a que os ◆ e o botão de remover atacam)
    private int _pcDrag = -1;      // índice do marcador a ser arrastado, -1 = nenhum
    private double _pcDragX0;
    private bool _pcDragMoved;

    /// <summary>
    /// Etiqueta spot escolhida no último pick do <see cref="ColorFlyout"/>. Existe porque a assinatura
    /// do flyout só devolve um uint: sem isto, escolher "PANTONE 185 C" aplicava o RGB certo e perdia
    /// o CÓDIGO — e uma cor de marca sem nome deixa de ser cor de marca no momento seguinte.
    /// Quem consome lê-a IMEDIATAMENTE dentro do callback; qualquer outro pick limpa-a.
    /// </summary>
    private SpotRef? _pcPickedSpot;

    // peças construídas uma vez e depois só sincronizadas
    private TextBlock? _pcWho, _pcFillHex, _pcFillSpot, _pcStrokeHex, _pcStrokeSpot, _pcStopInfo, _pcAviso;
    private Border? _pcFillSw, _pcStrokeSw;
    private Image? _pcRampImg;
    private Canvas? _pcMarks;
    private StackPanel? _pcGradBox, _pcSemGrad;
    private Button? _pcAddBtn, _pcDelBtn, _pcKfCor, _pcKfPos;
    private readonly Dictionary<string, Slider> _pcSl = new();
    private readonly Dictionary<string, TextBlock> _pcVal = new();
    private readonly Dictionary<GradKind, Button> _pcKindBtn = new();

    private static readonly IBrush PcBorda = new SolidColorBrush(Color.Parse("#DDDDDA"));
    private static readonly IBrush PcChip = new SolidColorBrush(Color.Parse("#F4F4F2"));

    // ============================================================ construção

    private void BuildColorPanel()
    {
        var st = ColorStack.Children;
        st.Clear();

        _pcWho = new TextBlock { Text = "(sem seleção)", FontSize = 10.5, Foreground = BrHead, Margin = new Thickness(0, 0, 0, 3) };
        st.Add(_pcWho);

        // ---------------- preenchimento / contorno ----------------
        st.Add(Head3D("PREENCHIMENTO"));
        _pcFillSw = PcSwatch();
        _pcFillHex = PcHexLabel();
        st.Add(PcSwatchRow(_pcFillSw, _pcFillHex, PcAbrirFill));
        _pcFillSpot = PcSpotLabel();
        st.Add(_pcFillSpot);

        st.Add(Head3D("CONTORNO"));
        _pcStrokeSw = PcSwatch();
        _pcStrokeHex = PcHexLabel();
        st.Add(PcSwatchRow(_pcStrokeSw, _pcStrokeHex, PcAbrirStroke));
        _pcStrokeSpot = PcSpotLabel();
        st.Add(_pcStrokeSpot);

        // ---------------- gradiente ----------------
        st.Add(Head3D("GRADIENTE"));

        // camada sem gradiente: nasce a partir da cor que já lá está, não do nada
        _pcSemGrad = new StackPanel { Spacing = 3 };
        _pcSemGrad.Children.Add(new TextBlock
        {
            Text = "Esta camada tem cor chapada.", FontSize = 10, Foreground = BrHead, TextWrapping = TextWrapping.Wrap,
        });
        var criar = new Button
        {
            Content = "Criar gradiente a partir da cor", FontSize = 10.5, Height = 22,
            Padding = new Thickness(8, 0), CornerRadius = new CornerRadius(6),
        };
        criar.Click += (_, _) => PcCriarGradiente();
        _pcSemGrad.Children.Add(criar);
        st.Add(_pcSemGrad);

        _pcGradBox = new StackPanel { Spacing = 3 };

        var kinds = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
        foreach (var (k, nome) in new[] { (GradKind.Linear, "Linear"), (GradKind.Radial, "Radial"), (GradKind.Conic, "Cónico") })
        {
            var b = new Button
            {
                Content = nome, FontSize = 10, Height = 21, Padding = new Thickness(8, 0),
                CornerRadius = new CornerRadius(6), Background = PcChip, BorderThickness = new Thickness(0),
            };
            var capturado = k;
            b.Click += (_, _) => PcMudarKind(capturado);
            _pcKindBtn[k] = b;
            kinds.Children.Add(b);
        }
        _pcGradBox.Children.Add(kinds);

        // rampa (Skia) + marcadores por cima: a rampa é imagem, os marcadores são controlos reais
        // porque precisam de captura do ponteiro para o arrasto e de âncora para o flyout de cor.
        var host = new Avalonia.Controls.Grid { Width = PcRampW, Height = PcRampH + 16, Margin = new Thickness(0, 2, 0, 0) };
        _pcRampImg = new Image
        {
            Width = PcRampW, Height = PcRampH, Stretch = Stretch.Fill,
            VerticalAlignment = VerticalAlignment.Top,
        };
        _pcMarks = new Canvas { Width = PcRampW, Height = PcRampH + 16 };
        host.Children.Add(_pcRampImg);
        host.Children.Add(_pcMarks);
        _pcGradBox.Children.Add(host);

        _pcStopInfo = new TextBlock { Text = "—", FontSize = 10, Foreground = BrValue };
        _pcGradBox.Children.Add(_pcStopInfo);

        var acoes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
        _pcAddBtn = PcMini("＋ paragem", "Acrescenta uma paragem no maior intervalo (máx. 8)");
        _pcAddBtn.Click += (_, _) => PcAddStop();
        _pcDelBtn = PcMini("− paragem", "Remove a paragem em foco (mínimo 2)");
        _pcDelBtn.Click += (_, _) => PcRemoveStop();
        _pcKfCor = PcMini("◆ cor", "Keyframe da COR desta paragem, no tempo atual");
        _pcKfCor.Click += (_, _) => PcKeyframeStop(true);
        _pcKfPos = PcMini("◆ pos", "Keyframe da POSIÇÃO desta paragem, no tempo atual");
        _pcKfPos.Click += (_, _) => PcKeyframeStop(false);
        acoes.Children.Add(_pcAddBtn); acoes.Children.Add(_pcDelBtn);
        acoes.Children.Add(_pcKfCor); acoes.Children.Add(_pcKfPos);
        _pcGradBox.Children.Add(acoes);

        _pcGradBox.Children.Add(Head3D("GEOMETRIA"));
        _pcGradBox.Children.Add(PcRow("gradient.angle", "Ângulo", 0, 360, "0"));
        _pcGradBox.Children.Add(PcRow("gradient.center.x", "Centro X", 0, 1, "0.00"));
        _pcGradBox.Children.Add(PcRow("gradient.center.y", "Centro Y", 0, 1, "0.00"));
        _pcGradBox.Children.Add(PcRow("gradient.radius", "Raio", 0.01, 1, "0.00"));

        st.Add(_pcGradBox);

        _pcAviso = new TextBlock
        {
            FontSize = 9.5, Foreground = BrHead, TextWrapping = TextWrapping.Wrap,
            LineHeight = 12, Margin = new Thickness(0, 6, 0, 0),
        };
        st.Add(_pcAviso);
    }

    private static Border PcSwatch() => new()
    {
        Width = 22, Height = 22, CornerRadius = new CornerRadius(5),
        Background = Brushes.Transparent, BorderBrush = PcBorda, BorderThickness = new Thickness(1),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static TextBlock PcHexLabel() => new()
    {
        Text = "—", FontSize = 11, FontFamily = new FontFamily("Consolas,monospace"),
        Foreground = BrValue, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
    };

    private static TextBlock PcSpotLabel() => new()
    {
        IsVisible = false, FontSize = 9.5, Foreground = BrHead, TextWrapping = TextWrapping.Wrap, LineHeight = 12,
    };

    /// <summary>Mancha clicável + hex. O flyout é criado no clique (e não uma vez) para abrir sempre na cor de agora.</summary>
    private static Button PcSwatchRow(Border swatch, TextBlock hex, Action<Button> abrir)
    {
        var linha = new StackPanel { Orientation = Orientation.Horizontal };
        linha.Children.Add(swatch);
        linha.Children.Add(hex);
        var b = new Button
        {
            Content = linha, Height = 28, Padding = new Thickness(2, 0), CornerRadius = new CornerRadius(7),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
        };
        b.Click += (_, _) => abrir(b);
        return b;
    }

    private static Button PcMini(string texto, string dica)
    {
        var b = new Button
        {
            Content = texto, FontSize = 9.5, Height = 20, Padding = new Thickness(6, 0),
            CornerRadius = new CornerRadius(5), Background = PcChip, BorderThickness = new Thickness(0),
        };
        ToolTip.SetTip(b, dica);
        return b;
    }

    /// <summary>Linha rótulo · slider · valor · ◆, no molde do painel 3D (Live3D a arrastar, Commit3D ao largar).</summary>
    private Control PcRow(string key, string label, double min, double max, string fmt)
    {
        var g = new Avalonia.Controls.Grid { ColumnDefinitions = new ColumnDefinitions("52,*,32,18"), Height = 22 };

        var lb = new TextBlock { Text = label, FontSize = 10.5, Foreground = BrLabel, VerticalAlignment = VerticalAlignment.Center };
        Avalonia.Controls.Grid.SetColumn(lb, 0); g.Children.Add(lb);

        var sl = new Slider
        {
            Minimum = min, Maximum = max, Height = 22, Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0),
        };
        Avalonia.Controls.Grid.SetColumn(sl, 1); g.Children.Add(sl);
        _pcSl[key] = sl;

        var vt = new TextBlock { FontSize = 10, Foreground = BrValue, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
        Avalonia.Controls.Grid.SetColumn(vt, 2); g.Children.Add(vt);
        _pcVal[key] = vt;

        sl.PropertyChanged += (_, ev) =>
        {
            if (ev.Property.Name != "Value" || _pcSync) return;
            vt.Text = sl.Value.ToString(fmt, CultureInfo.InvariantCulture);
            // Sem gradiente, o PropRegistry SEMEIA um a partir do par legado — mexer no slider por
            // engano transformava uma cor chapada em gradiente sem ninguém pedir. Daí a guarda.
            if (PcGrad() is null) return;
            Live3D(key, sl.Value, false);
        };
        sl.AddHandler(InputElement.PointerReleasedEvent, (_, _) => { if (PcGrad() is not null) Commit3D(); },
                      RoutingStrategies.Tunnel | RoutingStrategies.Bubble);

        var kb = new Button
        {
            Content = "◆", FontSize = 9, Width = 18, Height = 18, Padding = new Thickness(0),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = BrHead,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        ToolTip.SetTip(kb, "Keyframe aqui (no tempo atual da timeline)");
        kb.Click += (_, _) => PcKeyframe(key, sl.Value.ToString(CultureInfo.InvariantCulture), kb);
        Avalonia.Controls.Grid.SetColumn(kb, 3); g.Children.Add(kb);

        return g;
    }

    // ============================================================ estado / atalhos

    private Layer? PcLayer() => _selected >= 0 && _selected < _layers.Count ? _layers[_selected] : null;
    private GradientSpec? PcGrad() => PcLayer()?.FillGradient;

    private void PcSafe(Action act)
    {
        try { act(); }
        catch (Exception ex) { PcMsg(ex.Message); }
    }

    /// <summary>
    /// Mensagens (e erros) vão para o Inspector — nada de diálogos nativos. Tem de ser DEPOIS da
    /// mutação: qualquer Mutate faz Refresh→UpdateInspector, que reescreve este mesmo texto.
    /// </summary>
    private void PcMsg(string msg)
    {
        if (Inspector is not null) Inspector.Text = msg;
    }

    // ============================================================ preenchimento / contorno

    private void PcAbrirFill(Button ancora)
    {
        var l = PcLayer();
        if (l is null) { PcMsg("Seleciona uma camada primeiro."); return; }
        uint atual = l.FillColor?.Eval(_previewT) ?? l.FillArgb;
        PcMostrar(ancora, ColorFlyout(atual, cor => PcAplicarFill(cor)));
    }

    private void PcAplicarFill(uint cor)
    {
        var spot = _pcPickedSpot;   // lido JÁ: qualquer outro caminho de pick limpa-o
        var id = Sel3DKey();
        if (id is null) { PcMsg("Seleciona uma camada primeiro."); return; }

        PcSafe(() =>
        {
            if (spot is not null) { ApiSetSpot(id, spot.Code, "fill"); return; }

            int ix = _selected;
            var l = _layers[ix];
            Mutate(() => _layers[ix] = l with
            {
                FillArgb = cor,
                // O ColorTrack MANDA sobre o uint: deixá-lo lá prendia a cor e o swatch parecia avariado.
                // Só se mantém quando é uma animação a sério (>1 keyframe) — apagá-la seria roubo silencioso.
                FillColor = l.FillColor is { Keys.Count: > 1 } ? l.FillColor : null,
                // a cor deixou de ser a da chapa: manter a etiqueta era mentir ao gráfico
                FillSpot = null,
            });
        });
    }

    private void PcAbrirStroke(Button ancora)
    {
        var l = PcLayer();
        if (l is null) { PcMsg("Seleciona uma camada primeiro."); return; }
        uint atual = l.StrokeColor?.Eval(_previewT) ?? l.StrokeArgb ?? 0xFF232326u;
        PcMostrar(ancora, ColorFlyout(atual, cor => PcAplicarStroke(cor)));
    }

    private void PcAplicarStroke(uint cor)
    {
        var spot = _pcPickedSpot;
        var id = Sel3DKey();
        if (id is null) { PcMsg("Seleciona uma camada primeiro."); return; }

        PcSafe(() =>
        {
            if (spot is not null) { ApiSetSpot(id, spot.Code, "stroke"); return; }

            int ix = _selected;
            var l = _layers[ix];
            Mutate(() => _layers[ix] = l with
            {
                StrokeArgb = cor,
                StrokeColor = l.StrokeColor is { Keys.Count: > 1 } ? l.StrokeColor : null,
                // sem espessura o contorno não se desenha e a cor "não pegava" — parecia bug
                StrokeWidth = l.StrokeWidth > 0 ? l.StrokeWidth : 4,
                StrokeSpot = null,
            });
        });
    }

    private static void PcMostrar(Control ancora, Control conteudo)
        => new Flyout { Content = conteudo, Placement = PlacementMode.Bottom }.ShowAt(ancora);

    // ============================================================ gradiente

    private void PcCriarGradiente()
    {
        var l = PcLayer();
        var id = Sel3DKey();
        if (l is null || id is null) { PcMsg("Seleciona uma camada primeiro."); return; }

        uint c0 = l.FillColor?.Eval(_previewT) ?? l.FillArgb;
        uint c1 = PcEscurecer(c0, 0.45);   // a mesma cor mais funda: lê-se como sombra, não como cor nova
        PcSafe(() =>
        {
            ApiSetGradient(id, $"{GradHex(c0)}@0, {GradHex(c1)}@1", "linear", null, null, null, null, null);
            _pcSelStop = 0;
            PcMsg("Gradiente criado a partir da cor da camada.");
        });
    }

    private void PcMudarKind(GradKind k)
    {
        var l = PcLayer();
        if (l is null) { PcMsg("Seleciona uma camada primeiro."); return; }
        if (l.FillGradient is not { } g) { PcMsg("Esta camada ainda não tem gradiente — cria um primeiro."); return; }
        if (g.Kind == k) return;

        // De propósito NÃO se passa pelo ApiSetGradient: ele reconstrói as paragens a partir de hex e
        // isso apagava os ColorTrack/Offset das paragens animadas. Mudar o tipo não é mudar as cores.
        int ix = _selected;
        Mutate(() => _layers[ix] = _layers[ix] with { FillGradient = g with { Kind = k } });
    }

    private void PcAddStop()
    {
        var g = PcGrad();
        var id = Sel3DKey();
        if (g is null || id is null) return;

        var ps = g.Stops.Select(s => s.EvalPos(_previewT)).OrderBy(x => x).ToList();
        double alvo = 0.5, maior = -1;
        for (int i = 1; i < ps.Count; i++)
        {
            double d = ps[i] - ps[i - 1];
            if (d > maior) { maior = d; alvo = (ps[i] + ps[i - 1]) / 2.0; }
        }
        uint cor = PcAmostrar(g, alvo);   // nasce da cor que já lá está: acrescentar não deve mudar o desenho

        PcSafe(() =>
        {
            ApiAddStop(id, GradHex(cor), alvo);
            _pcSelStop = PcParagemPerto(alvo);
        });
    }

    private void PcRemoveStop()
    {
        var g = PcGrad();
        var id = Sel3DKey();
        if (g is null || id is null) return;
        PcSafe(() =>
        {
            ApiRemoveStop(id, Math.Clamp(_pcSelStop, 0, g.Stops.Count - 1));
            _pcSelStop = 0;
        });
    }

    private void PcKeyframeStop(bool cor)
    {
        var g = PcGrad();
        if (g is null) { PcMsg("Esta camada ainda não tem gradiente."); return; }
        int i = Math.Clamp(_pcSelStop, 0, g.Stops.Count - 1);
        var s = g.Stops[i];
        if (cor) PcKeyframe($"gradient.stop{i}.color", GradHex(s.EvalArgb(_previewT)), _pcKfCor);
        else PcKeyframe($"gradient.stop{i}.pos", s.EvalPos(_previewT).ToString(CultureInfo.InvariantCulture), _pcKfPos);
    }

    private void PcKeyframe(string path, string valor, Button? kb)
    {
        var id = Sel3DKey();
        if (id is null) { PcMsg("Seleciona uma camada primeiro."); return; }
        PcSafe(() =>
        {
            // "inout" e não "ease_in_out": o ParseEase só conhece hold/in/out/inout/outback e tudo o
            // resto cai em Linear — um keyframe que dizia ser suave e saía a direito.
            ApiSetKeyframe(id, path, _previewT, valor, "inout");
            if (kb is not null) kb.Foreground = BrAccent;
            PcMsg($"◆ {path} @ {_previewT:0.00}s");
        });
    }

    /// <summary>Cor do gradiente na posição p, interpolada em sRGB entre as paragens vizinhas.</summary>
    private uint PcAmostrar(GradientSpec g, double p)
    {
        var ps = g.Stops.Select(s => (pos: s.EvalPos(_previewT), argb: s.EvalArgb(_previewT)))
                        .OrderBy(x => x.pos).ToList();
        if (ps.Count == 0) return 0xFF000000u;
        if (p <= ps[0].pos) return ps[0].argb;
        for (int i = 1; i < ps.Count; i++)
        {
            if (p > ps[i].pos) continue;
            double span = ps[i].pos - ps[i - 1].pos;
            double f = span <= 1e-9 ? 0 : (p - ps[i - 1].pos) / span;
            return PcLerp(ps[i - 1].argb, ps[i].argb, f);
        }
        return ps[^1].argb;
    }

    /// <summary>Índice da paragem que ficou na posição p depois de o bus normalizar (que reordena).</summary>
    private int PcParagemPerto(double p)
    {
        var g = PcGrad();
        if (g is null) return 0;
        int melhor = 0; double dist = double.MaxValue;
        for (int i = 0; i < g.Stops.Count; i++)
        {
            double d = Math.Abs(g.Stops[i].EvalPos(_previewT) - p);
            if (d < dist) { dist = d; melhor = i; }
        }
        return melhor;
    }

    // ---------------- rampa + marcadores ----------------

    private void PcDrawRamp()
    {
        if (_pcRampImg is null) return;
        try { PcDrawRampCore(); }
        catch { /* corre a cada movimento do rato durante o arrasto: nunca pode derrubar a app */ }
    }

    private void PcDrawRampCore()
    {
        if (_pcRampImg is null) return;
        var g = PcGrad();
        _pcRampImg.Source = PcBitmap(PcRampW, PcRampH, c =>
        {
            PcXadrez(c, PcRampW, PcRampH);   // xadrez por baixo: sem ele, uma paragem transparente parecia branca
            if (g is null) return;

            var pares = g.Stops.Select(s => (p: (float)Math.Clamp(s.EvalPos(_previewT), 0, 1), k: (SKColor)s.EvalArgb(_previewT)))
                               .OrderBy(x => x.p).ToList();
            var pos = pares.Select(x => x.p).ToArray();
            var col = pares.Select(x => x.k).ToArray();
            if (col.Length == 1) { pos = new[] { 0f, 1f }; col = new[] { col[0], col[0] }; }
            if (col.Length == 0) return;

            using var sh = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(PcRampW, 0), col, pos, SKShaderTileMode.Clamp);
            using var p = new SKPaint { Shader = sh };
            c.DrawRect(0, 0, PcRampW, PcRampH, p);
            using var moldura = new SKPaint { Color = new SKColor(0xFFDDDDDA), IsStroke = true, StrokeWidth = 1 };
            c.DrawRect(0.5f, 0.5f, PcRampW - 1, PcRampH - 1, moldura);
        });
    }

    private void PcRebuildMarks()
    {
        if (_pcMarks is null) return;
        _pcMarks.Children.Clear();
        var g = PcGrad();
        if (g is null) return;

        for (int i = 0; i < g.Stops.Count; i++)
        {
            var s = g.Stops[i];
            double p = s.EvalPos(_previewT);
            var m = PcMarker(i, s.EvalArgb(_previewT), p);
            Avalonia.Controls.Canvas.SetLeft(m, p * PcRampW - 6.5);
            Avalonia.Controls.Canvas.SetTop(m, PcRampH + 1);
            _pcMarks.Children.Add(m);
        }
    }

    private Control PcMarker(int i, uint argb, double pos)
    {
        var m = new Border
        {
            Width = 13, Height = 13, CornerRadius = new CornerRadius(7),
            Background = new SolidColorBrush(Color.FromUInt32(argb)),
            BorderBrush = i == _pcSelStop ? BrAccent : PcBorda,
            BorderThickness = new Thickness(i == _pcSelStop ? 2 : 1),
            Cursor = new Cursor(StandardCursorType.SizeWestEast),
        };
        ToolTip.SetTip(m, $"Paragem {i} · {pos.ToString("0.00", CultureInfo.InvariantCulture)}\nArrasta para mover · clica para mudar a cor");

        m.PointerPressed += (_, e) =>
        {
            _pcDrag = i; _pcSelStop = i; _pcDragMoved = false;
            _pcDragX0 = e.GetPosition(_pcMarks).X;
            e.Pointer.Capture(m);
            e.Handled = true;
            PcSyncStopInfo();   // só texto e realces: reconstruir agora tirava o controlo debaixo do rato
        };

        m.PointerMoved += (_, e) =>
        {
            if (_pcDrag != i) return;
            double x = e.GetPosition(_pcMarks).X;
            // limiar: sem ele, o tremor do clique contava como arrasto e o flyout de cor nunca abria
            if (!_pcDragMoved && Math.Abs(x - _pcDragX0) < 2) return;
            _pcDragMoved = true;
            double p = Math.Clamp(x / PcRampW, 0, 1);
            Avalonia.Controls.Canvas.SetLeft(m, p * PcRampW - 6.5);
            Live3D($"gradient.stop{i}.pos", p, false);   // vivo, sem histórico
            PcDrawRamp();
        };

        m.PointerReleased += (_, e) =>
        {
            if (_pcDrag != i) return;
            _pcDrag = -1;
            e.Pointer.Capture(null);

            if (!_pcDragMoved) { PcAbrirStopPicker(i, m); return; }

            double p = Math.Clamp(e.GetPosition(_pcMarks).X / PcRampW, 0, 1);
            var id = Sel3DKey();
            if (id is null) return;
            // Commit pelo BUS e não por Commit3D: o Live3D deixou a posição num Offset track e é o
            // ApiSetStop que a fixa em Pos e limpa o track — senão o Normalized() do bus, que ordena
            // pelo Pos ESTÁTICO, passava a devolver índices que não batiam com o que está desenhado.
            // Ele já faz Mutate → o histórico leva UM ponto só, o do gesto inteiro.
            PcSafe(() => { ApiSetStop(id, i, null, p); _pcSelStop = PcParagemPerto(p); PcSyncStopInfo(); });
        };

        return m;
    }

    private void PcAbrirStopPicker(int i, Control ancora)
    {
        var g = PcGrad();
        if (g is null || i >= g.Stops.Count) return;
        uint atual = g.Stops[i].EvalArgb(_previewT);
        PcMostrar(ancora, ColorFlyout(atual, cor =>
        {
            var id = Sel3DKey();
            if (id is null) return;
            // GradHex e não Hex: o Hex deita fora o alpha e uma paragem que desvanece perdia a transparência
            PcSafe(() => ApiSetStop(id, i, GradHex(cor), null));
        }));
    }

    // ============================================================ sincronizar UI ← modelo

    private void SyncColorPanel()
    {
        if (!_pcBuilt || _tab != "color") return;
        if (_pcDrag >= 0) return;   // a meio de um arrasto: reconstruir marcadores partia a captura do ponteiro

        _pcSync = true;
        try
        {
            var l = PcLayer();
            if (_pcWho is not null) _pcWho.Text = l?.Name ?? "(sem seleção)";

            uint fill = l is null ? 0u : (l.FillColor?.Eval(_previewT) ?? l.FillArgb);
            uint stroke = l?.StrokeColor?.Eval(_previewT) ?? l?.StrokeArgb ?? 0u;
            PcPorSwatch(_pcFillSw, _pcFillHex, l is not null, fill);
            PcPorSwatch(_pcStrokeSw, _pcStrokeHex, l?.StrokeColor is not null || l?.StrokeArgb is not null, stroke);
            PcPorSpot(_pcFillSpot, l?.FillSpot);
            PcPorSpot(_pcStrokeSpot, l?.StrokeSpot);

            var g = l?.FillGradient;
            if (_pcGradBox is not null) _pcGradBox.IsVisible = g is not null;
            if (_pcSemGrad is not null) _pcSemGrad.IsVisible = l is not null && g is null;

            foreach (var (k, b) in _pcKindBtn)
            {
                bool on = g is not null && g.Kind == k;
                b.Background = on ? BrAccent : PcChip;
                b.Foreground = on ? Brushes.White : BrValue;
            }

            if (g is not null && _pcSelStop >= g.Stops.Count) _pcSelStop = 0;
            PcDrawRamp();
            PcRebuildMarks();
            PcSyncStopInfo();

            void Por(string k, double v)
            {
                if (!_pcSl.TryGetValue(k, out var sl)) return;
                sl.Value = Math.Clamp(v, sl.Minimum, sl.Maximum);
                if (_pcVal.TryGetValue(k, out var vt))
                    vt.Text = sl.Value.ToString(k == "gradient.angle" ? "0" : "0.00", CultureInfo.InvariantCulture);
            }
            Por("gradient.angle", g?.EvalAngle(_previewT) ?? 90);
            Por("gradient.center.x", g?.EvalCenterX(_previewT) ?? 0.5);
            Por("gradient.center.y", g?.EvalCenterY(_previewT) ?? 0.5);
            Por("gradient.radius", g?.EvalRadius(_previewT) ?? 0.62);

            // cada tipo só usa parte da geometria; mostrar sliders mortos convida a mexer e a não ver nada
            bool usaAngulo = g is not null && g.Kind != GradKind.Radial;
            bool usaCentro = g is not null && g.Kind != GradKind.Linear;
            if (_pcSl.TryGetValue("gradient.angle", out var sa)) sa.IsEnabled = usaAngulo;
            if (_pcSl.TryGetValue("gradient.center.x", out var sx)) sx.IsEnabled = usaCentro;
            if (_pcSl.TryGetValue("gradient.center.y", out var sy)) sy.IsEnabled = usaCentro;
            if (_pcSl.TryGetValue("gradient.radius", out var sr)) sr.IsEnabled = g is not null && g.Kind == GradKind.Radial;

            if (_pcAviso is not null)
            {
                _pcAviso.Text = l is null
                    ? "Escolhe uma camada para editar a cor."
                    : g is not null
                        ? "O gradiente é o que pinta esta camada — a mancha de preenchimento acima fica como cor de reserva."
                        : "";
                _pcAviso.IsVisible = _pcAviso.Text.Length > 0;
            }
        }
        catch { /* o painel nunca pode partir a app por causa de uma camada estranha */ }
        finally { _pcSync = false; }
    }

    private static void PcPorSwatch(Border? sw, TextBlock? txt, bool tem, uint argb)
    {
        if (sw is not null) sw.Background = tem ? new SolidColorBrush(Color.FromUInt32(argb)) : Brushes.Transparent;
        if (txt is not null) txt.Text = tem ? GradHex(argb) : "—";
    }

    private static void PcPorSpot(TextBlock? txt, SpotRef? sp)
    {
        if (txt is null) return;
        if (sp is null) { txt.IsVisible = false; return; }
        txt.IsVisible = true;
        txt.Text = sp.Code + (sp.Library.Length > 0 ? "  ·  " + sp.Library : "")
                 + (sp.HasCmyk
                     ? $"\nC{sp.C:0} M{sp.M:0} Y{sp.Y:0} K{sp.K:0}"
                     : "\n(este livro não traz chapa CMYK)");
    }

    /// <summary>Só texto e realces — não reconstrói marcadores, para poder correr a meio de um arrasto.</summary>
    private void PcSyncStopInfo()
    {
        var g = PcGrad();
        if (_pcStopInfo is not null)
            _pcStopInfo.Text = g is null || _pcSelStop >= g.Stops.Count
                ? "—"
                : $"Paragem {_pcSelStop} · {g.Stops[_pcSelStop].EvalPos(_previewT).ToString("0.00", CultureInfo.InvariantCulture)}"
                  + $" · {GradHex(g.Stops[_pcSelStop].EvalArgb(_previewT))}";

        if (_pcAddBtn is not null) _pcAddBtn.IsEnabled = g is not null && g.Stops.Count < GradientSpec.MaxStops;
        if (_pcDelBtn is not null) _pcDelBtn.IsEnabled = g is not null && g.Stops.Count > 2;

        if (_pcMarks is null) return;
        for (int i = 0; i < _pcMarks.Children.Count; i++)
            if (_pcMarks.Children[i] is Border b)
            {
                b.BorderBrush = i == _pcSelStop ? BrAccent : PcBorda;
                b.BorderThickness = new Thickness(i == _pcSelStop ? 2 : 1);
            }
    }

    // ============================================================ seletor de cor

    /// <summary>Seletor de cor rico: quadrado S/V, tira de matiz, alpha, hex, cores do documento e livros de cor.</summary>
    internal Control ColorFlyout(uint argb, Action<uint> pick)
    {
        const int W = 200, HSV = 112, TIRA = 13;

        var (h, s, v) = PcToHsv(argb);
        double a = ((argb >> 24) & 0xFF) / 255.0;
        bool aEscrever = false;   // a caixa de hex é reescrita por nós; sem guarda o handler re-entrava

        uint Actual() => PcFromHsv(h, s, v, a);

        var svImg = new Image { Width = W, Height = HSV, Stretch = Stretch.Fill, Cursor = new Cursor(StandardCursorType.Cross) };
        var hueImg = new Image { Width = W, Height = TIRA, Stretch = Stretch.Fill, Cursor = new Cursor(StandardCursorType.Hand) };
        var alfaImg = new Image { Width = W, Height = TIRA, Stretch = Stretch.Fill, Cursor = new Cursor(StandardCursorType.Hand) };

        var mostra = new Border
        {
            Width = 26, Height = 26, CornerRadius = new CornerRadius(6),
            BorderBrush = PcBorda, BorderThickness = new Thickness(1),
        };
        var caixaHex = new TextBox
        {
            FontSize = 11, Height = 26, Padding = new Thickness(6, 0), CornerRadius = new CornerRadius(6),
            FontFamily = new FontFamily("Consolas,monospace"), Watermark = "#RRGGBB",
        };
        ToolTip.SetTip(caixaHex, "Escreve o hex e carrega em Enter");

        void Pintar()
        {
            uint pura = PcFromHsv(h, s, v, 1.0);
            svImg.Source = PcBitmap(W, HSV, c => PcQuadradoSV(c, W, HSV, h, s, v));
            hueImg.Source = PcBitmap(W, TIRA, c => PcTiraMatiz(c, W, TIRA, h));
            alfaImg.Source = PcBitmap(W, TIRA, c => PcTiraAlfa(c, W, TIRA, pura, a));
            mostra.Background = new SolidColorBrush(Color.FromUInt32(Actual()));
            aEscrever = true; caixaHex.Text = GradHex(Actual()); aEscrever = false;
        }

        // pick só ao LARGAR (e não a cada pixel): cada pick é uma mutação, e uma por pixel enchia o histórico
        void Escolher() { _pcPickedSpot = null; pick(Actual()); }

        void SvEm(Point p) { s = Math.Clamp(p.X / W, 0, 1); v = Math.Clamp(1 - p.Y / HSV, 0, 1); Pintar(); }
        svImg.PointerPressed += (_, e) => { e.Pointer.Capture(svImg); SvEm(e.GetPosition(svImg)); };
        svImg.PointerMoved += (_, e) => { if (e.GetCurrentPoint(svImg).Properties.IsLeftButtonPressed) SvEm(e.GetPosition(svImg)); };
        svImg.PointerReleased += (_, e) => { e.Pointer.Capture(null); SvEm(e.GetPosition(svImg)); Escolher(); };

        void HueEm(Point p) { h = Math.Clamp(p.X / W, 0, 1) * 360.0; Pintar(); }
        hueImg.PointerPressed += (_, e) => { e.Pointer.Capture(hueImg); HueEm(e.GetPosition(hueImg)); };
        hueImg.PointerMoved += (_, e) => { if (e.GetCurrentPoint(hueImg).Properties.IsLeftButtonPressed) HueEm(e.GetPosition(hueImg)); };
        hueImg.PointerReleased += (_, e) => { e.Pointer.Capture(null); HueEm(e.GetPosition(hueImg)); Escolher(); };

        void AlfaEm(Point p) { a = Math.Clamp(p.X / W, 0, 1); Pintar(); }
        alfaImg.PointerPressed += (_, e) => { e.Pointer.Capture(alfaImg); AlfaEm(e.GetPosition(alfaImg)); };
        alfaImg.PointerMoved += (_, e) => { if (e.GetCurrentPoint(alfaImg).Properties.IsLeftButtonPressed) AlfaEm(e.GetPosition(alfaImg)); };
        alfaImg.PointerReleased += (_, e) => { e.Pointer.Capture(null); AlfaEm(e.GetPosition(alfaImg)); Escolher(); };

        caixaHex.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || aEscrever) return;
            e.Handled = true;
            var txt = (caixaHex.Text ?? "").Trim().TrimStart('#');
            // validar À MÃO antes do ParseColor: ele devolve o fallback em silêncio, e um hex torto
            // passava a "aplicou-se a cor de antes" sem ninguém perceber porquê
            if ((txt.Length != 6 && txt.Length != 8) || !txt.All(Uri.IsHexDigit))
            { PcMsg($"hex inválido: '{caixaHex.Text}'. Esperava #RRGGBB ou #AARRGGBB."); return; }
            uint c = ParseColor(txt, 0xFF000000u);
            (h, s, v) = PcToHsv(c);
            a = ((c >> 24) & 0xFF) / 255.0;
            Pintar();
            Escolher();
        };

        var topo = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        topo.Children.Add(mostra);
        caixaHex.Width = W - 32;
        topo.Children.Add(caixaHex);

        // cores JÁ USADAS no documento: reaproveitar é mais rápido (e mais coerente) do que reacertar à mão
        var usadas = new WrapPanel { Width = W, ItemWidth = 22, ItemHeight = 22 };
        foreach (var c in PcCoresDoDocumento(18))
        {
            var cel = new Button
            {
                Width = 20, Height = 20, Margin = new Thickness(1), Padding = new Thickness(0),
                CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1), BorderBrush = PcBorda,
                Background = new SolidColorBrush(Color.FromUInt32(c)),
            };
            ToolTip.SetTip(cel, GradHex(c));
            uint capturada = c;
            cel.Click += (_, _) =>
            {
                (h, s, v) = PcToHsv(capturada);
                a = ((capturada >> 24) & 0xFF) / 255.0;
                Pintar();
                Escolher();
            };
            usadas.Children.Add(cel);
        }

        var livros = new Button
        {
            Content = "Livros de cor…", FontSize = 10.5, Height = 24, Padding = new Thickness(8, 0),
            CornerRadius = new CornerRadius(6), HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        ToolTip.SetTip(livros, "PANTONE, HKS, TOYO, DIC… lidos do CorelDRAW instalado nesta máquina");
        livros.Click += (_, _) => new Flyout
        {
            Content = SpotFlyout((rgb, spot) =>
            {
                // a etiqueta vai à frente do pick: quem consome lê-a de dentro do callback
                _pcPickedSpot = spot;
                (h, s, v) = PcToHsv(rgb); a = 1.0;
                Pintar();
                pick(rgb);
            }),
            Placement = PlacementMode.Right,
        }.ShowAt(livros);

        var raiz = new StackPanel { Width = W + 12, Spacing = 5, Margin = new Thickness(6) };
        raiz.Children.Add(svImg);
        raiz.Children.Add(hueImg);
        raiz.Children.Add(alfaImg);
        raiz.Children.Add(topo);
        if (usadas.Children.Count > 0)
        {
            raiz.Children.Add(new TextBlock { Text = "No documento", FontSize = 9.5, Foreground = BrHead });
            raiz.Children.Add(usadas);
        }
        raiz.Children.Add(livros);

        Pintar();
        return raiz;
    }

    /// <summary>Cores que já existem no documento (fill, contorno, 2ª cor e paragens de gradiente).</summary>
    private List<uint> PcCoresDoDocumento(int max)
    {
        var vistas = new List<uint>();
        void Juntar(uint c)
        {
            if ((c >> 24) == 0) return;                 // totalmente transparente não é cor escolhível
            if (vistas.Count >= max || vistas.Contains(c)) return;
            vistas.Add(c);
        }
        try
        {
            foreach (var l in _layers)
            {
                Juntar(l.FillColor?.Eval(_previewT) ?? l.FillArgb);
                if (l.StrokeColor?.Eval(_previewT) is { } sc) Juntar(sc); else if (l.StrokeArgb is { } sa) Juntar(sa);
                if (l.FillArgb2 is { } f2) Juntar(l.FillColor2?.Eval(_previewT) ?? f2);
                if (l.FillGradient is { } g)
                    foreach (var s in g.Stops) Juntar(s.EvalArgb(_previewT));
                if (vistas.Count >= max) break;
            }
        }
        catch { /* varrer o documento nunca deve impedir o seletor de abrir */ }
        return vistas;
    }

    // ============================================================ desenho (Skia → WriteableBitmap)

    private static WriteableBitmap PcBitmap(int w, int h, Action<SKCanvas> desenhar)
    {
        var wb = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
        using (var fb = wb.Lock())
        {
            var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surf = SKSurface.Create(info, fb.Address, fb.RowBytes);
            // Create devolve NULL quando o formato/stride não lhe serve. Sem esta guarda, o painel
            // rebentava com NRE a meio de um arrasto — e o arrasto corre fora de qualquer try.
            if (surf is null) return wb;
            desenhar(surf.Canvas);
            surf.Canvas.Flush();
        }
        return wb;
    }

    private static void PcXadrez(SKCanvas c, int w, int h, int cel = 7)
    {
        c.Clear(new SKColor(0xFFFFFFFF));
        using var p = new SKPaint { Color = new SKColor(0xFFE6E6E3) };
        for (int y = 0; y < h; y += cel)
            for (int x = (y / cel % 2) * cel; x < w; x += cel * 2)
                c.DrawRect(x, y, cel, cel, p);
    }

    private static void PcQuadradoSV(SKCanvas c, int w, int h, double matiz, double s, double v)
    {
        var pura = (SKColor)PcFromHsv(matiz, 1, 1, 1.0);
        using (var sh = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(w, 0),
                   new[] { SKColors.White, pura }, null, SKShaderTileMode.Clamp))
        using (var p = new SKPaint { Shader = sh })
            c.DrawRect(0, 0, w, h, p);

        using (var sh = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, h),
                   new[] { SKColors.Transparent, SKColors.Black }, null, SKShaderTileMode.Clamp))
        using (var p = new SKPaint { Shader = sh })
            c.DrawRect(0, 0, w, h, p);

        PcAlvo(c, (float)(s * w), (float)((1 - v) * h));
    }

    private static void PcTiraMatiz(SKCanvas c, int w, int h, double matiz)
    {
        var cores = new SKColor[7];
        for (int i = 0; i < 7; i++) cores[i] = (SKColor)PcFromHsv(i * 60.0, 1, 1, 1.0);
        using (var sh = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(w, 0), cores, null, SKShaderTileMode.Clamp))
        using (var p = new SKPaint { Shader = sh })
            c.DrawRect(0, 0, w, h, p);
        PcRisca(c, (float)(matiz / 360.0 * w), h);
    }

    private static void PcTiraAlfa(SKCanvas c, int w, int h, uint puraArgb, double a)
    {
        PcXadrez(c, w, h, 6);
        var pura = (SKColor)puraArgb;
        using (var sh = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(w, 0),
                   new[] { pura.WithAlpha(0), pura.WithAlpha(255) }, null, SKShaderTileMode.Clamp))
        using (var p = new SKPaint { Shader = sh })
            c.DrawRect(0, 0, w, h, p);
        PcRisca(c, (float)(a * w), h);
    }

    /// <summary>Alvo do quadrado S/V: anel branco por dentro e escuro por fora, para se ver em qualquer cor.</summary>
    private static void PcAlvo(SKCanvas c, float x, float y)
    {
        using var escuro = new SKPaint { Color = new SKColor(0xB0000000), IsStroke = true, StrokeWidth = 1, IsAntialias = true };
        using var claro = new SKPaint { Color = new SKColor(0xFFFFFFFF), IsStroke = true, StrokeWidth = 2, IsAntialias = true };
        c.DrawCircle(x, y, 5, claro);
        c.DrawCircle(x, y, 6.5f, escuro);
    }

    private static void PcRisca(SKCanvas c, float x, int h)
    {
        using var claro = new SKPaint { Color = new SKColor(0xFFFFFFFF), StrokeWidth = 3, IsAntialias = true };
        using var escuro = new SKPaint { Color = new SKColor(0xB0000000), StrokeWidth = 1, IsAntialias = true };
        c.DrawLine(x, 0, x, h, claro);
        c.DrawLine(x, 0, x, h, escuro);
    }

    // ============================================================ cor: contas

    private static (double h, double s, double v) PcToHsv(uint argb)
    {
        double r = ((argb >> 16) & 0xFF) / 255.0, g = ((argb >> 8) & 0xFF) / 255.0, b = (argb & 0xFF) / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = 0;
        if (d > 1e-9)
        {
            if (max == r) h = 60.0 * (((g - b) / d) % 6.0);
            else if (max == g) h = 60.0 * (((b - r) / d) + 2.0);
            else h = 60.0 * (((r - g) / d) + 4.0);
        }
        if (h < 0) h += 360.0;
        return (h, max <= 1e-9 ? 0 : d / max, max);
    }

    private static uint PcFromHsv(double h, double s, double v, double a)
    {
        h = ((h % 360.0) + 360.0) % 360.0;
        double c = v * s, x = c * (1 - Math.Abs((h / 60.0) % 2.0 - 1)), m = v - c;
        double r, g, b;
        switch ((int)(h / 60.0))
        {
            case 0: (r, g, b) = (c, x, 0); break;
            case 1: (r, g, b) = (x, c, 0); break;
            case 2: (r, g, b) = (0, c, x); break;
            case 3: (r, g, b) = (0, x, c); break;
            case 4: (r, g, b) = (x, 0, c); break;
            default: (r, g, b) = (c, 0, x); break;
        }
        static byte B(double t) => (byte)Math.Clamp(Math.Round(t * 255.0), 0, 255);
        return ((uint)B(a) << 24) | ((uint)B(r + m) << 16) | ((uint)B(g + m) << 8) | B(b + m);
    }

    private static uint PcLerp(uint x, uint y, double f)
    {
        f = Math.Clamp(f, 0, 1);
        static uint Canal(uint a, uint b, int shift, double f)
        {
            double va = (a >> shift) & 0xFF, vb = (b >> shift) & 0xFF;
            return (uint)Math.Clamp(Math.Round(va + (vb - va) * f), 0, 255) << shift;
        }
        return Canal(x, y, 24, f) | Canal(x, y, 16, f) | Canal(x, y, 8, f) | Canal(x, y, 0, f);
    }

    private static uint PcEscurecer(uint argb, double f)
    {
        static uint Canal(uint a, int shift, double f)
            => (uint)Math.Clamp(Math.Round(((a >> shift) & 0xFF) * f), 0, 255) << shift;
        return (argb & 0xFF000000u) | Canal(argb, 16, f) | Canal(argb, 8, f) | Canal(argb, 0, f);
    }
}
