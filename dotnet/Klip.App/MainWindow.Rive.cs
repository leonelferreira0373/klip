using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Klip.Engine;
using Klip.Engine.Rive;
using Klip.Model;

namespace Klip.App;

/// <summary>
/// PAINEL RIVE: dar mãos ao runtime .riv que já existia mas não tinha um único botão.
///
/// Três decisões atravessam o ficheiro:
///  · o painel NÃO é montado por si próprio — expõe <see cref="BuildRivePanel"/> e
///    <see cref="SyncRivePanel"/> para quem manda no XAML/ShowTab os chamar. O painel não
///    é dono da aba: se se auto-montasse dentro de um painel alheio (Cor, 3D), roubava-lhe
///    o sítio e o outro painel deixava de aparecer;
///  · escolher a animação é a funcionalidade que faltava e por isso aplica-se JÁ, num
///    Mutate (= um ponto de undo), sem botão de "aplicar" — trocar de animação é barato
///    e reversível, pedir confirmação era ruído;
///  · arrastar um slider é pré-visualização viva SEM histórico (RvLive/Live3D) e largar é
///    UM commit (Commit3D). Sem isto, um arrasto de dois segundos enchia o histórico com
///    dezenas de estados e o Ctrl+Z ficava inútil — é a mesma regra do painel 3D e do Cor.
///
/// Erros e avisos vão para a barra do próprio painel (<see cref="_rvBarra"/>) ou para o
/// Inspector — nunca para diálogos nativos.
/// </summary>
public partial class MainWindow : Window
{
    // ---------------- estado ----------------
    private bool _rvBuilt;      // já foi construído (o coordenador só constrói uma vez)
    private bool _rvSync;       // guarda de re-entrância: o Sync mexe nos sliders e os sliders não podem responder

    private StackPanel? _rvAnimBox, _rvCorpo, _rvSemRive;
    private TextBlock? _rvQuem, _rvFicheiro, _rvArtboard, _rvPlayInfo, _rvMaqTexto, _rvBarra;
    private readonly Dictionary<string, Slider> _rvSl = new();
    private readonly Dictionary<string, TextBlock> _rvVal = new();
    private readonly List<Button> _rvAnimBtns = new();

    /// <summary>
    /// Caminho cuja LISTA de animações já está montada. Existe para o Sync não reconstruir
    /// os botões a cada refresh: reconstruí-los apagava o botão que está debaixo do rato
    /// (o clique perdia-se) e piscava a lista inteira 60×/s durante a reprodução.
    /// </summary>
    private string? _rvListado;

    /// <summary>
    /// Documentos .riv já lidos por este painel. O <see cref="RiveClip"/> só devolve nomes de
    /// animações; para mostrar fps/duração/repetição é preciso o documento, e relê-lo a cada
    /// sincronização era ler o ficheiro do disco dezenas de vezes por segundo.
    /// </summary>
    private readonly Dictionary<string, RiveDocument?> _rvDocs = new(StringComparer.OrdinalIgnoreCase);

    // ============================================================ construção

    /// <summary>
    /// Monta o painel dentro de <paramref name="host"/> (tipicamente o StackPanel de uma aba do
    /// painel direito). Chamar UMA vez; a partir daí só <see cref="SyncRivePanel"/>.
    /// </summary>
    public void BuildRivePanel(Panel host)
    {
        if (host is null) return;
        var st = host.Children;
        st.Clear();
        _rvSl.Clear();
        _rvVal.Clear();
        _rvAnimBtns.Clear();
        _rvListado = null;

        // ---- quem está seleccionado ----
        _rvQuem = new TextBlock { Text = "(sem seleção)", FontSize = 10.5, Foreground = BrHead, Margin = new Thickness(0, 0, 0, 3) };
        st.Add(_rvQuem);

        // ---- camada sem Rive: dizer o que fazer em vez de mostrar controlos mortos ----
        _rvSemRive = new StackPanel { Spacing = 3 };
        _rvSemRive.Children.Add(new TextBlock
        {
            Text = "Esta camada não é Rive.\nLarga um ficheiro .riv sobre a tela (ou usa o verbo insert_rive) e volta aqui.",
            FontSize = 10, Foreground = BrHead, TextWrapping = TextWrapping.Wrap, LineHeight = 13,
        });
        st.Add(_rvSemRive);

        // ---- corpo: só existe quando a camada tem RivePath ----
        _rvCorpo = new StackPanel { Spacing = 2 };

        _rvCorpo.Children.Add(Head3D("FICHEIRO"));
        _rvFicheiro = new TextBlock { Text = "—", FontSize = 10.5, Foreground = BrValue, TextWrapping = TextWrapping.Wrap };
        _rvCorpo.Children.Add(_rvFicheiro);
        _rvArtboard = new TextBlock { Text = "", FontSize = 9.5, Foreground = BrHead };
        _rvCorpo.Children.Add(_rvArtboard);

        // ---- a peça que faltava: escolher QUE animação toca ----
        _rvCorpo.Children.Add(Head3D("ANIMAÇÃO"));
        _rvAnimBox = new StackPanel { Spacing = 3 };
        _rvCorpo.Children.Add(_rvAnimBox);

        _rvCorpo.Children.Add(Head3D("REPRODUÇÃO"));
        _rvPlayInfo = new TextBlock
        {
            Text = "—", FontSize = 10, Foreground = BrValue, TextWrapping = TextWrapping.Wrap, LineHeight = 13,
        };
        _rvCorpo.Children.Add(_rvPlayInfo);
        _rvCorpo.Children.Add(new TextBlock
        {
            // Honestidade em vez de sliders falsos: a velocidade e o modo de repetição vivem DENTRO
            // do .riv (RiveAnimation.Speed/LoopValue) e a camada não tem onde guardar um valor que os
            // substitua. Um slider aqui mexeria em nada e o utilizador culpava o motor.
            Text = "Velocidade e repetição vêm de dentro do .riv. Para as poder mudar aqui, a camada precisa de campos próprios (ver nota ao coordenador).",
            FontSize = 9.5, Foreground = BrHead, TextWrapping = TextWrapping.Wrap, LineHeight = 12,
            Margin = new Thickness(0, 2, 0, 0),
        });

        // ---- enquadramento ----
        _rvCorpo.Children.Add(Head3D("ENQUADRAMENTO"));
        _rvCorpo.Children.Add(RvRow("scale", "Escala", 0.05, 3.0, "0.00", RvCampo.Escala));
        _rvCorpo.Children.Add(RvRow("rive.box", "Caixa", 0.10, 3.0, "0.00", RvCampo.Caixa));

        var botoes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Margin = new Thickness(0, 3, 0, 0) };
        var ajustar = RvMini("Ajustar à tela", "Escolhe a escala que faz o artboard caber na tela, com margem");
        ajustar.Click += (_, _) => RvAjustarATela();
        var original = RvMini("Tamanho original", "Escala 1 e caixa igual ao artboard do ficheiro");
        original.Click += (_, _) => RvTamanhoOriginal();
        botoes.Children.Add(ajustar);
        botoes.Children.Add(original);
        _rvCorpo.Children.Add(botoes);

        // ---- máquinas de estados: escolher qual conduz o desenho, e mexer nos inputs dela ----
        _rvCorpo.Children.Add(Head3D("MÁQUINA DE ESTADOS"));
        _rvMaqBotoes = new StackPanel { Spacing = 2 };
        _rvCorpo.Children.Add(_rvMaqBotoes);
        _rvInputs = new StackPanel { Spacing = 2, Margin = new Thickness(0, 3, 0, 0) };
        _rvCorpo.Children.Add(_rvInputs);
        _rvMaqTexto = new TextBlock
        {
            Text = "—", FontSize = 10, Foreground = BrHead, TextWrapping = TextWrapping.Wrap, LineHeight = 13,
            Margin = new Thickness(0, 3, 0, 0),
        };
        _rvCorpo.Children.Add(_rvMaqTexto);

        st.Add(_rvCorpo);

        // ---- barra do painel: onde os erros aterram (nada de diálogos nativos) ----
        _rvBarra = new TextBlock
        {
            FontSize = 9.5, Foreground = BrHead, TextWrapping = TextWrapping.Wrap, LineHeight = 12,
            Margin = new Thickness(0, 8, 0, 0), IsVisible = false,
        };
        st.Add(_rvBarra);

        _rvBuilt = true;
        SyncRivePanel();
    }

    /// <summary>Que campo do modelo é que este slider ataca — decide para onde vai o preview vivo.</summary>
    private enum RvCampo { Escala, Caixa }

    /// <summary>Linha rótulo · slider · valor · ◆, no molde do painel 3D.</summary>
    private Control RvRow(string key, string label, double min, double max, string fmt, RvCampo campo)
    {
        var g = new Avalonia.Controls.Grid { ColumnDefinitions = new ColumnDefinitions("46,*,32,18"), Height = 22 };

        var lb = new TextBlock { Text = label, FontSize = 10.5, Foreground = BrLabel, VerticalAlignment = VerticalAlignment.Center };
        Avalonia.Controls.Grid.SetColumn(lb, 0); g.Children.Add(lb);

        var sl = new Slider
        {
            Minimum = min, Maximum = max, Height = 22, Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0),
        };
        Avalonia.Controls.Grid.SetColumn(sl, 1); g.Children.Add(sl);
        _rvSl[key] = sl;

        var vt = new TextBlock { FontSize = 10, Foreground = BrValue, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
        Avalonia.Controls.Grid.SetColumn(vt, 2); g.Children.Add(vt);
        _rvVal[key] = vt;

        sl.PropertyChanged += (_, ev) =>
        {
            if (ev.Property.Name != "Value" || _rvSync) return;
            vt.Text = sl.Value.ToString(fmt, CultureInfo.InvariantCulture);
            if (RvLayer() is null) return;   // sem camada Rive, mexer no slider não pode inventar uma
            if (campo == RvCampo.Escala) Live3D("scale", sl.Value, false);   // "scale" é chave do PropRegistry
            else RvLiveCaixa(sl.Value);
        };
        sl.AddHandler(InputElement.PointerReleasedEvent, (_, _) => { if (RvLayer() is not null) Commit3D(); },
                      RoutingStrategies.Tunnel | RoutingStrategies.Bubble);

        // ◆ só faz sentido no que é keyframável: a caixa é geometria do clip, não um track.
        if (campo == RvCampo.Escala)
        {
            var kb = new Button
            {
                Content = "◆", FontSize = 9, Width = 18, Height = 18, Padding = new Thickness(0),
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = BrHead,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            ToolTip.SetTip(kb, "Keyframe da escala no tempo atual da timeline");
            kb.Click += (_, _) => RvKeyframeEscala(sl.Value, kb);
            Avalonia.Controls.Grid.SetColumn(kb, 3); g.Children.Add(kb);
        }
        return g;
    }

    private static Button RvMini(string texto, string dica)
    {
        var b = new Button
        {
            Content = texto, FontSize = 9.5, Height = 20, Padding = new Thickness(6, 0),
            CornerRadius = new CornerRadius(5), Background = PcChip, BorderThickness = new Thickness(0),
        };
        ToolTip.SetTip(b, dica);
        return b;
    }

    // ============================================================ estado / atalhos

    /// <summary>A camada seleccionada, mas SÓ se for mesmo Rive — o painel não deve fingir que é.</summary>
    private Layer? RvLayer()
    {
        var l = _selected >= 0 && _selected < _layers.Count ? _layers[_selected] : null;
        return l?.RivePath is null ? null : l;
    }

    private void RvMsg(string? msg)
    {
        if (_rvBarra is null) return;
        _rvBarra.Text = msg ?? "";
        _rvBarra.IsVisible = !string.IsNullOrEmpty(msg);
    }

    private void RvSafe(Action act)
    {
        try { act(); RvMsg(null); }
        catch (Exception ex) { RvMsg(ex.Message); }
    }

    /// <summary>Documento .riv lido (e guardado) por este painel. null = ilegível.</summary>
    private RiveDocument? RvDoc(string caminho)
    {
        if (_rvDocs.TryGetValue(caminho, out var d)) return d;
        RiveDocument? doc = null;
        try { doc = RiveLoader.Load(File.ReadAllBytes(caminho)); }
        catch { doc = null; }   // ficheiro apagado/corrompido não pode derrubar o painel
        _rvDocs[caminho] = doc;
        return doc;
    }

    /// <summary>Tamanho nativo do artboard. Cai para 400×400 (o mesmo defeito do ApiInsertRive) se não der.</summary>
    private (double w, double h) RvNativo(string caminho)
    {
        try
        {
            if (RiveClip.Info(caminho) is { } i && i.w > 0 && i.h > 0) return (i.w, i.h);
        }
        catch { /* ignorado: o painel usa o defeito */ }
        return (400, 400);
    }

    // ============================================================ escolher a animação

    /// <summary>
    /// Reconstrói os botões das animações. Só corre quando o CAMINHO muda — ver <see cref="_rvListado"/>.
    /// </summary>
    private void RvMontarAnimacoes(string caminho)
    {
        if (_rvAnimBox is null) return;
        _rvAnimBox.Children.Clear();
        _rvAnimBtns.Clear();
        _rvListado = caminho;

        string[] anims;
        try { anims = RiveClip.Info(caminho)?.anims ?? Array.Empty<string>(); }
        catch (Exception ex) { RvMsg("não consegui ler o .riv: " + ex.Message); anims = Array.Empty<string>(); }

        if (anims.Length == 0)
        {
            _rvAnimBox.Children.Add(new TextBlock
            {
                Text = "Este ficheiro não declara animações lineares.", FontSize = 10,
                Foreground = BrHead, TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        for (int i = 0; i < anims.Length; i++)
        {
            string nome = anims[i];
            var b = new Button
            {
                Content = nome.Length > 0 ? nome : $"(sem nome {i})",
                FontSize = 10.5, Height = 22, Padding = new Thickness(8, 0), CornerRadius = new CornerRadius(6),
                Background = PcChip, BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
            };
            ToolTip.SetTip(b, "Toca esta animação nesta camada");
            b.Click += (_, _) => RvEscolherAnimacao(nome);
            _rvAnimBtns.Add(b);
            _rvAnimBox.Children.Add(b);
        }
    }

    /// <summary>Troca a animação da camada. Aplica já — um Mutate = um ponto de undo.</summary>
    private void RvEscolherAnimacao(string nome)
    {
        int ix = _selected;
        if (ix < 0 || ix >= _layers.Count || _layers[ix].RivePath is null)
        { RvMsg("Seleciona primeiro uma camada Rive."); return; }
        if (string.Equals(_layers[ix].RiveAnim, nome, StringComparison.Ordinal)) return;

        RvSafe(() =>
        {
            var l = _layers[ix];
            Mutate(() => _layers[ix] = l with { RiveAnim = nome });
            // Depois do Mutate: ele faz Refresh→UpdateInspector, que reescreveria a mensagem.
            if (Inspector is not null) Inspector.Text = $"Rive: animação “{nome}”.";
        });
        SyncRivePanel();
    }

    /// <summary>
    /// Qual das animações está a tocar. RiveAnim a null NÃO quer dizer "nenhuma": o
    /// <see cref="RivePlayer.Find"/> cai na primeira — e o painel tem de marcar a mesma,
    /// senão a lista aparece toda apagada enquanto o desenho já se mexe.
    /// </summary>
    private static int RvIndiceActual(string[] anims, string? escolhida)
    {
        if (anims.Length == 0) return -1;
        if (string.IsNullOrEmpty(escolhida)) return 0;
        for (int i = 0; i < anims.Length; i++)
            if (string.Equals(anims[i], escolhida, StringComparison.OrdinalIgnoreCase)) return i;
        return 0;   // nome que já não existe no ficheiro → o motor também cai na primeira
    }

    // ============================================================ enquadramento

    /// <summary>
    /// Preview vivo da CAIXA (RiveW×RiveH), sem histórico. O <paramref name="f"/> é um múltiplo do
    /// tamanho nativo do artboard, e não pixels soltos: assim a caixa nunca perde a proporção e o
    /// desenho não estica. O Shape vai junto porque é ele que dá os limites de selecção — mudar só
    /// a caixa deixava a moldura de selecção a apontar para o sítio errado.
    /// </summary>
    private void RvLiveCaixa(double f)
    {
        try
        {
            int ix = _selected;
            if (ix < 0 || ix >= _layers.Count) return;
            var l = _layers[ix];
            if (l.RivePath is not string caminho) return;

            var (nw, nh) = RvNativo(caminho);
            double w = Math.Max(1, nw * f), h = Math.Max(1, nh * f);
            _layers[ix] = l with { RiveW = w, RiveH = h, Shape = MorphTrack.Static(Shapes.Rect(w / 2, h / 2)) };
            InvalidateComp();
            RenderView(_previewT);
        }
        catch { /* o preview nunca pode partir a UI */ }
    }

    private void RvAjustarATela()
    {
        var l = RvLayer();
        if (l?.RivePath is not string caminho) { RvMsg("Seleciona primeiro uma camada Rive."); return; }
        var (nw, nh) = RvNativo(caminho);
        // 0.9 = margem: encostado ao milímetro à borda da tela lê-se como erro, não como enquadramento
        double fit = Math.Min(W * 0.9 / Math.Max(1, nw), H * 0.9 / Math.Max(1, nh));
        int ix = _selected;
        RvSafe(() => Mutate(() => _layers[ix] = _layers[ix] with { Scale = Track.Const(fit) }));
        SyncRivePanel();
    }

    private void RvTamanhoOriginal()
    {
        var l = RvLayer();
        if (l?.RivePath is not string caminho) { RvMsg("Seleciona primeiro uma camada Rive."); return; }
        var (nw, nh) = RvNativo(caminho);
        int ix = _selected;
        RvSafe(() => Mutate(() => _layers[ix] = _layers[ix] with
        {
            Scale = Track.Const(1),
            RiveW = nw, RiveH = nh,
            Shape = MorphTrack.Static(Shapes.Rect(nw / 2, nh / 2)),
        }));
        SyncRivePanel();
    }

    private void RvKeyframeEscala(double v, Button kb)
    {
        var l = RvLayer();
        if (l is null) { RvMsg("Seleciona primeiro uma camada Rive."); return; }
        RvSafe(() =>
        {
            // "inout" e não "ease_in_out": o ParseEase só conhece hold/in/out/inout/outback — tudo o
            // resto cai em Linear e ficava um keyframe que dizia ser suave e saía a direito.
            ApiSetKeyframe(l.Key, "scale", _previewT, v.ToString(CultureInfo.InvariantCulture), "inout");
            kb.Foreground = BrAccent;
            RvMsg($"◆ escala @ {_previewT:0.00}s");
        });
    }

    // ============================================================ máquinas de estados

    /// <summary>
    /// Máquinas de estados já lidas, por caminho. O <see cref="SyncRivePanel"/> corre a cada
    /// Refresh e <see cref="RiveStateMachine.Ler(string)"/> faz File.ReadAllBytes + uma passagem
    /// completa sobre o ficheiro — sem esta cache o painel lia o .riv do disco dezenas de vezes
    /// por segundo durante a reprodução.
    /// </summary>
    private readonly Dictionary<string, IReadOnlyList<RiveMachine>> _rvMaqs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Erro devolvido pelo leitor, por caminho (null = leu bem).</summary>
    private readonly Dictionary<string, string?> _rvMaqErro = new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<RiveMachine> RvMaquinas(string caminho)
    {
        if (_rvMaqs.TryGetValue(caminho, out var cache)) return cache;
        IReadOnlyList<RiveMachine> ms;
        string? erro;
        // Ler(caminho, out erro) já apanha tudo por dentro; o try é só para o caso de um caminho
        // impossível rebentar antes de lá chegar. Um .riv estragado não pode derrubar o painel.
        try { ms = RiveStateMachine.Ler(caminho, out erro); }
        catch (Exception ex) { ms = Array.Empty<RiveMachine>(); erro = ex.Message; }
        _rvMaqs[caminho] = ms;
        _rvMaqErro[caminho] = erro;
        return ms;
    }

    /// <summary>
    /// Descreve as máquinas de estados do ficheiro, em texto, para a zona reservada.
    /// Ligado ao leitor real (<see cref="RiveStateMachine"/>) — era um esboço que devolvia
    /// sempre null, o que fazia a secção dizer "este ficheiro não expõe máquinas" mesmo em
    /// ficheiros cheios delas.
    /// </summary>
    private StackPanel? _rvMaqBotoes, _rvInputs;

    /// <summary>
    /// Escolher qual máquina conduz o desenho. Com uma escolhida, o .riv deixa de tocar uma
    /// animação fixa e passa a ser a máquina a decidir — que é a razão de existir do Rive.
    /// </summary>
    private void RvEscolherMaquina(string? nome)
    {
        int ix = _selected;
        if (ix < 0 || ix >= _layers.Count || _layers[ix].RivePath is null) return;
        RvSafe(() =>
        {
            var l = _layers[ix];
            // trocar de máquina invalida os inputs da anterior — nomes diferentes, valores sem sentido
            Mutate(() => _layers[ix] = l with { RiveMachine = nome, RiveInputs = null });
            if (Inspector is not null)
                Inspector.Text = nome is null ? "Rive: animação linear." : $"Rive: máquina “{nome}”.";
        });
        SyncRivePanel();
    }

    /// <summary>Mexe num input da máquina. É isto que faz a animação REAGIR.</summary>
    private void RvInput(string nome, double valor)
    {
        int ix = _selected;
        if (ix < 0 || ix >= _layers.Count) return;
        RvSafe(() =>
        {
            var l = _layers[ix];
            var d = l.RiveInputs is null
                ? new Dictionary<string, double>()
                : new Dictionary<string, double>(l.RiveInputs);
            d[nome] = valor;
            Mutate(() => _layers[ix] = l with { RiveInputs = d });
        });
        SyncRivePanel();
    }

    /// <summary>Constrói os botões de escolha da máquina e os controlos dos inputs dela.</summary>
    private void RvSyncMaquinas(string caminho)
    {
        if (_rvMaqBotoes is null || _rvInputs is null) return;
        _rvMaqBotoes.Children.Clear();
        _rvInputs.Children.Clear();

        var maqs = RvMaquinas(caminho);
        if (maqs.Count == 0) return;

        var l = _selected >= 0 && _selected < _layers.Count ? _layers[_selected] : null;
        string? activa = l?.RiveMachine;

        var linha = new WrapPanel { Orientation = Orientation.Horizontal };
        Button Chip(string texto, bool on, Action ac)
        {
            var b = new Button
            {
                Content = texto, FontSize = 9.5, Height = 20, Padding = new Thickness(6, 0),
                CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 0, 3, 3),
                BorderThickness = new Thickness(0),
                Background = PcChip, Foreground = on ? BrAccent : BrValue,
                FontWeight = on ? FontWeight.Bold : FontWeight.Normal,
            };
            b.Click += (_, _) => ac();
            return b;
        }

        linha.Children.Add(Chip("linha temporal", activa is null, () => RvEscolherMaquina(null)));
        foreach (var m in maqs)
        {
            var nome = m.Nome;
            linha.Children.Add(Chip(nome, string.Equals(activa, nome, StringComparison.OrdinalIgnoreCase),
                                    () => RvEscolherMaquina(nome)));
        }
        _rvMaqBotoes.Children.Add(linha);

        if (activa is null) return;
        var maq = maqs.FirstOrDefault(m => string.Equals(m.Nome, activa, StringComparison.OrdinalIgnoreCase));
        if (maq is null || maq.Inputs.Count == 0) return;

        foreach (var inp in maq.Inputs)
        {
            double actual = l?.RiveInputs is not null && l.RiveInputs.TryGetValue(inp.Nome, out var v) ? v : inp.Valor;
            var nome = inp.Nome;

            if (inp.Tipo == "gatilho")
            {
                // Um gatilho não tem valor — dispara. Fica ligado enquanto estiver marcado, e a
                // simulação determinística dispara-o no arranque; desmarcar volta atrás.
                var b = new Button
                {
                    Content = (actual != 0 ? "▣ " : "▢ ") + nome, FontSize = 10, Height = 21,
                    Padding = new Thickness(7, 0), CornerRadius = new CornerRadius(5),
                    Background = PcChip, BorderThickness = new Thickness(0),
                    Foreground = actual != 0 ? BrAccent : BrValue,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                };
                b.Click += (_, _) => RvInput(nome, actual != 0 ? 0 : 1);
                _rvInputs.Children.Add(b);
            }
            else if (inp.Tipo == "booleano")
            {
                var b = new Button
                {
                    Content = (actual != 0 ? "☑ " : "☐ ") + nome, FontSize = 10, Height = 21,
                    Padding = new Thickness(7, 0), CornerRadius = new CornerRadius(5),
                    Background = PcChip, BorderThickness = new Thickness(0),
                    Foreground = actual != 0 ? BrAccent : BrValue,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                };
                b.Click += (_, _) => RvInput(nome, actual != 0 ? 0 : 1);
                _rvInputs.Children.Add(b);
            }
            else
            {
                var g = new Avalonia.Controls.Grid { ColumnDefinitions = new ColumnDefinitions("62,*,34"), Height = 22 };
                var lb = new TextBlock { Text = nome, FontSize = 10, Foreground = BrLabel, VerticalAlignment = VerticalAlignment.Center };
                Avalonia.Controls.Grid.SetColumn(lb, 0); g.Children.Add(lb);
                var sl = new Slider { Minimum = 0, Maximum = 100, Value = Math.Clamp(actual, 0, 100), Height = 22, Padding = new Thickness(0) };
                var val = new TextBlock { Text = actual.ToString("0.#"), FontSize = 10, Foreground = BrValue, VerticalAlignment = VerticalAlignment.Center };
                // arrastar só actualiza o número; ao largar é que muda o documento — um só undo
                sl.PropertyChanged += (_, ev) => { if (ev.Property == Slider.ValueProperty) val.Text = sl.Value.ToString("0.#"); };
                sl.AddHandler(PointerReleasedEvent, (_, _) => RvInput(nome, sl.Value),
                              Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble);
                Avalonia.Controls.Grid.SetColumn(sl, 1); g.Children.Add(sl);
                Avalonia.Controls.Grid.SetColumn(val, 2); g.Children.Add(val);
                _rvInputs.Children.Add(g);
            }
        }
    }

    private string RvTextoMaquinas(string caminho)
    {
        var maqs = RvMaquinas(caminho);
        if (_rvMaqErro.TryGetValue(caminho, out var erro) && erro is not null)
            return "não consegui ler as máquinas de estados: " + erro;

        if (maqs.Count == 0)
            return "Este ficheiro não tem máquinas de estados — só animações lineares.";

        var sb = new System.Text.StringBuilder();
        sb.Append(maqs.Count).Append(maqs.Count == 1 ? " máquina:" : " máquinas:");
        foreach (var m in maqs)
        {
            sb.Append("\n· “").Append(m.Nome).Append('”');
            if (!string.IsNullOrEmpty(m.Artboard)) sb.Append("  (artboard ").Append(m.Artboard).Append(')');
            sb.Append("\n   ").Append(m.Camadas.Count).Append(m.Camadas.Count == 1 ? " camada · " : " camadas · ")
              .Append(m.TotalEstados).Append(" estados · ").Append(m.TotalTransicoes).Append(" transições");
            if (m.Inputs.Count > 0)
            {
                sb.Append("\n   inputs: ");
                for (int i = 0; i < m.Inputs.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    var it = m.Inputs[i];
                    sb.Append(it.Nome).Append(" (").Append(it.Tipo).Append(')');
                }
            }
            else sb.Append("\n   sem inputs (corre sozinha)");
        }
        sb.Append("\nOs inputs ainda não são editáveis aqui: a camada não tem onde guardar os valores.");
        return sb.ToString();
    }

    // ============================================================ sincronizar UI ← modelo

    /// <summary>
    /// Põe o painel de acordo com a camada seleccionada. Barato de propósito (é chamado a cada
    /// Refresh): a lista de animações só se reconstrói quando o ficheiro muda.
    /// </summary>
    public void SyncRivePanel()
    {
        if (!_rvBuilt) return;
        _rvSync = true;
        try
        {
            var l = RvLayer();
            var bruta = _selected >= 0 && _selected < _layers.Count ? _layers[_selected] : null;

            if (_rvQuem is not null)
                _rvQuem.Text = bruta is null ? "(sem seleção)" : bruta.Name + (l is null ? "  ·  não é Rive" : "  ·  Rive");
            if (_rvSemRive is not null) _rvSemRive.IsVisible = l is null;
            if (_rvCorpo is not null) _rvCorpo.IsVisible = l is not null;
            if (l?.RivePath is not string caminho) { _rvListado = null; return; }

            // ---- ficheiro ----
            if (_rvFicheiro is not null)
            {
                _rvFicheiro.Text = Path.GetFileName(caminho);
                ToolTip.SetTip(_rvFicheiro, caminho);
            }
            var (nw, nh) = RvNativo(caminho);
            bool existe = false;
            try { existe = File.Exists(caminho); } catch { /* caminho torto conta como inexistente */ }
            if (_rvArtboard is not null)
                _rvArtboard.Text = existe
                    ? $"artboard {nw:0}×{nh:0}  ·  caixa {l.RiveW:0}×{l.RiveH:0}"
                    : "ficheiro não encontrado no disco";

            string[] anims;
            try { anims = RiveClip.Info(caminho)?.anims ?? Array.Empty<string>(); }
            catch { anims = Array.Empty<string>(); }

            // ---- lista de animações (só remonta quando o ficheiro muda) ----
            if (!string.Equals(_rvListado, caminho, StringComparison.OrdinalIgnoreCase))
                RvMontarAnimacoes(caminho);

            int actual = RvIndiceActual(anims, l.RiveAnim);
            for (int i = 0; i < _rvAnimBtns.Count; i++)
            {
                bool on = i == actual;
                _rvAnimBtns[i].Background = on ? BrAccent : PcChip;
                _rvAnimBtns[i].Foreground = on ? Brushes.White : BrValue;
            }

            // ---- reprodução (o que o ficheiro manda) ----
            if (_rvPlayInfo is not null)
                _rvPlayInfo.Text = RvTextoReproducao(caminho, actual >= 0 && actual < anims.Length ? anims[actual] : null);

            // ---- enquadramento ----
            void Por(string k, double v, string fmt)
            {
                if (!_rvSl.TryGetValue(k, out var sl)) return;
                sl.Value = Math.Clamp(v, sl.Minimum, sl.Maximum);
                if (_rvVal.TryGetValue(k, out var vt)) vt.Text = sl.Value.ToString(fmt, CultureInfo.InvariantCulture);
            }
            Por("scale", l.Scale?.Eval(_previewT) ?? 1.0, "0.00");
            Por("rive.box", nw > 0 ? l.RiveW / nw : 1.0, "0.00");

            // ---- máquinas de estados ----
            RvSyncMaquinas(caminho);
            if (_rvMaqTexto is not null) _rvMaqTexto.Text = RvTextoMaquinas(caminho);
        }
        catch (Exception ex) { RvMsg("painel Rive: " + ex.Message); }
        finally { _rvSync = false; }
    }

    /// <summary>Linha de leitura: fps, duração, modo de repetição e velocidade da animação escolhida.</summary>
    private string RvTextoReproducao(string caminho, string? animNome)
    {
        var ab = RvDoc(caminho)?.First;
        if (ab is null) return "não consegui ler a reprodução deste ficheiro.";
        if (ab.Animations.Count == 0) return "sem animações lineares.";

        RiveAnimation? a = null;
        if (!string.IsNullOrEmpty(animNome))
            foreach (var cand in ab.Animations)
                if (string.Equals(cand.Name, animNome, StringComparison.OrdinalIgnoreCase)) { a = cand; break; }
        a ??= ab.Animations[0];

        string modo = a.LoopValue switch
        {
            1 => "ciclo",
            2 => "ida-e-volta",
            _ => "uma vez",
        };
        return $"{a.Fps} fps  ·  {a.DurationSeconds.ToString("0.00", CultureInfo.InvariantCulture)}s "
             + $"({a.DurationFrames} fotogramas)\nrepetição: {modo}  ·  "
             + $"velocidade: {a.Speed.ToString("0.##", CultureInfo.InvariantCulture)}×";
    }
}
