using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Klip.Engine.ThreeD;

namespace Klip.App;

/// <summary>
/// MODO MALHA — editar a geometria A MÃO, dentro do KLIP, sem nunca ver a janela do Blender.
///
/// A ideia toda: o Blender é o MOTOR, as mãos são aqui. Para isso é preciso saber, com exactidão,
/// em que vértice/aresta/face o rato tocou — e é isso que este ficheiro faz. Nada aqui desenha a
/// peça nem fala com o Blender; só transforma um pixel num elemento da malha e mostra-o.
///
/// PORQUÊ ASSIM:
///
/// · As matrizes vêm de <see cref="Hybrid3D.BuildMatrices"/>, AS MESMAS que o render usa. Construir
///   aqui outras seria garantir que, mais cedo ou mais tarde, se aponta para um sítio e a peça está
///   noutro. O Renderer desenha a imagem 3D exactamente por cima do rectângulo [0..W]x[0..H] do
///   comp, portanto o supersampling e a escala de saída cancelam-se e NÃO entram nesta conta.
///
/// · O hit-test é MATEMÁTICO, como no gizmo (ver MainWindow.Gizmo.cs): o Overlay não recebe cliques
///   — é só pintura por cima. Um raio desprojectado atravessa a malha e o triângulo mais próximo
///   ganha. Sem grelha espacial: o teste linear sobre todos os vértices custa décimos de
///   milissegundo nas malhas que o KLIP produz, e uma grelha teria de ser invalidada a cada edição.
///
/// · Os eventos do rato são apanhados em TÚNEL sobre o CanvasHost. O túnel corre antes das mãos
///   normais (mover/escalar/gizmo), portanto marcar o evento como tratado desliga-as sem lhes tocar
///   — e desligar o modo repõe tudo sozinho, porque nada foi alterado.
/// </summary>
public partial class MainWindow : Window
{
    // ---------------------------------------------------------------- estado
    private bool _malhaModo;                 // interruptor
    private char _malhaTipo = 'v';           // 'v' vértice · 'a' aresta · 'f' face
    private bool _malhaLigado;               // handlers de túnel já instalados
    private double _malhaValor = 0.05;       // valor das operações (chanfro, extrusão…)
    private bool _malhaAgarrou;              // o modo malha comeu ESTE toque (só então come o largar)
    private bool _malhaOcupado;              // já há um Blender a correr sobre este .blend

    /// <summary>O que está sob o cursor. tri = índice do PRIMEIRO vértice do triângulo.</summary>
    private (int parte, int tri, int va, int vb, Vector3 impacto)? _malhaSobCursor;

    /// <summary>Selecção acumulada, por vértice. Aresta acrescenta 2, face acrescenta 3.</summary>
    private readonly List<(int parte, int vi)> _malhaSel = new();

    /// <summary>Último ponto tocado (espaço de OBJECTO) e a parte onde caiu.</summary>
    private Vector3? _malhaPonto;
    private int _malhaPontoParte = -1;

    // ---------------------------------------------------------------- pintura
    private Polygon? _mkFace;
    private Line? _mkAresta;
    private Ellipse? _mkVertice;
    private readonly List<Ellipse> _mkSel = new();
    private Border? _malhaBarra;
    private TextBlock? _malhaEstado, _malhaValorLbl;
    private Button? _malhaBtn;
    private readonly List<Button> _malhaBtnsTipo = new();
    private readonly List<Button> _malhaBtnsOp = new();
    private StackPanel? _malhaFerramentas;

    /// <summary>Mensagem inline com prazo. O vigia de 220 ms reescrevia o estado e apagava o
    /// resultado da operação antes de alguém o ler — e é o ÚNICO canal (nada de diálogos).</summary>
    private string _malhaMsg = "";
    private DateTime _malhaMsgAte = DateTime.MinValue;

    private void MalhaDizer(string texto, double segundos = 6)
    {
        _malhaMsg = texto;
        _malhaMsgAte = DateTime.UtcNow.AddSeconds(segundos);
        if (_malhaEstado is not null) _malhaEstado.Text = texto;
    }

    // A vista faz ease no zoom/pan (_vs anima), e o tempo pode andar. Os marcadores têm de seguir,
    // senão ficam colados a pixéis velhos. Um relógio curto só enquanto o modo está ligado.
    private Avalonia.Threading.DispatcherTimer? _malhaRelogio;
    private (double vs, double ox, double oy, double t) _malhaVistaAnt;

    private static readonly IBrush BrMalhaHot = new SolidColorBrush(Color.Parse("#FF8A3D"));
    private static readonly IBrush BrMalhaSel = new SolidColorBrush(Color.Parse("#2FD07A"));

    // ================================================================ arranque
    // OnLoaded já está tomado (MainWindow.Panel3D.cs) e OnKeyDown também: entra-se por aqui, que
    // corre uma vez quando a janela se liga à árvore visual e já tem os controlos do XAML.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        LigarMalha();
    }

    private void LigarMalha()
    {
        if (_malhaLigado) return;
        _malhaLigado = true;
        try
        {
            // TÚNEL: corre de cima para baixo, ANTES dos handlers normais (que são de borbulha).
            // É o que permite roubar o arrasto sem tocar numa linha do código de selecção.
            CanvasHost.AddHandler(InputElement.PointerPressedEvent, MalhaPressed, RoutingStrategies.Tunnel);
            CanvasHost.AddHandler(InputElement.PointerMovedEvent, MalhaMoved, RoutingStrategies.Tunnel);
            CanvasHost.AddHandler(InputElement.PointerReleasedEvent, MalhaReleased, RoutingStrategies.Tunnel);
            ConstruirBarraMalha();
            SincronizarBarraMalha();

            // A selecção de camada muda por muitos caminhos (tela, lista, undo, IA) e nenhum deles é
            // meu para alterar. Um relógio lento é o preço de não tocar em ficheiro alheio — só lê
            // dois campos e liga/desliga uma barra.
            var vigia = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
            vigia.Tick += (_, _) => SincronizarBarraMalha();
            vigia.Start();
        }
        catch { /* sem barra é melhor do que sem app */ }
    }

    /// <summary>Teste BARATO (sem carregar a malha): a camada seleccionada tem ficheiro de malha?</summary>
    private bool MalhaDisponivel()
        => _selected >= 0 && _selected < _layers.Count
           && _layers[_selected].ThreeD?.MeshPath is { Length: > 0 } p && System.IO.File.Exists(p);

    /// <summary>
    /// Há .blend por trás? Sem fonte, o ApiMeshOp recusa — e recusar DEPOIS de o dono escolher
    /// vértices e carregar num botão é pior do que dizer já que aqui não se opera. Escolher
    /// continua a funcionar (serve para medir/apontar); só as ferramentas ficam apagadas.
    /// </summary>
    private bool MalhaTemFonte()
        => _selected >= 0 && _selected < _layers.Count
           && _layers[_selected].ThreeD?.SourceBlend is { Length: > 0 } b && System.IO.File.Exists(b);

    // ================================================================ matemática
    /// <summary>
    /// Lê o float[16] column-major como row-major — ou seja, a TRANSPOSTA, que é a convenção de
    /// vector-linha que <see cref="Vector4.Transform(Vector4, Matrix4x4)"/> usa. É o mesmo truque
    /// que o GltfMesh.NodeMatrix já faz. (Mat4 não tem inversa; System.Numerics tem.)
    /// </summary>
    private static Matrix4x4 MalhaNum(float[] m) => new(
        m[0], m[1], m[2], m[3],
        m[4], m[5], m[6], m[7],
        m[8], m[9], m[10], m[11],
        m[12], m[13], m[14], m[15]);

    /// <summary>Ponto do espaço de OBJECTO → píxel de ECRÃ. Null se ficar atrás da câmara.</summary>
    private Point? MalhaParaEcra(Vector3 p, Matrix4x4 mvpN)
    {
        var c = Vector4.Transform(new Vector4(p, 1f), mvpN);
        if (c.W <= 1e-6f) return null;
        double cx = (c.X / c.W + 1.0) * 0.5 * W;
        double cy = (1.0 - c.Y / c.W) * 0.5 * H;
        return FromCanvas(cx, cy);
    }

    /// <summary>Píxel de ecrã → raio (origem + direcção) JÁ em espaço de OBJECTO.</summary>
    private (Vector3 o, Vector3 d) MalhaRaio(Matrix4x4 invN, Point ecra)
    {
        var c = ToCanvas(ecra);                       // desfaz zoom/pan da vista
        float nx = (float)(2.0 * c.X / W - 1.0);
        float ny = (float)(1.0 - 2.0 * c.Y / H);
        var a = Vector4.Transform(new Vector4(nx, ny, -1f, 1f), invN);
        var b = Vector4.Transform(new Vector4(nx, ny, 1f, 1f), invN);
        var o = new Vector3(a.X, a.Y, a.Z) / a.W;
        var f = new Vector3(b.X, b.Y, b.Z) / b.W;
        return (o, Vector3.Normalize(f - o));
    }

    /// <summary>Möller–Trumbore. t = distância ao longo do raio; só conta se estiver à frente.</summary>
    private static bool MalhaTri(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c, out float t)
    {
        t = 0;
        var e1 = b - a; var e2 = c - a;
        var pv = Vector3.Cross(d, e2);
        float det = Vector3.Dot(e1, pv);
        if (MathF.Abs(det) < 1e-12f) return false;    // raio paralelo ao triângulo
        float inv = 1f / det;
        var tv = o - a;
        float u = Vector3.Dot(tv, pv) * inv;
        if (u < -1e-6f || u > 1f + 1e-6f) return false;
        var qv = Vector3.Cross(tv, e1);
        float v = Vector3.Dot(d, qv) * inv;
        if (v < -1e-6f || u + v > 1f + 1e-6f) return false;
        t = Vector3.Dot(e2, qv) * inv;
        return t > 1e-6f;
    }

    private static Vector3 MalhaV(IReadOnlyList<GltfMesh.Part> ps, int parte, int vi)
    {
        var d = ps[parte].Data;
        return new Vector3(d[vi * 8], d[vi * 8 + 1], d[vi * 8 + 2]);   // 8 floats/vértice: pos,nrm,uv
    }

    // A cache de partes do GltfMesh é um Dictionary simples. Até agora só a thread «klip-3d» lhe
    // tocava; o modo malha passou a chamá-la da thread da UI A CADA MOVIMENTO DO RATO, e a operação
    // chama-a de um worker. Três threads a escreverem no mesmo Dictionary é como se parte um
    // Dictionary (ciclo infinito a 100% de CPU, não excepção). Não posso trancar o ficheiro do
    // leitor — não é meu —, mas posso não ser eu a bater lá: memória local, uma entrada, com
    // tranca. Poupa também o stat de disco por movimento.
    private static readonly object _malhaCacheTranca = new();
    private static string _malhaCacheChave = "";
    private static IReadOnlyList<GltfMesh.Part>? _malhaCachePartes;

    internal static IReadOnlyList<GltfMesh.Part>? MalhaPartes(Klip.Model.Layer l)
    {
        if (l.ThreeD?.MeshPath is not { Length: > 0 } p) return null;
        string chave;
        try
        {
            if (!System.IO.File.Exists(p)) return null;
            chave = p + "|" + System.IO.File.GetLastWriteTimeUtc(p).Ticks;
        }
        catch { return null; }

        lock (_malhaCacheTranca)
        {
            if (chave == _malhaCacheChave && _malhaCachePartes is { Count: > 0 }) return _malhaCachePartes;
            var ps = Hybrid3D.PartsOf(l);
            if (ps is null || ps.Count == 0) return null;
            _malhaCacheChave = chave; _malhaCachePartes = ps;
            return ps;
        }
    }

    /// <summary>A camada 3D com malha que está seleccionada — ou null (e então não há modo malha).</summary>
    private (Klip.Model.Layer camada, IReadOnlyList<GltfMesh.Part> partes)? MalhaAlvo()
    {
        if (_selected < 0 || _selected >= _layers.Count) return null;
        var l = _layers[_selected];
        var ps = MalhaPartes(l);
        if (ps is null) return null;
        return (l, ps);
    }

    /// <summary>MVP e a sua inversa no instante MOSTRADO (o zoom/pan e o tempo andam — lê-se agora).</summary>
    private (Matrix4x4 mvp, Matrix4x4 inv)? MalhaMatrizes(Klip.Model.Layer camada)
    {
        var (_, _, _, mvp, _) = Hybrid3D.BuildMatrices(BuildComp(), camada, _previewT);
        var mvpN = MalhaNum(mvp);
        if (!Matrix4x4.Invert(mvpN, out var inv)) return null;
        return (mvpN, inv);
    }

    /// <summary>
    /// O ELEMENTO sob este píxel: triângulo mais próximo atingido pelo raio e, dentro dele, o
    /// vértice mais perto e a aresta mais perto do ponto de impacto.
    /// </summary>
    private (int parte, int tri, int va, int vb, Vector3 impacto)? MalhaApanhar(Point ecra)
    {
        if (MalhaAlvo() is not { } alvo) return null;
        if (MalhaMatrizes(alvo.camada) is not { } mm) return null;
        var (o, d) = MalhaRaio(mm.inv, ecra);

        float melhorT = float.MaxValue; int mp = -1, mt = -1;
        var ps = alvo.partes;
        for (int p = 0; p < ps.Count; p++)
        {
            var data = ps[p].Data; int n = ps[p].Count;
            for (int k = 0; k + 2 < n; k += 3)
            {
                var a = new Vector3(data[k * 8], data[k * 8 + 1], data[k * 8 + 2]);
                var b = new Vector3(data[(k + 1) * 8], data[(k + 1) * 8 + 1], data[(k + 1) * 8 + 2]);
                var c = new Vector3(data[(k + 2) * 8], data[(k + 2) * 8 + 1], data[(k + 2) * 8 + 2]);
                if (MalhaTri(o, d, a, b, c, out float tt) && tt < melhorT)
                { melhorT = tt; mp = p; mt = k; }
            }
        }
        if (mp < 0) return null;

        var hit = o + d * melhorT;
        var v0 = MalhaV(ps, mp, mt); var v1 = MalhaV(ps, mp, mt + 1); var v2 = MalhaV(ps, mp, mt + 2);

        // vértice mais próximo do impacto
        int va = mt; double best = (v0 - hit).Length();
        if ((v1 - hit).Length() < best) { best = (v1 - hit).Length(); va = mt + 1; }
        if ((v2 - hit).Length() < best) { va = mt + 2; }

        // aresta mais próxima: distância do impacto a cada um dos três segmentos
        int ea = mt, eb = mt + 1; double bestE = double.MaxValue;
        for (int k = 0; k < 3; k++)
        {
            int i0 = mt + k, i1 = mt + (k + 1) % 3;
            double dd = MalhaDistSeg(hit, MalhaV(ps, mp, i0), MalhaV(ps, mp, i1));
            if (dd < bestE) { bestE = dd; ea = i0; eb = i1; }
        }

        // devolve-se o vértice em «va» e a aresta em «va,vb» conforme o modo — quem lê decide
        return _malhaTipo == 'a' ? (mp, mt, ea, eb, hit) : (mp, mt, va, va, hit);
    }

    private static double MalhaDistSeg(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = b - a; float len2 = Vector3.Dot(ab, ab);
        if (len2 < 1e-20f) return (p - a).Length();
        float t = Math.Clamp(Vector3.Dot(p - a, ab) / len2, 0f, 1f);
        return (p - (a + ab * t)).Length();
    }

    // ================================================================ eventos (túnel)
    /// <summary>
    /// O túnel passa por TUDO o que está dentro do CanvasHost — e dentro do CanvasHost não vive só
    /// a tela: vive o GuideLayer (✕ das guias + esta barra) e vive a BARRA CONTEXTUAL (CtxBar),
    /// que está visível SEMPRE que há uma camada seleccionada, ou seja sempre que o modo malha é
    /// possível. Marcar o toque como tratado ali matava esses botões todos.
    ///
    /// Por isso a regra é pela positiva e não pela negativa: o modo malha só come o toque quando
    /// ele nasceu na SUPERFÍCIE DE DESENHO — a Image «Canvas» (ou o próprio CanvasHost, quando a
    /// Image ainda não tem Source). O Overlay não é hit-testável e o GuideLayer é um Canvas sem
    /// fundo, portanto nenhum dos dois aparece como origem a não ser pelos filhos deles.
    /// </summary>
    private bool MalhaNaTela(object? origem)
        => ReferenceEquals(origem, Canvas) || ReferenceEquals(origem, CanvasHost);

    private void MalhaPressed(object? s, PointerPressedEventArgs e)
    {
        _malhaAgarrou = false;
        if (!_malhaModo || !MalhaNaTela(e.Source)) return;
        var props = e.GetCurrentPoint(CanvasHost).Properties;
        // pan (botão do meio / mão / espaço) continua a ser pan — não se rouba a navegação
        if (!props.IsLeftButtonPressed || _handTool || _spaceDown) return;

        var p = e.GetPosition(CanvasHost);
        _malhaSobCursor = MalhaApanhar(p);
        bool acrescentar = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (!acrescentar) _malhaSel.Clear();

        if (_malhaSobCursor is { } h)
        {
            _malhaPonto = h.impacto; _malhaPontoParte = h.parte;
            foreach (var vi in MalhaIndicesDoAlvo(h)) MalhaAlternar(h.parte, vi, acrescentar);
        }
        else if (!acrescentar) { _malhaPonto = null; _malhaPontoParte = -1; }

        DesenharMalha();
        SincronizarBarraMalha();
        _malhaAgarrou = true;
        e.Handled = true;      // o arrasto NÃO move a camada enquanto o modo malha está ligado
    }

    private void MalhaMoved(object? s, PointerEventArgs e)
    {
        if (!_malhaModo || _panning || !MalhaNaTela(e.Source)) return;
        _malhaSobCursor = MalhaApanhar(e.GetPosition(CanvasHost));
        DesenharMalha();
        e.Handled = true;
    }

    /// <summary>
    /// SÓ se come o largar quando se comeu o agarrar. Comer sempre parecia inofensivo e não era:
    /// o OnReleased é quem põe <c>_panning = false</c> e repõe o cursor. Um pan (botão do meio, ou
    /// mão/espaço) feito com o modo malha ligado deixava a tela AGARRADA para sempre — e desligar
    /// o modo não a soltava, porque o estado que ficou preso é do outro ficheiro.
    /// </summary>
    private void MalhaReleased(object? s, PointerReleasedEventArgs e)
    {
        bool comeu = _malhaAgarrou;
        _malhaAgarrou = false;
        // largar EM CIMA de um controlo (arrastou-se da tela para a barra) nunca se come:
        // seria um clique perdido no botão que está por baixo do dedo
        if (!_malhaModo || !comeu || _panning || !MalhaNaTela(e.Source)) return;
        e.Handled = true;      // sem isto, o OnReleased empurrava um ponto de undo por cada clique
    }

    /// <summary>Que vértices é que este toque selecciona, conforme o modo (1 · 2 · 3).</summary>
    private IEnumerable<int> MalhaIndicesDoAlvo((int parte, int tri, int va, int vb, Vector3 impacto) h)
    {
        if (_malhaTipo == 'v') { yield return h.va; yield break; }
        if (_malhaTipo == 'a') { yield return h.va; yield return h.vb; yield break; }
        yield return h.tri; yield return h.tri + 1; yield return h.tri + 2;
    }

    private void MalhaAlternar(int parte, int vi, bool acrescentar)
    {
        int ix = _malhaSel.IndexOf((parte, vi));
        if (ix >= 0) { if (acrescentar) _malhaSel.RemoveAt(ix); }   // shift em algo já escolhido = tirar
        else _malhaSel.Add((parte, vi));
    }

    // ================================================================ desenho no Overlay
    private void DesenharMalha()
    {
        GarantirMarcadores();
        if (!_malhaModo || MalhaAlvo() is not { } alvo || MalhaMatrizes(alvo.camada) is not { } mm)
        { EsconderMarcadores(); return; }

        var ps = alvo.partes;
        var mvp = mm.mvp;

        // --- realce do que está sob o cursor ---
        _mkFace!.IsVisible = false; _mkAresta!.IsVisible = false; _mkVertice!.IsVisible = false;
        if (_malhaSobCursor is { } h && h.parte < ps.Count)
        {
            if (_malhaTipo == 'f')
            {
                var a = MalhaParaEcra(MalhaV(ps, h.parte, h.tri), mvp);
                var b = MalhaParaEcra(MalhaV(ps, h.parte, h.tri + 1), mvp);
                var c = MalhaParaEcra(MalhaV(ps, h.parte, h.tri + 2), mvp);
                if (a is { } pa && b is { } pb && c is { } pc)
                {
                    _mkFace.Points = new List<Point> { pa, pb, pc };
                    _mkFace.IsVisible = true;
                }
            }
            else if (_malhaTipo == 'a')
            {
                var a = MalhaParaEcra(MalhaV(ps, h.parte, h.va), mvp);
                var b = MalhaParaEcra(MalhaV(ps, h.parte, h.vb), mvp);
                if (a is { } pa && b is { } pb)
                {
                    _mkAresta.StartPoint = pa; _mkAresta.EndPoint = pb;
                    _mkAresta.IsVisible = true;
                }
            }
            else if (MalhaParaEcra(MalhaV(ps, h.parte, h.va), mvp) is { } pv)
            {
                Avalonia.Controls.Canvas.SetLeft(_mkVertice, pv.X - 5);
                Avalonia.Controls.Canvas.SetTop(_mkVertice, pv.Y - 5);
                _mkVertice.IsVisible = true;
            }
        }

        // --- marcadores da selecção (uma bola por vértice escolhido) ---
        while (_mkSel.Count < _malhaSel.Count)
        {
            var el = new Ellipse
            {
                Width = 8, Height = 8, Fill = BrMalhaSel,
                Stroke = Brushes.White, StrokeThickness = 1.2, IsVisible = false,
            };
            _mkSel.Add(el);
            Overlay.Children.Add(el);
        }
        for (int i = 0; i < _mkSel.Count; i++)
        {
            if (i >= _malhaSel.Count) { _mkSel[i].IsVisible = false; continue; }
            var (parte, vi) = _malhaSel[i];
            if (parte >= ps.Count || vi >= ps[parte].Count) { _mkSel[i].IsVisible = false; continue; }
            if (MalhaParaEcra(MalhaV(ps, parte, vi), mvp) is not { } sp) { _mkSel[i].IsVisible = false; continue; }
            Avalonia.Controls.Canvas.SetLeft(_mkSel[i], sp.X - 4);
            Avalonia.Controls.Canvas.SetTop(_mkSel[i], sp.Y - 4);
            _mkSel[i].IsVisible = true;
        }

        _malhaVistaAnt = (_vs, _vox, _voy, _previewT);
    }

    private void GarantirMarcadores()
    {
        if (_mkFace is not null) return;
        _mkFace = new Polygon
        {
            Fill = new SolidColorBrush(Color.Parse("#FF8A3D"), 0.28),
            Stroke = BrMalhaHot, StrokeThickness = 1.4, IsVisible = false,
        };
        _mkAresta = new Line { Stroke = BrMalhaHot, StrokeThickness = 2.6, IsVisible = false };
        _mkVertice = new Ellipse
        {
            Width = 10, Height = 10, Fill = BrMalhaHot,
            Stroke = Brushes.White, StrokeThickness = 1.4, IsVisible = false,
        };
        Overlay.Children.Add(_mkFace);
        Overlay.Children.Add(_mkAresta);
        Overlay.Children.Add(_mkVertice);
    }

    private void EsconderMarcadores()
    {
        if (_mkFace is not null) _mkFace.IsVisible = false;
        if (_mkAresta is not null) _mkAresta.IsVisible = false;
        if (_mkVertice is not null) _mkVertice.IsVisible = false;
        foreach (var el in _mkSel) el.IsVisible = false;
    }

    // ================================================================ interruptor
    private void AlternarModoMalha(object? s, RoutedEventArgs e)
    {
        if (!_malhaModo && !MalhaDisponivel())
        {
            MalhaDizer("esta camada não tem malha");
            return;
        }
        _malhaModo = !_malhaModo;

        if (_malhaModo)
        {
            // um arrasto a meio ficaria pendurado; o gizmo idem
            _drag = Drag.None;
            _gizAxis = -1; _gizHover = -1;
            _malhaRelogio ??= new Avalonia.Threading.DispatcherTimer
            { Interval = TimeSpan.FromMilliseconds(40) };
            _malhaRelogio.Tick -= MalhaTick; _malhaRelogio.Tick += MalhaTick;
            _malhaRelogio.Start();
        }
        else
        {
            _malhaRelogio?.Stop();
            _malhaSobCursor = null;
            _malhaSel.Clear();
            _malhaAgarrou = false;
            _malhaPonto = null; _malhaPontoParte = -1;
            EsconderMarcadores();
        }
        DesenharMalha();
        SincronizarBarraMalha();
    }

    /// <summary>A vista faz ease e o tempo pode andar — se algo mexeu, reprojectar os marcadores.</summary>
    private void MalhaTick(object? s, EventArgs e)
    {
        if (!_malhaModo) { _malhaRelogio?.Stop(); return; }
        if (Math.Abs(_vs - _malhaVistaAnt.vs) < 1e-9 && Math.Abs(_vox - _malhaVistaAnt.ox) < 1e-9 &&
            Math.Abs(_voy - _malhaVistaAnt.oy) < 1e-9 && Math.Abs(_previewT - _malhaVistaAnt.t) < 1e-9)
            return;
        DesenharMalha();
    }

    // ================================================================ API para o outro agente
    /// <summary>Último ponto tocado na malha, em espaço de OBJECTO, e a parte onde caiu.</summary>
    internal (Vector3 ponto, int parte)? MeshPontoSeleccionado()
        => _malhaPonto is { } p ? (p, _malhaPontoParte) : null;

    /// <summary>Vértices seleccionados, em espaço de OBJECTO (converter com Part.ParaBlender).</summary>
    internal List<Vector3> MeshSeleccao()
    {
        var r = new List<Vector3>();
        if (MalhaAlvo() is not { } alvo) return r;
        foreach (var (parte, vi) in _malhaSel)
            if (parte < alvo.partes.Count && vi < alvo.partes[parte].Count)
                r.Add(MalhaV(alvo.partes, parte, vi));
        return r;
    }

    /// <summary>
    /// A mesma selecção já em coordenadas do .blend (desnormalizada + Z-up).
    /// NÃO é isto que se manda ao <see cref="ApiMeshOp"/>: esse recebe espaço de OBJECTO e faz a
    /// conversão ele próprio. Existe para quem precise das coordenadas do .blend em cru.
    /// </summary>
    internal List<Vector3> MeshSeleccaoBlender()
    {
        var r = new List<Vector3>();
        if (MalhaAlvo() is not { } alvo) return r;
        foreach (var (parte, vi) in _malhaSel)
            if (parte < alvo.partes.Count && vi < alvo.partes[parte].Count)
                r.Add(alvo.partes[parte].ParaBlender(MalhaV(alvo.partes, parte, vi)));
        return r;
    }

    /// <summary>Índices crus da selecção — útil para quem quiser reconstruir arestas/faces.</summary>
    internal IReadOnlyList<(int parte, int vi)> MeshSeleccaoIndices() => _malhaSel;

    /// <summary>Modo de selecção actual: 'v' vértice · 'a' aresta · 'f' face.</summary>
    internal char MeshModoSeleccao() => _malhaTipo;

    /// <summary>true enquanto o modo malha está a comer os cliques do canvas.</summary>
    internal bool MeshModoLigado() => _malhaModo;

    // ================================================================ barra flutuante
    private void ConstruirBarraMalha()
    {
        if (_malhaBarra is not null || GuideLayer is null) return;

        var linha = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };

        _malhaBtn = BotaoMalha("⬡ Malha", "Editar a geometria à mão (o Blender faz o trabalho, tu mandas)");
        _malhaBtn.Click += AlternarModoMalha;
        linha.Children.Add(_malhaBtn);

        linha.Children.Add(SeparadorMalha());

        foreach (var (t, txt, dica) in new[]
        {
            ('v', "•", "Modo vértice"),
            ('a', "╱", "Modo aresta"),
            ('f', "◣", "Modo face"),
        })
        {
            var b = BotaoMalha(txt, dica);
            b.Tag = t;
            b.Click += (_, _) =>
            {
                _malhaTipo = t;
                _malhaSel.Clear(); _malhaSobCursor = null;
                DesenharMalha(); SincronizarBarraMalha();
            };
            _malhaBtnsTipo.Add(b);
            linha.Children.Add(b);
        }

        linha.Children.Add(SeparadorMalha());

        // ferramentas: só aparecem com o modo ligado — barra curta é barra que se lê
        // OS VERBOS SÃO OS DO MOTOR, à letra. Estavam em inglês («bevel», «extrude», …) e o
        // catálogo do MeshOps está em português — nenhum dos seis botões chegava a arrancar o
        // Blender: o ApiMeshOp recusava logo com «não conheço a operação». Ver MeshOps.Catalogo.
        _malhaFerramentas = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, IsVisible = false };
        foreach (var (op, txt, dica) in new[]
        {
            ("bisel",      "Chanfrar",  "Chanfrar as arestas tocadas (valor = largura)"),
            ("extrudir",   "Extrudir",  "Puxar as faces tocadas para fora (valor = altura)"),
            ("inset",      "Moldura",   "Face mais pequena dentro das faces tocadas (valor = espessura)"),
            ("subdividir", "Subdividir","Dividir as faces tocadas em mais faces (valor ignorado)"),
            ("suavizar",   "Suavizar",  "Relaxar os vértices tocados (valor = força 0..1)"),
            ("apagar",     "Apagar",    "Apagar as faces tocadas (abre um buraco)"),
            ("fundir",     "Unir",      "Fundir os vértices tocados num só ponto"),
        })
        {
            var b = BotaoMalha(txt, dica);
            b.Click += (_, _) => ExecutarOpMalha(op);
            _malhaBtnsOp.Add(b);
            _malhaFerramentas.Children.Add(b);
        }

        // valor da operação (espessura do chanfro, altura da extrusão…): slider, nunca caixa de diálogo
        var sl = new Slider { Minimum = 0.005, Maximum = 0.5, Value = _malhaValor, Width = 86, Height = 20 };
        _malhaValorLbl = new TextBlock
        {
            Text = _malhaValor.ToString("0.###", CultureInfo.InvariantCulture),
            FontSize = 10.5, Foreground = BrLabel, VerticalAlignment = VerticalAlignment.Center, Width = 32,
        };
        sl.PropertyChanged += (_, ev) =>
        {
            if (ev.Property != Slider.ValueProperty) return;
            _malhaValor = sl.Value;
            if (_malhaValorLbl is not null)
                _malhaValorLbl.Text = _malhaValor.ToString("0.###", CultureInfo.InvariantCulture);
        };
        _malhaFerramentas.Children.Add(sl);
        _malhaFerramentas.Children.Add(_malhaValorLbl);
        linha.Children.Add(_malhaFerramentas);

        _malhaEstado = new TextBlock
        {
            Text = "", FontSize = 10.5, Foreground = BrHead,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0),
        };
        linha.Children.Add(_malhaEstado);

        _malhaBarra = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#FAFAF9"), 0.96),
            BorderBrush = new SolidColorBrush(Color.Parse("#E4E4E1")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(6, 5),
            Child = linha,
            IsVisible = false,
        };
        Avalonia.Controls.Canvas.SetLeft(_malhaBarra, 14);
        Avalonia.Controls.Canvas.SetTop(_malhaBarra, 14);
        GuideLayer.Children.Add(_malhaBarra);
    }

    private static Button BotaoMalha(string txt, string dica)
    {
        var b = new Button
        {
            Content = txt, FontSize = 10.5, Height = 22, Padding = new Thickness(8, 0),
            CornerRadius = new CornerRadius(6), VerticalContentAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(b, dica);
        return b;
    }

    private static Border SeparadorMalha() => new()
    {
        Width = 1, Height = 16, Margin = new Thickness(3, 0),
        Background = new SolidColorBrush(Color.Parse("#E4E4E1")),
    };

    /// <summary>A barra só existe quando faz sentido, e diz sempre em que pé está a selecção.</summary>
    private void SincronizarBarraMalha()
    {
        if (_malhaBarra is null) return;
        bool podeMalha = MalhaDisponivel();
        if (!podeMalha && _malhaModo)
        {
            // trocou-se de camada com o modo ligado → sair sozinho, senão os cliques ficavam presos
            _malhaModo = false; _malhaRelogio?.Stop();
            _malhaSel.Clear(); _malhaSobCursor = null;
            _malhaAgarrou = false;
            EsconderMarcadores();
        }
        _malhaBarra.IsVisible = podeMalha;
        if (!podeMalha) return;

        if (_malhaBtn is not null)
        {
            _malhaBtn.Foreground = new SolidColorBrush(Color.Parse(_malhaModo ? "#6D5EF6" : "#5E5E5B"));
            _malhaBtn.FontWeight = _malhaModo ? FontWeight.SemiBold : FontWeight.Normal;
        }
        foreach (var b in _malhaBtnsTipo)
        {
            bool on = b.Tag is char c && c == _malhaTipo;
            b.IsEnabled = _malhaModo;
            b.Foreground = new SolidColorBrush(Color.Parse(on && _malhaModo ? "#6D5EF6" : "#8A8A87"));
        }
        if (_malhaFerramentas is not null) _malhaFerramentas.IsVisible = _malhaModo;

        // sem .blend não há operação possível (o ApiMeshOp recusa), e enquanto um Blender corre
        // um segundo processo sobre o MESMO ficheiro dava-o corrompido
        bool podeOperar = _malhaModo && MalhaTemFonte() && !_malhaOcupado;
        foreach (var b in _malhaBtnsOp) b.IsEnabled = podeOperar;

        if (_malhaEstado is null) return;
        // uma mensagem com prazo manda sobre o estado — senão o resultado da operação era
        // apagado 220 ms depois de aparecer, e é o único sítio onde ele se lê
        if (DateTime.UtcNow < _malhaMsgAte) { _malhaEstado.Text = _malhaMsg; return; }
        _malhaEstado.Text =
            !_malhaModo ? "" :
            _malhaOcupado ? "a operar…" :
            !MalhaTemFonte() ? "sem fonte .blend — dá para escolher, não para operar" :
            _malhaSel.Count == 0 ? "toca na peça (shift = juntar)" : $"{_malhaSel.Count} ponto(s)";
    }

    // ================================================================ ponte para as operações
    // (Era por reflexão enquanto os dois ficheiros eram escritos em paralelo. O ApiMeshOp já
    // aterrou no MainWindow.MeshOps.cs — chamada directa, que o compilador verifica.)

    /// <summary>
    /// A selecção em JSON, em espaço de OBJECTO do KLIP. Formato: [[x,y,z], …].
    ///
    /// ATENÇÃO — ESTAVA AQUI O ERRO QUE FAZIA CLICAR NUM SÍTIO E EDITAR NOUTRO: mandava-se
    /// <c>MeshSeleccaoBlender()</c>, ou seja pontos JÁ passados por Part.ParaBlender, e o ApiMeshOp
    /// volta a aplicar ParaBlender a tudo o que recebe (o contrato dele é espaço de objecto).
    /// A conversão corria DUAS VEZES: desnormalizava-se em cima do que já estava desnormalizado e
    /// trocava-se o eixo vertical outra vez. E não falhava com barulho — o «mais próximo é sempre
    /// incluído» do lado do Blender garante que algo é sempre apanhado, portanto a operação
    /// aplicava-se calmamente no sítio errado.
    /// </summary>
    private string MalhaSeleccaoJson()
    {
        var sb = new StringBuilder("[");
        var pts = MeshSeleccao();
        for (int i = 0; i < pts.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('[')
              .Append(pts[i].X.ToString("R", CultureInfo.InvariantCulture)).Append(',')
              .Append(pts[i].Y.ToString("R", CultureInfo.InvariantCulture)).Append(',')
              .Append(pts[i].Z.ToString("R", CultureInfo.InvariantCulture)).Append(']');
        }
        return sb.Append(']').ToString();
    }

    private void ExecutarOpMalha(string operacao)
    {
        if (_malhaOcupado) { MalhaDizer("espera — já há uma operação a correr"); return; }
        if (MalhaAlvo() is not { } alvo) return;
        if (_malhaSel.Count == 0) { MalhaDizer("escolhe alguma coisa primeiro"); return; }
        if (!MalhaTemFonte())
        {
            MalhaDizer("esta peça não tem .blend — só objectos feitos com blender_object se editam");
            return;
        }
        string id = alvo.camada.Key;
        string json = MalhaSeleccaoJson();     // espaço de OBJECTO — o ApiMeshOp é que converte
        double valor = _malhaValor;
        int quantos = _malhaSel.Count;

        _malhaOcupado = true;
        MalhaDizer(operacao + "… (o Blender leva ~7 s)", 600);
        SincronizarBarraMalha();

        // O Blender demora — nunca na thread da UI. O resultado volta pelo chat, como as outras pontes.
        System.Threading.Tasks.Task.Run(() =>
        {
            object? r = null; string? falha = null;
            try { r = ApiMeshOp(id, operacao, json, valor); }
            catch (Exception ex) { falha = ex.Message; }

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _malhaOcupado = false;
                // os índices da malha antiga deixam de valer assim que a geometria muda
                _malhaSel.Clear(); _malhaSobCursor = null;
                _malhaPonto = null; _malhaPontoParte = -1;
                InvalidateComp();
                RenderView(_previewT);
                DesenharMalha();
                MalhaDizer(falha is null ? $"{operacao}: {quantos} ponto(s) ✓" : "falhou — ver chat");
                SincronizarBarraMalha();
            });

            UiChat("·", falha is null
                ? $"malha «{id}»: {operacao} sobre {quantos} ponto(s) → {r}"
                : $"malha «{id}»: {operacao} falhou — {falha}");
        });
    }
}
