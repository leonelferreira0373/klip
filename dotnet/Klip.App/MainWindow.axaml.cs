using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Klip.Engine;
using Klip.Model;

namespace Klip.App;

public partial class MainWindow : Window
{
    private const int W = 1000, H = 700;
    private const double Grid = 20;

    private List<Layer> _layers = new();
    private int _selected = -1;
    private enum Drag { None, Move, Resize }
    private Drag _drag = Drag.None;
    private Point _lastCanvas;
    private double _resizeStartDist, _resizeStartScale;
    private bool _snap;
    private int _nameSeq = 1;
    private string _defaultFamily = "Segoe UI";                                   // fonte por omissão dos insert_text
    private readonly Dictionary<string, (string text, float size)> _textMeta = new();  // texto+size p/ re-bake em set_font

    private readonly List<List<Layer>> _hist = new();
    private int _histIx = -1;

    private static readonly uint[] Palette = { 0xFF6D5EF6, 0xFFFF5A5F, 0xFF232326, 0xFFF5B82E, 0xFF12B5A5 };
    private int _palIx;

    public MainWindow()
    {
        InitializeComponent();






        // arranca VAZIO num artboard branco (sem estrela/acento nem fundo creme)

        // handlers no CONTENTOR (sempre dimensionado) — a Image só ganha tamanho depois de ter Source
        CanvasHost.PointerPressed += OnPressed;
        CanvasHost.PointerMoved += OnMoved;
        CanvasHost.PointerReleased += OnReleased;
        CanvasHost.PointerWheelChanged += OnWheel;
        // pinch do trackpad → zoom suave
        CanvasHost.AddHandler(Avalonia.Input.InputElement.PinchEvent, OnPinch);
        CanvasHost.AddHandler(Avalonia.Input.InputElement.PinchEndedEvent, OnPinchEnded);
        // arrastar guias das réguas (CorelDRAW): topo→horizontal, esquerda→vertical
        RulerTop.PointerPressed += (s, e) => OnRulerPressed(s, e, 'h');
        RulerTop.PointerMoved += OnRulerMoved;
        RulerTop.PointerReleased += OnRulerReleased;
        RulerLeft.PointerPressed += (s, e) => OnRulerPressed(s, e, 'v');
        RulerLeft.PointerMoved += OnRulerMoved;
        RulerLeft.PointerReleased += OnRulerReleased;
        CanvasHost.PointerExited += (_, _) => { if (HoverBox is not null) HoverBox.IsVisible = false; };
        LayoutUpdated += (_, _) =>
        {
            var b = CanvasHost.Bounds;
            if (b.Width > 50 && b.Height > 50)
            {
                // primeira vez, OU o viewport cresceu/mudou muito e o utilizador ainda não deu zoom/pan
                bool grew = Math.Abs(b.Width - _lastBoundsW) > 2 || Math.Abs(b.Height - _lastBoundsH) > 2;
                if (!_viewInit || (grew && !_viewUserAdjusted))
                {
                    InitView();
                    RenderView(_playTimer is null ? 0 : _playT);
                }
            }
            UpdateOverlay();
        };

        PushHistory();
        Refresh();

        // AI-first: command bus + control server — ID único por editor (multi-composição independente)
        _registry = new Ai.ActionRegistry(this);
        var instanceId = (_instanceSeq++ == 0) ? "main" : "c" + Guid.NewGuid().ToString("N")[..6];
        _api = new Ai.AnthropicBackend(_registry);
        _cli = new Ai.ClaudeCliBackend();
        try
        {
            _control = new Ai.ControlServer(_registry, instanceId);
            _cli.PortFile = _control.PortFilePath;   // este Claude Code dirige ESTE documento
            // O título é o que o Windows escreve na barra dele e na barra de tarefas. Um número de
            // porta de debug ali é ruído para quem usa a app — a porta vive no tooltip do ⓘ e no
            // ficheiro de porta, que é onde o Claude Code a vai buscar.
            Title = "KLIP Animator";
        }
        catch (Exception ex)
        {
            Title = "KLIP Animator";
            System.Diagnostics.Debug.WriteLine("AI bus falhou: " + ex.Message);
        }

        // drag & drop: SVG + imagens (vídeo não suportado)
        AddHandler(DragDrop.DragOverEvent, (_, ev) =>
        { ev.DragEffects = ev.DataTransfer?.Contains(DataFormat.File) == true ? DragDropEffects.Copy : DragDropEffects.None; });
        AddHandler(DragDrop.DropEvent, OnDrop);

        // browser embutido: injeta o "chip + Tela" ao carregar cada página; recebe o clique de volta
        Browser.NavigationCompleted += OnBrowserNavCompleted;
        Browser.WebMessageReceived += OnWebMessage;

        LoadProfile();
        SetSessionIcon();
        LoadHotkeyConfig();
        PopulateHotkeysList();

        // error logging opt-in (semanal, só se o user ligar)
        Telemetry.Install();
        ErrLogChk.IsChecked = Telemetry.OptIn;
        _ = Telemetry.WeeklySendIfDue(WorkerRoot(), Ai.AiConfig.ResolveEmail());

        // auto-update probe (verifica GitHub → descarrega em background → pronto a aplicar)
        _ = CheckForUpdateAsync();

        // op-log: regista operações + métricas (local sempre; envio semanal só com opt-in)
        OpLog.Start();
        _ = OpLog.WeeklySendIfDue(WorkerRoot(), Ai.AiConfig.ResolveEmail());

        // entrada suave do chat (nunca snappy)
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            ChatCard.Opacity = 1;
            ChatCard.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("translateY(0px)");

            // §BETA primeira utilização — depois do layout assentar: tutorial de 3 cards + dot no ◆
            CreditsDot.IsVisible = !Onboarding.HasSeenPayments;
            if (!Onboarding.HasSeenTutorial) ShowTutorial();
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    // ================= perfil (PFP + nome, otimista) =================
    private void LoadProfile()
    {
        var email = Ai.AiConfig.ResolveEmail();
        EmailLbl.Text = string.IsNullOrEmpty(email) ? "(sem email definido)" : email;
        var name = Ai.AiConfig.GetProfile("display_name") ?? "";
        NameBox.Text = name;
        PfpInitial.Text = string.IsNullOrEmpty(name)
            ? (string.IsNullOrEmpty(email) ? "K" : email[..1].ToUpperInvariant())
            : name[..1].ToUpperInvariant();
        try
        {
            if (File.Exists(Ai.AiConfig.PfpPath))
            {
                PfpImg.Source = new Bitmap(Ai.AiConfig.PfpPath);
                PfpInitial.IsVisible = false;
            }
        }
        catch { }
    }

    private void OnPfpSaveName(object? s, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim() ?? "";
        if (name.Length == 0) return;
        PfpInitial.Text = name[..1].ToUpperInvariant();          // otimista, já
        AppendChat("·", $"nome atualizado: {name}");
        _ = System.Threading.Tasks.Task.Run(() => Ai.AiConfig.SetProfile("display_name", name));
    }

    private async void OnPickPfp(object? s, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Imagem") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.webp" } } },
        });
        if (files.Count == 0) return;
        var path = files[0].TryGetLocalPath();
        if (path is null) return;
        var fi = new FileInfo(path);
        if (fi.Length > 5 * 1024 * 1024) { AppendChat("✗", "foto acima de 5 MB — escolhe mais pequena."); return; }
        try
        {
            PfpImg.Source = new Bitmap(path);                    // otimista, já
            PfpInitial.IsVisible = false;
        }
        catch { AppendChat("✗", "imagem inválida"); return; }
        // efeito Chrome: o avatar entra no ícone da taskbar assim que a cópia local existir
        _ = System.Threading.Tasks.Task.Delay(600).ContinueWith(_ =>
            Avalonia.Threading.Dispatcher.UIThread.Post(SetSessionIcon));
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Ai.AiConfig.PfpPath)!);
                File.Copy(path, Ai.AiConfig.PfpPath, overwrite: true);
            }
            catch { }
        });
    }

    // ================= import: drag&drop + botão ＋ =================
    private void OnDrop(object? s, DragEventArgs e)
    {
        var files = e.DataTransfer?.TryGetFiles();
        if (files is null) return;
        foreach (var f in files)
        {
            var p = f.TryGetLocalPath();
            if (p is not null) ImportFile(p);
        }
    }

    private async void OnAttach(object? s, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = true,
            FileTypeFilter = new[] { new FilePickerFileType("SVG e imagens")
                { Patterns = new[] { "*.svg", "*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp" } } },
        });
        foreach (var f in files)
        {
            var p = f.TryGetLocalPath();
            if (p is not null) ImportFile(p);
        }
    }

    private void ImportFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".mp4" or ".mov" or ".avi" or ".webm" or ".mkv")
        { AppendChat("✗", "vídeo não é suportado — só SVG e imagens."); return; }
        if (ext is ".svg") { ImportSvg(path); return; }
        if (ext is ".riv") { try { ApiInsertRive(path, null); } catch (Exception e) { AppendChat("✗", e.Message); } return; }
        if (ext is ".json") { try { ApiInsertLottie(path); } catch (Exception e) { AppendChat("✗", e.Message); } return; }
        if (ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif") { ImportImage(path); return; }
        AppendChat("✗", $"formato não suportado: {ext}");
    }

    private void ImportImage(string path)
    {
        var name = ApiInsertImage(path);
        if (name is not null) AppendChat("·", $"imagem adicionada: {Path.GetFileName(path)}");
    }

    public object ApiInsertRive(string path, string? anim)
    {
        var info = Klip.Engine.Rive.RiveClip.Info(path);
        if (info is null) throw new InvalidOperationException("não consegui abrir o .riv");
        var (w, h, anims) = info.Value;
        double rw = w > 0 ? w : 400, rh = h > 0 ? h : 400;
        double fit = Math.Min(1.0, 460.0 / Math.Max(rw, rh));
        var name = $"rive-{_nameSeq++}";
        Mutate(() =>
        {
            _layers.Add(new Layer(name, MorphTrack.Static(Shapes.Rect(rw / 2, rh / 2)), 0x00FFFFFF,
                RivePath: path, RiveAnim: anim, RiveW: rw, RiveH: rh, Scale: Track.Const(fit)));
            _selected = _layers.Count - 1;
        });
        if (anims.Length > 0) AppendChat("·", $"Rive: {Path.GetFileName(path)} — animações: {string.Join(", ", anims)}");
        return new { id = name, width = rw, height = rh, animations = anims };
    }

    public object ApiInsertLottie(string path)
    {
        var info = Klip.Engine.Lottie.LottieClip.Info(path);
        if (info is null) throw new InvalidOperationException("não consegui abrir o .json Lottie");
        var (w, h, secs, fps) = info.Value;
        double lw = w > 0 ? w : 400, lh = h > 0 ? h : 400;
        double fit = Math.Min(1.0, 460.0 / Math.Max(lw, lh));
        var name = $"lottie-{_nameSeq++}";
        Mutate(() =>
        {
            _layers.Add(new Layer(name, MorphTrack.Static(Shapes.Rect(lw / 2, lh / 2)), 0x00FFFFFF,
                LottiePath: path, LottieW: lw, LottieH: lh, Scale: Track.Const(fit)));
            _selected = _layers.Count - 1;
        });
        AppendChat("·", $"Lottie: {Path.GetFileName(path)} — {secs:0.##}s @ {fps:0}fps");
        return new { id = name, width = lw, height = lh, seconds = secs, fps };
    }

    public string? ApiInsertImage(string path)
    {
        using var bmp = SkiaSharp.SKBitmap.Decode(path);
        if (bmp is null) { AppendChat("✗", "imagem ilegível: " + Path.GetFileName(path)); return null; }
        double sc = Math.Min(1.0, 500.0 / Math.Max(bmp.Width, bmp.Height));
        var name = $"img-{_nameSeq++}";
        Mutate(() =>
        {
            _layers.Add(new Layer(name, MorphTrack.Static(Shapes.Rect(bmp.Width / 2.0, bmp.Height / 2.0)),
                0x00FFFFFF, ImagePath: path, Scale: Track.Const(sc)));
            _selected = _layers.Count - 1;
        });
        return name;
    }

    private void ImportSvg(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch { AppendChat("✗", "não consegui ler o SVG"); return; }
        var matches = System.Text.RegularExpressions.Regex.Matches(text, "\\bd\\s*=\\s*\"([^\"]+)\"");
        int added = 0;
        Mutate(() =>
        {
            foreach (System.Text.RegularExpressions.Match m in matches)
            {
                if (added >= 12) break;
                using var p = SkiaSharp.SKPath.ParseSvgPathData(m.Groups[1].Value);
                if (p is null || p.IsEmpty) continue;
                var b = p.Bounds;
                p.Transform(SkiaSharp.SKMatrix.CreateTranslation(-b.MidX, -b.MidY));   // centrar
                _layers.Add(new Layer($"svg-{_nameSeq++}", MorphTrack.Static(p.ToSvgPathData()),
                    added == 0 ? 0xFF232326 : NextColor()));
                added++;
            }
            if (added > 0) _selected = _layers.Count - 1;
        });
        AppendChat(added > 0 ? "·" : "✗",
            added > 0 ? $"SVG importado ({added} caminho(s))" : "SVG sem caminhos <path d=…> reconhecíveis");
    }

    private void OnMic(object? s, RoutedEventArgs e)
        => AppendChat("·", "voz (transcrição + fala): no roadmap — whisper + Piper embutidos, em breve.");

    private void OnChatStop(object? s, RoutedEventArgs e)
    {
        _chatCts?.Cancel();
        _cli.Stop();
    }

    // ---- tokens/custo ----
    private long _tokTotal;
    private double _costUsd;

    private void UpdateTokens()
        => TokensLbl.Text = $"{_tokTotal:n0} tok · ${_costUsd.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}";

    private Ai.ControlServer? _control;
    private static int _instanceSeq;   // 0 = janela principal; >0 = composições adicionais
    private Ai.ActionRegistry _registry = null!;
    private Ai.AnthropicBackend _api = null!;
    private Ai.ClaudeCliBackend _cli = null!;
    private System.Threading.CancellationTokenSource? _chatCts;

    // ================= AI chat panel =================
    private void AppendChat(string who, string text)
    {
        ChatLog.IsVisible = true;
        ChatLog.Text += (string.IsNullOrEmpty(who) ? text : $"{who} {text}") + "\n";
        ChatLog.CaretIndex = ChatLog.Text?.Length ?? 0;
    }

    private void OnChatKey(object? s, KeyEventArgs e)
    { if (e.Key == Key.Enter) { OnSendChat(null, new RoutedEventArgs()); e.Handled = true; } }

    // ================= BROWSER embutido =================
    private bool _browserOpen;
    private bool _browserFull;   // ecrã inteiro vs meia tela
    private bool _browserNav;    // já navegou uma vez

    private static readonly Avalonia.Controls.GridLength ColZero = new(0);
    private static readonly Avalonia.Controls.GridLength ColStar = new(1, Avalonia.Controls.GridUnitType.Star);
    private static readonly Avalonia.Controls.GridLength ColCamadas = new(320);   // abas Camadas·3D·Chat

    /// <summary>Geometria do browser: fechado(0) · metade(canvas *|browser * + camadas 246) · full(só browser).</summary>
    private void ApplyBrowserLayout()
    {
        var c = ContentGrid.ColumnDefinitions;   // [1]=canvas [2]=browser [3]=camadas
        BrowserPanel.IsVisible = _browserOpen;
        if (!_browserOpen)      { c[1].Width = ColStar; c[2].Width = ColZero; c[3].Width = ColCamadas; }
        else if (_browserFull)  { c[1].Width = ColZero; c[2].Width = ColStar; c[3].Width = ColZero; }
        else                    { c[1].Width = ColStar; c[2].Width = ColStar; c[3].Width = ColCamadas; }
        BrowserBtn.Foreground = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.Parse(_browserOpen ? "#6D5EF6" : "#6B6B68"));
    }

    /// <summary>Pastas especiais do KLIP — sobrevivem a resets (%APPDATA%\Klip\assets\…).</summary>
    private static string AssetsDir(string sub)
    {
        var d = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Klip", "assets", sub);
        System.IO.Directory.CreateDirectory(d);
        return d;
    }

    private void OnToggleBrowser(object? s, RoutedEventArgs e)
    {
        _browserOpen = !_browserOpen;
        if (!_browserOpen) _browserFull = false;   // fecha sempre no estado meia-tela
        ApplyBrowserLayout();
        if (_browserOpen && !_browserNav)
        {
            _browserNav = true;
            NavigateBrowser("https://www.google.com");
        }
    }

    private void OnBrowserFullscreen(object? s, RoutedEventArgs e)
    {
        if (!_browserOpen) return;
        _browserFull = !_browserFull;
        ApplyBrowserLayout();
    }

    private void NavigateBrowser(string text)
    {
        try
        {
            var url = ToUrl(text);
            BrowserUrl.Text = url;
            Browser.Source = new Uri(url);
        }
        catch (Exception ex) { AppendChat("✗", "browser: " + ex.Message); }
    }

    /// <summary>Texto → URL: se parece URL usa-a; senão pesquisa no Google.</summary>
    private static string ToUrl(string text)
    {
        text = text.Trim();
        if (text.StartsWith("http://") || text.StartsWith("https://")) return text;
        bool looksUrl = !text.Contains(' ') && text.Contains('.') && !text.Contains("  ");
        return looksUrl ? "https://" + text
            : "https://www.google.com/search?q=" + Uri.EscapeDataString(text);
    }

    private void OnBrowserUrlKey(object? s, KeyEventArgs e)
    { if (e.Key == Key.Enter) { NavigateBrowser(BrowserUrl.Text ?? ""); e.Handled = true; } }

    private void OnBrowserBack(object? s, RoutedEventArgs e) { try { Browser.GoBack(); } catch { } }
    private void OnBrowserForward(object? s, RoutedEventArgs e) { try { Browser.GoForward(); } catch { } }
    private void OnBrowserReload(object? s, RoutedEventArgs e) { try { Browser.Refresh(); } catch { } }

    /// <summary>Baixa a URL atual (se for imagem) para os assets e insere na tela.</summary>
    private async void OnBrowserToCanvas(object? s, RoutedEventArgs e)
    {
        var url = BrowserUrl.Text?.Trim() ?? "";
        if (!url.StartsWith("http")) { AppendChat("·", "escreve o URL de uma imagem para a trazer p/ a tela"); return; }
        try
        {
            var path = await DownloadImage(url);
            if (path is not null) { ApiInsertImage(path); AppendChat("·", "imagem trazida do browser para a tela"); }
            else AppendChat("✗", "o URL não é uma imagem descarregável");
        }
        catch (Exception ex) { AppendChat("✗", "download: " + ex.Message); }
    }

    private static readonly System.Net.Http.HttpClient _dl = MakeDl();
    private static System.Net.Http.HttpClient MakeDl()
    {
        var h = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // UA realista — hosts como a Wikimedia rejeitam (400/403) UAs genéricos. Definido UMA vez.
        h.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        return h;
    }

    /// <summary>Descarrega uma imagem (http(s) OU data:base64) para %APPDATA%\Klip\assets\images. null se não for imagem.</summary>
    public static async Task<string?> DownloadImage(string url)
    {
        byte[] bytes;
        if (url.StartsWith("data:"))
        {
            var comma = url.IndexOf(',');
            if (comma < 0) return null;
            var meta = url.Substring(5, comma - 5);           // ex.: image/png;base64
            var data = url[(comma + 1)..];
            bytes = meta.Contains("base64")
                ? Convert.FromBase64String(data)
                : System.Text.Encoding.UTF8.GetBytes(Uri.UnescapeDataString(data));
        }
        else if (url.StartsWith("blob:")) return null;        // blobs não são descarregáveis directamente
        else bytes = await _dl.GetByteArrayAsync(url);

        using var probe = SkiaSharp.SKBitmap.Decode(bytes);
        if (probe is null) return null;                       // não é imagem
        var name = "web_" + Environment.TickCount64 + ExtOf(bytes);
        var path = System.IO.Path.Combine(AssetsDir("images"), name);
        await System.IO.File.WriteAllBytesAsync(path, bytes);
        return path;
    }

    /// <summary>Extensão pelos magic bytes (mais fiável que o URL).</summary>
    private static string ExtOf(byte[] b)
    {
        if (b.Length > 12 && b[0] == 0x89 && b[1] == 0x50) return ".png";
        if (b.Length > 3 && b[0] == 0x47 && b[1] == 0x49) return ".gif";
        if (b.Length > 12 && b[8] == 0x57 && b[9] == 0x45) return ".webp";   // RIFF....WEBP
        return ".jpg";
    }

    // ---- clicar em QUALQUER imagem da página → trazê-la p/ a tela (JS↔host) ----

    private const string GrabJs = @"
(function(){
  if(window.__klipGrab) return; window.__klipGrab=true;
  var chip=document.createElement('div'); var cur=null;
  chip.textContent='+ Tela';
  chip.style.cssText='position:fixed;z-index:2147483647;display:none;padding:4px 9px;'+
    'background:#6D5EF6;color:#fff;font:600 12px system-ui,sans-serif;border-radius:8px;'+
    'cursor:pointer;box-shadow:0 2px 10px rgba(0,0,0,.35);user-select:none';
  var attach=function(){ if(document.body){document.body.appendChild(chip);} else {setTimeout(attach,200);} };
  attach();
  document.addEventListener('mouseover',function(e){
    var img=e.target&&e.target.closest?e.target.closest('img'):null;
    if(img){ var r=img.getBoundingClientRect(); if(r.width<40||r.height<40)return;
      cur=img; chip.style.left=(r.left+8)+'px'; chip.style.top=(r.top+8)+'px'; chip.style.display='block'; }
  },true);
  chip.addEventListener('click',function(){
    if(cur&&window.chrome&&window.chrome.webview){
      window.chrome.webview.postMessage(JSON.stringify({type:'grab',src:cur.currentSrc||cur.src})); }
  });
  window.addEventListener('scroll',function(){chip.style.display='none';},true);
})();";

    private System.Threading.Tasks.TaskCompletionSource<bool>? _navTcs;   // Fase 8: espera de navegação (browser_wait_idle)

    private async void OnBrowserNavCompleted(object? s, Avalonia.Controls.WebViewNavigationCompletedEventArgs e)
    {
        try { if (e.IsSuccess) await Browser.InvokeScript(GrabJs); } catch { }
        _navTcs?.TrySetResult(e.IsSuccess);   // desbloqueia quem espera a navegação
    }

    private void OnWebMessage(object? s, Avalonia.Controls.WebMessageReceivedEventArgs e)
    {
        try
        {
            var n = System.Text.Json.Nodes.JsonNode.Parse(e.Body);
            if (n?["type"]?.GetValue<string>() == "grab")
            {
                var src = n["src"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(src)) _ = GrabWebImage(src);
            }
        }
        catch { }
    }

    private async Task GrabWebImage(string src)
    {
        try
        {
            var path = await DownloadImage(src);
            if (path is not null) { ApiInsertImage(path); AppendChat("·", "imagem trazida do browser (clique) para a tela"); }
            else AppendChat("✗", "essa imagem não é descarregável (blob/stream)");
        }
        catch (Exception ex) { AppendChat("✗", "grab: " + ex.Message); }
    }

    // ---- ações do BUS (a IA conduz o browser) ----

    /// <summary>web_open: a IA abre o browser embutido e navega (vê o que estás a ver).</summary>
    public object ApiWebOpen(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) throw new InvalidOperationException("url vazio");
        if (!_browserOpen) { _browserOpen = true; _browserNav = true; ApplyBrowserLayout(); }
        NavigateBrowser(url);
        return new { ok = true, url = BrowserUrl.Text };
    }

    /// <summary>download_image: baixa um URL de imagem p/ os assets e insere na tela. Devolve id+path.</summary>
    public object ApiDownloadImage(string url)
    {
        var path = Task.Run(() => DownloadImage(url)).GetAwaiter().GetResult()
                   ?? throw new InvalidOperationException("o URL não é uma imagem descarregável");
        var id = ApiInsertImage(path);
        AppendChat("·", "imagem baixada da web para a tela");
        return new { id, path };
    }

    /// <summary>list_assets: ficheiros nas pastas especiais do KLIP (sobrevivem a resets).</summary>
    public object ApiListAssets()
    {
        var outp = new List<object>();
        foreach (var kind in new[] { "images", "downloads", "videos", "audio" })
            foreach (var f in System.IO.Directory.EnumerateFiles(AssetsDir(kind)))
                outp.Add(new { name = System.IO.Path.GetFileName(f), path = f, kind });
        return new { root = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Klip", "assets"),
                     count = outp.Count, assets = outp };
    }

    /// <summary>download_youtube: yt-dlp (SABR bypass) → assets\videos, em background. A IA faz list_assets depois.</summary>
    public object ApiDownloadYoutube(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || !url.Contains("http"))
            throw new InvalidOperationException("url de vídeo inválido");
        var dir = AssetsDir("videos");
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "python",
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true,
        };
        foreach (var arg in new[] { "-m", "yt_dlp",
            "--no-playlist", "-f", "mp4/best",
            "--extractor-args", "youtube:player_client=android,ios,tv,web_embedded",
            "-o", System.IO.Path.Combine(dir, "%(title).80s.%(ext)s"), url })
            psi.ArgumentList.Add(arg);
        var proc = System.Diagnostics.Process.Start(psi)
                   ?? throw new InvalidOperationException("não consegui lançar o yt-dlp (python -m yt_dlp)");
        AppendChat("·", "a baixar vídeo do YouTube em background…");
        _ = Task.Run(async () =>
        {
            await proc.WaitForExitAsync();
            var ok = proc.ExitCode == 0;
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                AppendChat(ok ? "·" : "✗", ok ? "vídeo do YouTube guardado em assets\\videos" : "falha no download do vídeo"));
        });
        return new { started = true, folder = dir };
    }

    // ---- VOZ→TEXTO (a "mesma tech" da visão, mas p/ áudio) ----

    /// <summary>Escreve no chat a partir de threads de fundo (marshal p/ a UI).</summary>
    private void UiChat(string who, string msg)
        => Avalonia.Threading.Dispatcher.UIThread.Post(() => AppendChat(who, msg));

    /// <summary>Resolve um nome relativo às pastas de assets (ou devolve o caminho absoluto tal e qual).</summary>
    private static string ResolveAsset(string path, params string[] kinds)
    {
        if (System.IO.Path.IsPathRooted(path)) return path;
        foreach (var k in kinds)
        {
            var c = System.IO.Path.Combine(AssetsDir(k), path);
            if (System.IO.File.Exists(c)) return c;
        }
        return path;
    }

    /// <summary>transcribe: áudio/vídeo → texto (Whisper on-device). Corre em background (Background=true no bus).</summary>
    public object ApiTranscribe(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("path vazio");
        path = ResolveAsset(path, "videos", "audio", "downloads", "images");
        if (!System.IO.File.Exists(path)) throw new InvalidOperationException("ficheiro não encontrado: " + path);

        if (!Transcriber.ModelReady())
        {
            _ = Task.Run(async () =>
            {
                try { await Transcriber.PrepareModel(m => UiChat("·", m)); UiChat("·", "modelo de voz pronto ✓ — já podes transcrever"); }
                catch (Exception ex) { UiChat("✗", "falha a descarregar o modelo de voz: " + ex.Message); }
            });
            return new { model_downloading = true, note = "modelo de voz a descarregar (~142MB, só 1ª vez); repete o transcribe quando eu avisar que está pronto" };
        }

        var text = Transcriber.Transcribe(path, m => UiChat("·", m)).GetAwaiter().GetResult();
        var outp = System.IO.Path.Combine(AssetsDir("audio"),
            System.IO.Path.GetFileNameWithoutExtension(path) + ".txt");
        System.IO.File.WriteAllText(outp, text);
        UiChat("·", $"transcrição pronta ({text.Length} chars) → assets\\audio");
        return new { text, @out = outp, chars = text.Length };
    }

    /// <summary>read_text: conteúdo de um .txt (ex.: uma transcrição).</summary>
    public object ApiReadText(string path)
    {
        path = ResolveAsset(path, "audio", "downloads", "images", "videos");
        if (!System.IO.File.Exists(path)) throw new InvalidOperationException("não existe: " + path);
        var txt = System.IO.File.ReadAllText(path);
        return new { path, text = txt.Length > 20000 ? txt[..20000] : txt, chars = txt.Length };
    }

    // ---- CAPTURA (a IA vê o que vês: browser nativo ou a janela) ----

    /// <summary>Captura um visual pelo seu rectângulo no ECRÃ (px físicos) → PNG (apanha o WebView2 nativo).</summary>
    private static byte[] CaptureVisual(Avalonia.Visual v)
    {
        var tl = v.PointToScreen(new Avalonia.Point(0, 0));
        var br = v.PointToScreen(new Avalonia.Point(v.Bounds.Width, v.Bounds.Height));
        return ScreenCapture.CaptureRectPng(tl.X, tl.Y, br.X - tl.X, br.Y - tl.Y);
    }

    /// <summary>browser_capture: PNG do que o browser mostra; devolvido com _image → a IA VÊ-o.</summary>
    public object ApiBrowserCapture()
    {
        if (!_browserOpen) throw new InvalidOperationException("o browser não está aberto — usa web_open primeiro");
        var png = CaptureVisual(Browser);
        var path = System.IO.Path.Combine(AssetsDir("downloads"), "shot_" + Environment.TickCount64 + ".png");
        System.IO.File.WriteAllBytes(path, png);
        return new { _image = path, url = BrowserUrl.Text, note = "o que o utilizador vê no browser" };
    }

    /// <summary>screenshot: PNG da janela inteira do KLIP; devolvido com _image → a IA vê-o.</summary>
    public object ApiScreenshot()
    {
        var png = CaptureVisual(this);
        var path = System.IO.Path.Combine(AssetsDir("downloads"), "win_" + Environment.TickCount64 + ".png");
        System.IO.File.WriteAllBytes(path, png);
        return new { _image = path, note = "a janela do KLIP" };
    }

    // ================= FASE 8: agência ao nível do DOM + baixar qualquer asset =================

    private static string AssetsRootDir()
    {
        var d = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Klip", "assets");
        System.IO.Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>String C# → literal JS seguro (JSON string é JS válido).</summary>
    private static string JsStr(string s) => System.Text.Json.JsonSerializer.Serialize(s);

    /// <summary>Corre JS no browser e devolve o JSON desembrulhado. Lança se o browser não está aberto.</summary>
    private async Task<System.Text.Json.Nodes.JsonNode?> EvalDomAsync(string js)
    {
        if (!_browserOpen) throw new InvalidOperationException("o browser não está aberto — usa web_open primeiro");
        var raw = await Browser.InvokeScript(js);
        return UnwrapJson(raw);
    }

    /// <summary>Desembrulha o resultado do InvokeScript (tolera dupla-codificação de string JSON).</summary>
    private static System.Text.Json.Nodes.JsonNode? UnwrapJson(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Trim() == "null") return null;
        System.Text.Json.Nodes.JsonNode? node;
        try { node = System.Text.Json.Nodes.JsonNode.Parse(raw); } catch { return null; }
        if (node is System.Text.Json.Nodes.JsonValue jv && jv.TryGetValue<string>(out var inner))
        {
            var t = inner.TrimStart();
            if (t.StartsWith('{') || t.StartsWith('['))
                try { return System.Text.Json.Nodes.JsonNode.Parse(inner); } catch { }
        }
        return node;
    }

    private async Task<bool> WaitNavAsync(int timeoutMs = 8000)
    {
        var tcs = _navTcs;
        if (tcs is null) return false;
        var done = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
        return done == tcs.Task && tcs.Task.Result;
    }

    /// <summary>browser_dom: mapa estruturado da página atual (mesmo shape que DomExtract.Parse).</summary>
    public async Task<object?> ApiBrowserDom()
    {
        var node = await EvalDomAsync(Klip.Engine.DomExtract.ExtractJs);
        return node is null ? new { ok = false, error = "sem resposta do DOM" } : (object)node;
    }

    /// <summary>browser_extract_assets: só as listas de URLs de assets da página aberta.</summary>
    public async Task<object?> ApiBrowserExtractAssets()
    {
        var node = await EvalDomAsync(Klip.Engine.DomExtract.ExtractJs);
        if (node is null) return new { ok = false, error = "sem resposta" };
        return new { images = node["images"], videos = node["videos"], audios = node["audios"], links = node["links"] };
    }

    /// <summary>browser_click: por seletor CSS OU por texto do link/botão; opcionalmente espera a navegação.</summary>
    public async Task<object?> ApiBrowserClick(string? selector, string? text, bool waitNav)
    {
        string js;
        if (!string.IsNullOrWhiteSpace(selector))
            js = "(function(){var el=document.querySelector(" + JsStr(selector) +
                 "); if(!el) return JSON.stringify({ok:false,error:'no match'}); el.click(); return JSON.stringify({ok:true,clicked:(el.tagName||'').toLowerCase()});})()";
        else if (!string.IsNullOrWhiteSpace(text))
            js = "(function(){var t=" + JsStr(text) +
                 ".toLowerCase(); var els=[].slice.call(document.querySelectorAll('a,button,[role=button],input[type=submit],input[type=button]')); var el=els.find(function(e){return ((e.innerText||e.value||'').toLowerCase().indexOf(t)>=0);}); if(!el) return JSON.stringify({ok:false,error:'no text match'}); el.click(); return JSON.stringify({ok:true,clicked:(el.innerText||el.value||'').slice(0,60)});})()";
        else return new { ok = false, error = "dá selector ou text" };

        if (waitNav) _navTcs = new(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        var node = await EvalDomAsync(js);
        bool navigated = false;
        if (waitNav) navigated = await WaitNavAsync();
        return new { result = node, navigated };
    }

    /// <summary>browser_type: escreve num campo por seletor + dispara input/change (React/Vue-safe).</summary>
    public async Task<object?> ApiBrowserType(string selector, string text)
    {
        if (string.IsNullOrWhiteSpace(selector)) throw new InvalidOperationException("selector vazio");
        var js = "(function(){var el=document.querySelector(" + JsStr(selector) +
                 "); if(!el) return JSON.stringify({ok:false,error:'no match'}); var v=" + JsStr(text) +
                 "; var proto=el.tagName==='TEXTAREA'?window.HTMLTextAreaElement.prototype:window.HTMLInputElement.prototype; var d=Object.getOwnPropertyDescriptor(proto,'value'); if(d&&d.set){d.set.call(el,v);}else{el.value=v;} el.dispatchEvent(new Event('input',{bubbles:true})); el.dispatchEvent(new Event('change',{bubbles:true})); return JSON.stringify({ok:true});})()";
        return await EvalDomAsync(js) ?? (object)new { ok = false };
    }

    /// <summary>browser_eval: JS cru com return → JSON {ok,result} ou {ok:false,error}.</summary>
    public async Task<object?> ApiBrowserEval(string js)
    {
        if (string.IsNullOrWhiteSpace(js)) throw new InvalidOperationException("js vazio");
        var wrapped = "(function(){try{var __r=(function(){" + js +
                      "})(); return JSON.stringify({ok:true,result:(__r===undefined?null:__r)});}catch(e){return JSON.stringify({ok:false,error:String(e)});}})()";
        return await EvalDomAsync(wrapped) ?? (object)new { ok = false, error = "sem resposta" };
    }

    /// <summary>browser_wait_idle: espera a próxima navegação terminar (até timeout).</summary>
    public async Task<object?> ApiBrowserWaitIdle(int timeoutMs)
    {
        _navTcs = new(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        return new { navigated = await WaitNavAsync(timeoutMs <= 0 ? 8000 : timeoutMs) };
    }

    /// <summary>download_asset: baixa QUALQUER url (magic-bytes) → pasta certa; imagem vira camada.</summary>
    public async Task<object?> ApiDownloadAsset(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) throw new InvalidOperationException("url vazio");
        var res = await Klip.Engine.AssetDownloader.Shared.DownloadAsync(url, AssetsRootDir());
        string? layerId = null;
        if (res.Kind == Klip.Engine.AssetKind.Image)
        {
            layerId = ApiInsertImage(res.Path);
            AppendChat("·", "asset (imagem) baixado da web → camada");
        }
        else AppendChat("·", $"asset baixado ({res.Kind}) → {System.IO.Path.GetFileName(res.Path)}");
        return new { kind = res.Kind.ToString().ToLowerInvariant(), path = res.Path, from_cache = res.FromCache, id = layerId, family = res.FontFamily };
    }

    // ================= PAYWALL / Créditos (comprovativo PDF → worker FK) =================
    private static readonly System.Net.Http.HttpClient _pay = new() { Timeout = TimeSpan.FromSeconds(60) };
    private double _selectedPlanKz = 200000;

    /// <summary>Raiz do worker (as rotas de créditos estão na raiz, não em /ai).</summary>
    private static string WorkerRoot()
    {
        var u = Ai.AiConfig.ResolveWorkerUrl().TrimEnd('/');
        return u.EndsWith("/ai") ? u[..^3] : u;
    }

    /// <summary>UM número, não um intervalo. Taxa mista $10/1M de output (entre Opus $25 e
    /// Haiku $5) — aproximado de propósito, daí o ~. 40 000 Kz → ~2 M tokens.</summary>
    private static string TokenLabel(double kz)
    {
        double tokens = kz / 2000.0 / 10.0 * 1e6;
        return $"~{Compacto(tokens)} tokens";
    }

    /// <summary>"2 000 000" não se lê; "2 M" lê-se.</summary>
    private static string Compacto(double n)
    {
        if (n >= 1e6) { double m = n / 1e6; return (m >= 10 ? $"{m:0}" : $"{m:0.#}").Replace('.', ',') + " M"; }
        if (n >= 1e3) { double k = n / 1e3; return (k >= 10 ? $"{k:0}" : $"{k:0.#}").Replace('.', ',') + " k"; }
        return $"{n:0}";
    }

    private void OnErrLogToggle(object? s, RoutedEventArgs e)
    { if (ErrLogChk is not null) Telemetry.OptIn = ErrLogChk.IsChecked == true; }

    private async Task CheckForUpdateAsync()
    {
        if (Updater.UpdateReady) Avalonia.Threading.Dispatcher.UIThread.Post(ShowUpdateReady);
        var (newer, tag, url) = await Updater.CheckAsync();
        if (!newer) return;
        UiChat("✦", $"Nova versão {tag} disponível — a descarregar em background…");
        var path = await Updater.DownloadAsync(url);
        if (path is not null)
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            { ShowUpdateReady(); AppendChat("✦", $"Atualização {tag} pronta. Vai a ⌨ → Atualizar (reinicia e aplica)."); });
    }

    private void ShowUpdateReady() { if (UpdateBtn is not null) UpdateBtn.IsVisible = true; }
    private void OnApplyUpdate(object? s, RoutedEventArgs e) => Updater.Apply();

    private void OnOpenPayment(object? s, RoutedEventArgs e) => OpenPayment();

    private void OpenPayment()
    {
        if (string.IsNullOrWhiteSpace(PaymentEmail.Text)) PaymentEmail.Text = Ai.AiConfig.ResolveEmail();
        PaymentStatus.Text = "";
        _selectedPlanKz = 200000; HighlightPlan(PlanB);
        PaymentPanel.IsVisible = true;
        // §BETA: já viu os pagamentos → o dot de descoberta some para sempre. Aqui dentro (e não no
        // handler do clique) porque o evento "nocredits" também abre o paywall sozinho.
        Onboarding.HasSeenPayments = true;
        CreditsDot.IsVisible = false;
        _ = RefreshBalance();
    }

    private void HighlightPlan(Button? sel)
    {
        foreach (var b in new[] { PlanA, PlanB, PlanC }) b.Classes.Set("sel", b == sel);
    }

    private void OnSelectPlan(object? s, RoutedEventArgs e)
    {
        if (s is Button b && double.TryParse(b.Tag?.ToString(), out var kz))
        { _selectedPlanKz = kz; PlanCustom.Text = ""; HighlightPlan(b); }
    }

    private void OnCustomAmountKey(object? s, KeyEventArgs e)
    {
        var digits = new string((PlanCustom.Text ?? "").Where(char.IsDigit).ToArray());
        if (double.TryParse(digits, out var kz) && kz > 0) { _selectedPlanKz = kz; HighlightPlan(null); }
    }

    private async void OnRequestPayment(object? s, RoutedEventArgs e)
    {
        var email = (PaymentEmail.Text ?? "").Trim();
        if (string.IsNullOrEmpty(email) || !email.Contains('@')) { PaymentStatus.Text = "Escreve o teu email primeiro."; return; }
        if (_selectedPlanKz < 40000) { PaymentStatus.Text = "Mínimo 40 000 Kz."; return; }
        Ai.AiConfig.SetProfile("klip_email", email);
        PaymentReqBtn.IsEnabled = false; PaymentStatus.Text = "A enviar os dados de pagamento…";
        try
        {
            var body = new System.Net.Http.StringContent(
                new System.Text.Json.Nodes.JsonObject { ["email"] = email, ["amount_kz"] = _selectedPlanKz }.ToJsonString(),
                System.Text.Encoding.UTF8, "application/json");
            var resp = await _pay.PostAsync(WorkerRoot() + "/klip/pay-request", body);
            var n = System.Text.Json.Nodes.JsonNode.Parse(await resp.Content.ReadAsStringAsync());
            PaymentStatus.Text = n?["ok"]?.GetValue<bool>() == true
                ? $"✓ Email enviado para {email}. Abre o link (15 min) para copiar o IBAN, paga e envia o comprovativo aqui."
                : "✗ " + (n?["error"]?.GetValue<string>() ?? "falha");
        }
        catch (Exception ex) { PaymentStatus.Text = "✗ erro: " + ex.Message; }
        finally { PaymentReqBtn.IsEnabled = true; }
    }

    private void OnClosePayment(object? s, RoutedEventArgs e) => PaymentPanel.IsVisible = false;
    private void OnPaymentBackdrop(object? s, PointerPressedEventArgs e) => PaymentPanel.IsVisible = false;
    private void OnPaymentCardPressed(object? s, PointerPressedEventArgs e) => e.Handled = true;  // clicar no cartão não fecha

    private async Task RefreshBalance()
    {
        var email = (PaymentEmail.Text ?? "").Trim();
        if (string.IsNullOrEmpty(email)) { PaymentBalance.Text = "Saldo: — (define o teu email)"; return; }
        try
        {
            var url = WorkerRoot() + "/credits/balance";
            var body = new System.Net.Http.StringContent(
                new System.Text.Json.Nodes.JsonObject { ["email"] = email }.ToJsonString(),
                System.Text.Encoding.UTF8, "application/json");
            var resp = await _pay.PostAsync(url, body);
            var n = System.Text.Json.Nodes.JsonNode.Parse(await resp.Content.ReadAsStringAsync());
            var bal = n?["balanceUsd"]?.GetValue<double>() ?? n?["balance_usd"]?.GetValue<double>() ?? 0;
            PaymentBalance.Text = "Saldo: " + (bal <= 0 ? "0 tokens" : TokenLabel(bal * 2000));
        }
        catch { PaymentBalance.Text = "Saldo: — (offline)"; }
    }

    private async void OnPickComprovativo(object? s, RoutedEventArgs e)
    {
        var email = (PaymentEmail.Text ?? "").Trim();
        if (string.IsNullOrEmpty(email) || !email.Contains('@')) { PaymentStatus.Text = "Escreve o teu email primeiro."; return; }
        Ai.AiConfig.SetProfile("klip_email", email);
        var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Comprovativo (PDF)", AllowMultiple = false,
            FileTypeFilter = new[] { new Avalonia.Platform.Storage.FilePickerFileType("PDF") { Patterns = new[] { "*.pdf" } } },
        });
        if (files.Count == 0) return;
        PaymentStatus.Text = "A verificar o comprovativo…"; PaymentSendBtn.IsEnabled = false;
        try
        {
            await using var stream = await files[0].OpenReadAsync();
            using var mem = new System.IO.MemoryStream();
            await stream.CopyToAsync(mem);
            // O MultipartFormDataContent do .NET escreve `name=file` SEM aspas quando o valor é um
            // token válido; o parser de formData() da Cloudflare exige `name="file"` e responde
            // "Content-Disposition header in FormData part is missing a name". Aspas explícitas.
            using var content = new System.Net.Http.MultipartFormDataContent();
            var filePart = new System.Net.Http.ByteArrayContent(mem.ToArray());
            filePart.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("form-data")
            { Name = "\"file\"", FileName = "\"comprovativo.pdf\"" };
            filePart.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
            content.Add(filePart);
            var emailPart = new System.Net.Http.StringContent(email);
            emailPart.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("form-data") { Name = "\"email\"" };
            content.Add(emailPart);
            var prodPart = new System.Net.Http.StringContent("KLIP");
            prodPart.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("form-data") { Name = "\"product\"" };
            content.Add(prodPart);
            var url = WorkerRoot() + "/comprovativo";
            var resp = await _pay.PostAsync(url, content);
            var n = System.Text.Json.Nodes.JsonNode.Parse(await resp.Content.ReadAsStringAsync());
            if (n?["verdict"]?.GetValue<string>() == "VERDADEIRO")
            {
                var usd = n["creditsUsd"]?.GetValue<double>() ?? 0;
                var bal = n["balanceUsd"]?.GetValue<double>() ?? 0;
                PaymentStatus.Text = $"✓ Creditado (+{TokenLabel(usd * 2000)}). Recebeste a fatura por email. Obrigado!";
                PaymentBalance.Text = "Saldo: " + TokenLabel(bal * 2000);
                AppendChat("·", "créditos carregados — saldo " + TokenLabel(bal * 2000));
            }
            else
            {
                var reason = n?["reason"] is System.Text.Json.Nodes.JsonArray a && a.Count > 0
                    ? string.Join("; ", a.Select(x => x?.GetValue<string>()))
                    : (n?["error"]?.GetValue<string>() ?? "não reconhecido");
                PaymentStatus.Text = "✗ " + reason;
            }
        }
        catch (Exception ex) { PaymentStatus.Text = "✗ erro: " + ex.Message; }
        finally { PaymentSendBtn.IsEnabled = true; }
    }

    // ================= RECLAMAÇÕES §BETA (logon leve + consentimento → worker FK) =================

    private void OnOpenComplaint(object? s, RoutedEventArgs e)
    {
        // logon leve: nome + email escrevem-se UMA vez; nas próximas já vêm preenchidos
        if (string.IsNullOrWhiteSpace(ComplaintName.Text))
            ComplaintName.Text = Ai.AiConfig.GetProfile("display_name") ?? "";
        if (string.IsNullOrWhiteSpace(ComplaintEmail.Text))
            ComplaintEmail.Text = Ai.AiConfig.ResolveEmail();
        ComplaintStatus.Text = "";
        ComplaintPanel.IsVisible = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => (string.IsNullOrWhiteSpace(ComplaintName.Text) ? ComplaintName : ComplaintMsg).Focus(),
            Avalonia.Threading.DispatcherPriority.Input);
    }

    private void OnCloseComplaint(object? s, RoutedEventArgs e) => ComplaintPanel.IsVisible = false;
    private void OnComplaintBackdrop(object? s, PointerPressedEventArgs e) => ComplaintPanel.IsVisible = false;
    private void OnComplaintCardPressed(object? s, PointerPressedEventArgs e) => e.Handled = true;

    /// <summary>Validação real (o `Contains('@')` do paywall deixa passar "@" e "a@b").</summary>
    private static bool ValidEmail(string e)
    {
        if (e.Length < 5 || e.Contains(' ')) return false;
        var at = e.IndexOf('@');
        if (at <= 0 || at != e.LastIndexOf('@') || at == e.Length - 1) return false;
        var dom = e[(at + 1)..];
        return dom.Contains('.') && !dom.StartsWith('.') && !dom.EndsWith('.') && dom.Length >= 3;
    }

    private void ComplaintSay(string text, Avalonia.Media.IBrush brush)
    { ComplaintStatus.Text = text; ComplaintStatus.Foreground = brush; }

    private async void OnSubmitComplaint(object? s, RoutedEventArgs e)
    {
        var name = (ComplaintName.Text ?? "").Trim();
        var email = (ComplaintEmail.Text ?? "").Trim();
        var msg = (ComplaintMsg.Text ?? "").Trim();

        // validação ANTES do POST — nada sai daqui inválido
        if (name.Length == 0) { ComplaintSay("Escreve o teu nome primeiro.", Red()); return; }
        if (!ValidEmail(email)) { ComplaintSay("Esse email não parece válido — confirma o endereço.", Red()); return; }
        if (msg.Length == 0) { ComplaintSay("Escreve a tua reclamação — o campo está vazio.", Red()); return; }

        // o "logon" fica guardado (mesmas chaves do perfil — não duplica estado)
        Ai.AiConfig.SetProfile("display_name", name);
        Ai.AiConfig.SetProfile("klip_email", email);
        LoadProfile();   // mantém o flyout da conta em sincronia

        var consent = ComplaintConsent.IsChecked == true;
        var cat = (ComplaintCat.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "geral";

        ComplaintSendBtn.IsEnabled = false;
        ComplaintSay("A enviar…", Gray());

        // SEM consentimento não se recolhe nada. COM consentimento, recolhe-se fora da UI thread.
        try
        {
            System.Text.Json.Nodes.JsonObject? machine = null, usage = null;
            if (consent)
            {
                var mode = _aiSelection; var layers = _layers.Count;
                machine = await Task.Run(() => Complaints.Machine());
                usage = await Task.Run(() => Complaints.Usage(mode, layers));
            }

            var (ok, info) = await Complaints.SubmitAsync(WorkerRoot(), name, email, cat, msg, consent, machine, usage);
            if (ok)
            {
                // A folha é nossa e a data é constante, mas `info` vem do fio: limita-se o que se pinta.
                string quando = string.IsNullOrWhiteSpace(info) || info.Length > 40 ? "quinta-feira 08:00" : info;
                ComplaintSay($"✓ Recebida. Entra na folha desta semana — entrega {quando}.", Green());
                ComplaintMsg.Text = "";
                OpLog.Op("COMPLAINT", $"enviada · categoria={cat} · diagnóstico={(consent ? "sim" : "não")}");
            }
            else ComplaintSay($"✗ Não foi enviada ({info}). O teu texto continua aqui — tenta outra vez.", Red());
        }
        catch (Exception ex)
        {
            // async void sem catch = crash do processo. E sem o finally o botão ficava trancado para sempre.
            ComplaintSay($"✗ Não foi enviada ({ex.Message}). O teu texto continua aqui — tenta outra vez.", Red());
        }
        finally { ComplaintSendBtn.IsEnabled = true; }
    }

    // ================= TUTORIAL 1ª utilização §BETA (3 cards) =================

    /// <summary>3 cards, nada mais. O ícone é grande e carrega o significado; o texto é a legenda.
    /// Glifos reaproveitados da própria app: K (o chip da title bar), ✦ (KLIP AI), ◆ (créditos).</summary>
    private static readonly (string icon, string title, string text)[] TutCards =
    {
        ("K", "Bem-vindo ao KLIP", "Animação vetorial — do esboço ao vídeo, na mesma tela."),
        ("✦", "Tens IA lá dentro", "Escreve o que queres. Ela desenha e anima por ti."),
        ("$", "Pagas por créditos", "A IA gasta créditos. Carregas no cartão, aqui em cima."),
    };
    private int _tutStep;

    /// <summary>ⓘ na title bar — rever a introdução quando se quiser (ShowTutorial já faz reset).</summary>
    private void OnReplayTutorial(object? s, RoutedEventArgs e) => ShowTutorial();

    private void ShowTutorial()
    {
        _tutStep = 0;
        RenderTutorial();
        TutorialPanel.IsVisible = true;
        // marcado ao MOSTRAR: se a app fechar a meio, não volta a chatear (nem noutra composição)
        Onboarding.HasSeenTutorial = true;
        // foco no cartão: o Avalonia só entrega teclas NÃO tratadas ao Window, e se o foco ficasse
        // na caixa do chat (por trás) o Esc/setas nunca cá chegavam. Post: só focar depois de visível.
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => TutNext.Focus(), Avalonia.Threading.DispatcherPriority.Input);
    }

    private void RenderTutorial()
    {
        var c = TutCards[_tutStep];
        // O card dos créditos usa a imagem do cartão (o mesmo da barra); os outros usam glifo.
        bool cartao = c.icon == "$";
        TutIconImg.IsVisible = cartao;
        TutIcon.IsVisible = !cartao;
        TutIcon.Text = c.icon;
        TutIcon.FontSize = _tutStep == 0 ? 44 : 50;   // o K é uma letra; o ✦ pede mais corpo
        TutTitle.Text = c.title;
        TutText.Text = c.text;

        var dots = new[] { TutDot0, TutDot1, TutDot2 };
        for (int i = 0; i < dots.Length; i++)
            dots[i].Background = new Avalonia.Media.SolidColorBrush(
                Avalonia.Media.Color.Parse(i == _tutStep ? "#6D5EF6" : "#ECECEA"));

        TutBack.IsEnabled = _tutStep > 0;
        TutNext.Content = _tutStep == TutCards.Length - 1 ? "Começar" : "Seguinte";
    }

    private void OnTutorialNext(object? s, RoutedEventArgs e)
    {
        if (_tutStep >= TutCards.Length - 1) { CloseTutorial(); return; }
        _tutStep++; RenderTutorial();
    }

    private void OnTutorialBack(object? s, RoutedEventArgs e)
    { if (_tutStep > 0) { _tutStep--; RenderTutorial(); } }

    private void OnSkipTutorial(object? s, RoutedEventArgs e) => CloseTutorial();
    private void OnTutorialBackdrop(object? s, PointerPressedEventArgs e) => CloseTutorial();
    private void OnTutorialCardPressed(object? s, PointerPressedEventArgs e) => e.Handled = true;

    private void CloseTutorial()
    {
        TutorialPanel.IsVisible = false;
        Onboarding.HasSeenTutorial = true;
    }

    // ================= TIMELINE EDITOR (keyframes + scrub) =================
    private bool _timelineOpen;
    private double _previewT;                    // tempo mostrado no canvas (scrub)
    private bool _tlScrubbing;
    private readonly Avalonia.Media.Imaging.WriteableBitmap?[] _tlWb = new Avalonia.Media.Imaging.WriteableBitmap?[2];
    private int _tlIx, _tlW, _tlH;
    private const double TlGutter = 118;         // largura da coluna de nomes (px lógicos)

    private void OnToggleTimeline(object? s, RoutedEventArgs e)
    {
        _timelineOpen = !_timelineOpen;
        TimelinePanel.IsVisible = _timelineOpen;
        TimelineBtn.Foreground = new Avalonia.Media.SolidColorBrush(
            Avalonia.Media.Color.Parse(_timelineOpen ? "#6D5EF6" : "#5E5E5B"));
        if (_timelineOpen) Avalonia.Threading.Dispatcher.UIThread.Post(RenderTimeline,
            Avalonia.Threading.DispatcherPriority.Background);
    }

    private static IEnumerable<double> LayerKeyTimes(Layer l)
    {
        foreach (var tr in new[] { l.PosX, l.PosY, l.Rotation, l.Scale, l.ScaleX, l.ScaleY,
                                    l.SkewX, l.BlurRadius, l.Opacity, l.TrimStart, l.TrimEnd })
            if (tr is { Keys.Count: > 0 })
                foreach (var k in tr.Keys) yield return k.Time;
    }

    private void RenderTimeline()
    {
        if (!_timelineOpen) return;
        var b = TimelinePanel.Bounds;
        if (b.Width < 30) return;
        double rs = Math.Clamp((TopLevel.GetTopLevel(this)?.RenderScaling) ?? 1.0, 0.5, 4.0);
        int w = Math.Max(2, (int)(b.Width * rs)), h = Math.Max(2, (int)(136 * rs));
        if (_tlW != w || _tlH != h)
        {
            _tlWb[0] = new Avalonia.Media.Imaging.WriteableBitmap(new PixelSize(w, h), new Vector(96, 96),
                Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
            _tlWb[1] = new Avalonia.Media.Imaging.WriteableBitmap(new PixelSize(w, h), new Vector(96, 96),
                Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
            _tlW = w; _tlH = h;
        }
        _tlIx ^= 1;
        var wb = _tlWb[_tlIx]!;
        double D = Math.Max(0.1, _motionDur);
        float G = (float)(TlGutter * rs);
        float trackW = w - G - 8 * (float)rs;
        int n = _layers.Count;
        float rowH = n > 0 ? Math.Min(26 * (float)rs, (h - 22 * (float)rs) / n) : 0;
        float top = 22 * (float)rs;
        float Xt(double t) => G + (float)(t / D) * trackW;

        try
        {
            using var fb = wb.Lock();
            var info = new SkiaSharp.SKImageInfo(w, h, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
            using var surf = SkiaSharp.SKSurface.Create(info, fb.Address, fb.RowBytes);
            var c = surf.Canvas;
            c.Clear(new SkiaSharp.SKColor(0xFFFBFBFA));
            using var tick = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(0xFFDDDDDA), IsAntialias = true, StrokeWidth = 1 };
            using var lbl = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(0xFF9A9A97), IsAntialias = true };
            using var name = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(0xFF4A4A47), IsAntialias = true };
            using var lblFont = new SkiaSharp.SKFont { Size = 9.5f * (float)rs };
            using var nameFont = new SkiaSharp.SKFont { Size = 10.5f * (float)rs };
            using var rowbg = new SkiaSharp.SKPaint { IsAntialias = false };
            using var dot = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(0xFF6D5EF6), IsAntialias = true };

            // ruler: ticks a cada 0.5s
            for (double t = 0; t <= D + 1e-6; t += 0.5)
            {
                float x = Xt(t);
                c.DrawLine(x, 0, x, h, tick);
                c.DrawText($"{t:0.0}s", x + 3 * (float)rs, 12 * (float)rs, lblFont, lbl);
            }
            // linha do gutter
            c.DrawLine(G, 0, G, h, tick);

            // rows + keyframes
            for (int i = 0; i < n; i++)
            {
                var layer = _layers[n - 1 - i];   // topo primeiro (como o painel de camadas)
                float y = top + i * rowH;
                if (i % 2 == 1) { rowbg.Color = new SkiaSharp.SKColor(0x08000000); c.DrawRect(0, y, w, rowH, rowbg); }
                c.DrawText(layer.Name.Length > 15 ? layer.Name[..15] : layer.Name, 8 * (float)rs, y + rowH * 0.62f, nameFont, name);
                float cy = y + rowH / 2;
                foreach (var kt in LayerKeyTimes(layer).Distinct())
                {
                    float x = Xt(kt);
                    c.DrawCircle(x, cy, 3.2f * (float)rs, dot);
                }
            }

            // playhead
            using var ph = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(0xFFE0245E), IsAntialias = true, StrokeWidth = 1.5f * (float)rs };
            float px = Xt(_previewT);
            c.DrawLine(px, 0, px, h, ph);
            using var phead = new SkiaSharp.SKPath();
            phead.MoveTo(px - 5 * (float)rs, 0); phead.LineTo(px + 5 * (float)rs, 0); phead.LineTo(px, 8 * (float)rs); phead.Close();
            using var phfill = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(0xFFE0245E), IsAntialias = true };
            c.DrawPath(phead, phfill);
            c.Flush();
        }
        catch { return; }
        TimelineImg.Source = wb;
        TimeLbl.Text = $"{_previewT:0.00}s";
    }

    private void TimelineScrub(Point p)
    {
        var b = TimelineImg.Bounds;
        if (b.Width < 30) return;
        double trackW = b.Width - TlGutter - 8;
        double t = (p.X - TlGutter) / Math.Max(1, trackW) * Math.Max(0.1, _motionDur);
        _previewT = Math.Clamp(t, 0, _motionDur);
        RenderView(_previewT);
        RenderTimeline();
    }

    private void OnTimelinePressed(object? s, PointerPressedEventArgs e)
    { _tlScrubbing = true; TimelineScrub(e.GetPosition(TimelineImg)); }
    private void OnTimelineMoved(object? s, PointerEventArgs e)
    { if (_tlScrubbing) TimelineScrub(e.GetPosition(TimelineImg)); }
    private void OnTimelineReleased(object? s, PointerReleasedEventArgs e) => _tlScrubbing = false;

    // ---------------- title bar custom (full bleed) + multi-composição ----------------
    private void OnTitleBarPressed(object? s, PointerPressedEventArgs e)
    {
        if (e.ClickCount == 2) { OnMaxRestore(null, new RoutedEventArgs()); return; }
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }
    private void OnMinimize(object? s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaxRestore(object? s, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseWindow(object? s, RoutedEventArgs e) => Close();

    /// <summary>Nova composição: 2ª janela KLIP INDEPENDENTE (doc + IA + bus próprios), lado a lado.</summary>
    private void OnNewComposition(object? s, RoutedEventArgs e)
    {
        var second = new MainWindow();
        var scr = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (scr is not null)
        {
            var wa = scr.WorkingArea;
            int half = wa.Width / 2;
            WindowState = WindowState.Normal;
            Position = new PixelPoint(wa.X, wa.Y);
            Width = half / scr.Scaling; Height = wa.Height / scr.Scaling;
            second.Position = new PixelPoint(wa.X + half, wa.Y);
            second.Width = half / scr.Scaling; second.Height = wa.Height / scr.Scaling;
        }
        second.Show();
    }

    // ---------------- spinner "a pensar" (esconde as ações; só palavras espertas) ----------------
    private Avalonia.Threading.DispatcherTimer? _thinkTimer;
    private double _spinAngle;
    private int _wordIx;
    private static readonly string[] ThinkWords =
    {
        "A pensar…", "A sintetizar…", "A compor…", "A raciocinar…", "A desenhar…",
        "A imaginar…", "A afinar…", "A equilibrar…", "A dar vida…", "A polir…",
    };

    private void StartThinking()
    {
        _wordIx = 0; _spinAngle = 0;
        ThinkingWord.Text = ThinkWords[0];
        ThinkingBar.IsVisible = true;
        int tick = 0;
        _thinkTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(45) };
        _thinkTimer.Tick += (_, _) =>
        {
            _spinAngle = (_spinAngle + 14) % 360;
            Spinner.RenderTransform = new Avalonia.Media.RotateTransform(_spinAngle);
            if (++tick % 34 == 0)   // ~1.5s → nova palavra
            {
                _wordIx = (_wordIx + 1) % ThinkWords.Length;
                ThinkingWord.Text = ThinkWords[_wordIx];
            }
        };
        _thinkTimer.Start();
    }

    private void StopThinking()
    {
        _thinkTimer?.Stop(); _thinkTimer = null;
        ThinkingBar.IsVisible = false;
    }

    // ---------------- menu AI (1 popup: KLIP AI/Sonnet/Haiku/Claude Code/BYOK) ----------------
    private string _aiSelection = "klip";
    private static readonly Dictionary<string, string> AiLabels = new()
    { ["klip"] = "✦ KLIP AI", ["sonnet"] = "Sonnet 5", ["haiku"] = "Haiku 4.5", ["cli"] = "Claude Code", ["byok"] = "BYOK" };

    private void OnPickAi(object? s, RoutedEventArgs e)
    {
        if (s is not Button b || b.Tag is not string sel) return;
        _aiSelection = sel;
        AiMenuLabel.Text = AiLabels.GetValueOrDefault(sel, "✦ KLIP AI");
        ByokPanel.IsVisible = sel == "byok";
        if (sel == "byok") { ByokKey.Text ??= Ai.AiConfig.ResolveApiKey(); ValidateByok(); }
    }

    private System.Threading.CancellationTokenSource? _byokCts;
    private void OnByokChanged(object? s, TextChangedEventArgs e) => ValidateByok();

    private async void ValidateByok()
    {
        var key = (ByokKey.Text ?? "").Trim();
        if (key.Length == 0) { ByokStatus.Text = ""; return; }
        if (!key.StartsWith("sk-ant-") || key.Length < 40)
        { ByokStatus.Text = "✗"; ByokStatus.Foreground = Red(); return; }
        ByokStatus.Text = "…"; ByokStatus.Foreground = Gray();
        _byokCts?.Cancel();
        _byokCts = new System.Threading.CancellationTokenSource();
        var ct = _byokCts.Token;
        bool ok;
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, "https://api.anthropic.com/v1/models?limit=1");
            req.Headers.Add("x-api-key", key);
            req.Headers.Add("anthropic-version", "2023-06-01");
            var resp = await http.SendAsync(req, ct);
            ok = resp.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) { return; }
        catch { ByokStatus.Text = "✗"; ByokStatus.Foreground = Red(); return; }
        if (ct.IsCancellationRequested) return;
        if (ok)
        {
            ByokStatus.Text = "✓"; ByokStatus.Foreground = Green();
            _aiSelection = "byok"; AiMenuLabel.Text = "BYOK ✓";
            await System.Threading.Tasks.Task.Run(() => Ai.AiConfig.SetProfile("api_key", key));
        }
        else { ByokStatus.Text = "✗"; ByokStatus.Foreground = Red(); }
    }

    private static Avalonia.Media.IBrush Red() => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#E0245E"));
    private static Avalonia.Media.IBrush Green() => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#12B36A"));
    private static Avalonia.Media.IBrush Gray() => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#9A9A97"));

    private async void OnSendChat(object? s, RoutedEventArgs e)
    {
        var prompt = ChatInput.Text?.Trim();
        if (string.IsNullOrEmpty(prompt) || _chatCts is not null) return;
        ChatInput.Text = "";
        AppendChat("Tu:", prompt);
        ChatSend.IsVisible = false;
        ChatStop.IsVisible = true;
        ChatInput.IsEnabled = false;
        _chatCts = new System.Threading.CancellationTokenSource();
        StartThinking();


        void OnEvent(string kind, string data) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            switch (kind)
            {
                // SÓ os pensamentos do Claude aparecem. Ações/tools ficam escondidas atrás do spinner.
                case "text": AppendChat("", data); break;
                case "tool": case "tool_result": case "meta": break;   // silenciado — o spinner mostra o progresso
                case "nocredits": AppendChat("", "Sem créditos — abre ◆ para recarregar."); OpenPayment(); break;
                case "error": AppendChat("✗", data); break;
                case "usage":
                {
                    var parts = data.Split('|');
                    if (parts.Length == 2 && long.TryParse(parts[0], out var ti) && long.TryParse(parts[1], out var to))
                        _tokTotal += ti + to;
                    UpdateTokens();
                    break;
                }
                case "usage_cost":
                    if (double.TryParse(data, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var cost)) _costUsd += cost;
                    UpdateTokens();
                    break;
            }
            Refresh();   // o bus muda o doc — reflete já
        });

        try
        {
            if (_aiSelection == "cli")
            {
                _cli.ModelAlias = "opus";
                await System.Threading.Tasks.Task.Run(() => _cli.Send(prompt!, OnEvent, _chatCts.Token));
            }
            else
            {
                _api.CreditsMode = _aiSelection != "byok";   // créditos = CF worker; byok = chave própria
                _api.ModelId = _aiSelection switch
                { "sonnet" => "claude-sonnet-5", "haiku" => "claude-haiku-4-5", _ => "claude-opus-4-8" };
                await System.Threading.Tasks.Task.Run(() => _api.Send(prompt!, OnEvent, _chatCts.Token));
            }
        }
        catch (Exception ex) { AppendChat("✗", ex.Message); }
        finally
        {
            _chatCts = null;
            StopThinking();
            ChatSend.IsVisible = true;
            ChatStop.IsVisible = false;
            ChatInput.IsEnabled = true;
            Refresh();
        }
    }

    // ---------------- render / state ----------------
    private double _motionDur = 4.0, _motionFps = 30;
    private Avalonia.Threading.DispatcherTimer? _playTimer;
    private double _playT;

    private Comp? _compCache;   // reusa o comp durante pan/zoom (o doc não muda) — invalidado no Mutate
    private Comp BuildComp() => _compCache ??=
        new(W, H, _motionFps, _motionDur, 0xFFFFFFFF, new List<Layer>(_layers),
            BackgroundArgb2: null, Camera: BuildCamera());
    private void InvalidateComp() => _compCache = null;

    private void OnPlay(object? s, RoutedEventArgs e)
    {
        if (_playTimer is not null) { StopPlayback(); return; }
        _playT = 0;
        _playTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000 / _motionFps) };
        _playTimer.Tick += (_, _) =>
        {
            _playT += 1.0 / _motionFps;
            if (_playT >= _motionDur) { StopPlayback(); return; }
            _previewT = _playT;
            RenderView(_playT);
            if (_timelineOpen) RenderTimeline();   // playhead segue a reprodução
        };
        _playTimer.Start();
        AudioPlayFrom(0);                 // DAW: o som arranca colado ao playhead
        PlayBtn.Content = "■";
    }

    private void StopPlayback()
    {
        _playTimer?.Stop();
        _playTimer = null;
        AudioStop();
        PlayBtn.Content = "▶";
        Refresh();
    }

    // ---- vista: whiteboard infinito (zoom na roda, pan no botão do meio); re-render por
    // zoom = vetores sempre nítidos, nunca um PNG esticado ----
    private double _vs = 1, _vox, _voy;               // valores RENDERIZADOS (actuais)
    private double _vsT = 1, _voxT, _voyT;            // ALVOS — a vista faz ease até eles (nunca snap)
    private bool _viewInit;
    private bool _viewUserAdjusted;      // true depois de o utilizador dar zoom/pan → não reajustar sozinho
    private double _lastBoundsW, _lastBoundsH;
    private bool _panning;
    private bool _handTool;              // ferramenta MÃO (space ou botão) → arrastar com esquerdo = pan
    private Point _panLast;
    private Avalonia.Threading.DispatcherTimer? _viewAnim;

    private void InitView()
    {
        var b = CanvasHost.Bounds;
        if (b.Width < 50 || b.Height < 50) return;
        _vs = Math.Min((b.Width - 90) / W, (b.Height - 90) / H);
        if (_vs <= 0.001 || double.IsNaN(_vs) || double.IsInfinity(_vs)) _vs = 1;
        _vox = (b.Width - W * _vs) / 2.0;
        _voy = (b.Height - H * _vs) / 2.0;
        _vsT = _vs; _voxT = _vox; _voyT = _voy;
        _viewInit = true;
        _lastBoundsW = b.Width; _lastBoundsH = b.Height;
    }

    /// <summary>Arranca o ease suave da vista (actual → alvo). Nunca salta: tudo desliza.</summary>
    private void KickViewAnim()
    {
        if (_viewAnim is not null) return;
        _interacting = true;
        _viewAnim = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000.0 / 90) };
        _viewAnim.Tick += (_, _) =>
        {
            const double k = 0.28;   // factor de ease por tick (suave, ~5-6 frames p/ assentar)
            _vs += (_vsT - _vs) * k;
            _vox += (_voxT - _vox) * k;
            _voy += (_voyT - _voy) * k;
            bool done = Math.Abs(_vsT - _vs) < 1e-4 * Math.Max(1, _vsT)
                      && Math.Abs(_voxT - _vox) < 0.2 && Math.Abs(_voyT - _voy) < 0.2;
            if (done) { _vs = _vsT; _vox = _voxT; _voy = _voyT; _viewAnim!.Stop(); _viewAnim = null; _interacting = false; }
            RenderView(_playTimer is null ? 0 : _playT);   // frame final (done→_interacting false) sai nítido
            UpdateOverlay();
        };
        _viewAnim.Start();
    }

    /// <summary>Pan imediato (arrasto directo com a mão) — segue o dedo sem lag.</summary>
    private void PanBy(double dx, double dy)
    {
        _vox += dx; _voy += dy; _voxT = _vox; _voyT = _voy;
        _viewUserAdjusted = true; _interacting = true;
        RenderView(_playTimer is null ? 0 : _playT);
        UpdateOverlay();
    }

    /// <summary>Fim de interação → um render NÍTIDO (sombra com blur completo).</summary>
    private void SettleView()
    {
        if (!_interacting) return;
        _interacting = false;
        RenderView(_playTimer is null ? 0 : _playT);
    }

    private readonly Avalonia.Media.Imaging.WriteableBitmap?[] _viewWb = new Avalonia.Media.Imaging.WriteableBitmap?[2];
    private int _wbIx, _wbW, _wbH;
    private bool _interacting;   // true durante pan/zoom/drag → render "fast" (sombra chapada)

    private void RenderView(double t)
    {
        var b = CanvasHost.Bounds;
        if (b.Width < 10 || b.Height < 10) return;
        if (!_viewInit) InitView();

        // guarda: escala degenerada ou artboard totalmente fora do viewport → re-ajustar
        bool degenerate = _vs < 0.01 || double.IsNaN(_vs) || double.IsInfinity(_vs)
            || double.IsNaN(_vox) || double.IsNaN(_voy);
        bool offscreen = _vox + W * _vs < 20 || _voy + H * _vs < 20
            || _vox > b.Width - 20 || _voy > b.Height - 20;
        if (degenerate || (offscreen && !_viewUserAdjusted)) { InitView(); }

        double rs = (TopLevel.GetTopLevel(this)?.RenderScaling) ?? 1.0;
        rs = Math.Clamp(rs, 0.5, 4.0);
        int vw = Math.Max(2, (int)(b.Width * rs)), vh = Math.Max(2, (int)(b.Height * rs));

        try
        {
            // ── CAMINHO RÁPIDO: Skia DIRETO no buffer do ecrã, duplo-buffer — SEM PNG, SEM cópias, sem freeze ──
            if (_wbW != vw || _wbH != vh)
            {
                _viewWb[0] = new Avalonia.Media.Imaging.WriteableBitmap(new PixelSize(vw, vh), new Vector(96, 96),
                    Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
                _viewWb[1] = new Avalonia.Media.Imaging.WriteableBitmap(new PixelSize(vw, vh), new Vector(96, 96),
                    Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
                _wbW = vw; _wbH = vh;
            }
            _wbIx ^= 1;
            var wb = _viewWb[_wbIx]!;
            var allGuides = _guides.Count + _ruleGuides.Count > 0 ? _guides.Concat(_ruleGuides).ToList() : null;
            using (var fb = wb.Lock())
            {
                var info = new SkiaSharp.SKImageInfo(vw, vh, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
                using var surface = SkiaSharp.SKSurface.Create(info, fb.Address, fb.RowBytes);
                if (surface is not null)
                {
                    EngineExport.DrawView(surface.Canvas, BuildComp(), t,
                        (float)(_vs * rs), (float)(_vox * rs), (float)(_voy * rs), allGuides, fast: _interacting);
                    surface.Canvas.Flush();
                }
            }
            Canvas.Source = wb;   // troca a referência → Avalonia re-carrega, garantido
        }
        catch (Exception ex)
        {
            AppendChat("✗", "render: " + ex.Message);
            try { InitView(); } catch { }
        }
    }

    private void OnFitView(object? s, RoutedEventArgs e)
    { _viewUserAdjusted = false; _viewInit = false; InitView(); RenderView(0); UpdateOverlay(); }

    /// <summary>DIAGNÓSTICO: dump do estado da vista + o PNG exato que RenderView produz.</summary>
    public object ApiExportView(string path)
    {
        var b = CanvasHost.Bounds;
        double rs = (TopLevel.GetTopLevel(this)?.RenderScaling) ?? 1.0;
        double rsClamped = Math.Clamp(rs, 0.5, 4.0);
        int vw = (int)(b.Width * rsClamped), vh = (int)(b.Height * rsClamped);
        byte[] png;
        try
        {
            png = EngineExport.RenderViewPng(BuildComp(), 0, vw, vh,
                (float)(_vs * rsClamped), (float)(_vox * rsClamped), (float)(_voy * rsClamped));
            File.WriteAllBytes(path, png);
        }
        catch (Exception ex)
        {
            return new { error = ex.ToString(), boundsW = b.Width, boundsH = b.Height, rs, _vs, _vox, _voy };
        }
        return new
        {
            path, boundsW = b.Width, boundsH = b.Height, rs, vw, vh,
            vs = _vs, vox = _vox, voy = _voy, viewInit = _viewInit,
            userAdjusted = _viewUserAdjusted, layers = _layers.Count,
            canvasSourceNull = Canvas.Source is null, pngBytes = png.Length,
        };
    }

    // ================= GRID LOGO: grelha de construção + âncoras + caneta =================
    private int _gridMode;                                            // 0 off · 1 círculos φ · 2 quadrícula · 3 ambas
    private readonly List<EngineExport.Guide> _guides = new();
    private readonly List<EngineExport.Guide> _ruleGuides = new();   // guias arrastadas das réguas (CorelDRAW)
    private bool _draggingGuide;
    private char _guideKind;   // 'h' ou 'v'

    // arrastar a partir da régua TOPO → guia horizontal; régua ESQUERDA → guia vertical
    private void OnRulerPressed(object? s, PointerPressedEventArgs e, char kind)
    { _draggingGuide = true; _guideKind = kind; e.Pointer.Capture((Avalonia.Input.IInputElement)s!); }

    private void OnRulerMoved(object? s, PointerEventArgs e)
    {
        if (!_draggingGuide) return;
        // posição do rato relativa ao CanvasHost → coords do canvas
        var p = e.GetPosition(CanvasHost);
        var c = ToCanvas(p);
        UpdateDragGuide((float)(_guideKind == 'h' ? c.Y : c.X));
    }

    private void OnRulerReleased(object? s, PointerReleasedEventArgs e)
    {
        if (!_draggingGuide) return;
        _draggingGuide = false;
        e.Pointer.Capture(null);
        // finalizar: a guia provisória (R<0) passa a permanente (R=0)
        for (int i = 0; i < _ruleGuides.Count; i++)
            if (_ruleGuides[i].R < 0)
                _ruleGuides[i] = _ruleGuides[i] with { R = 0 };
        RenderView(0);
    }

    private void UpdateDragGuide(float pos)
    {
        // guia provisória = a última da lista com kind actual e flag; simplif.: recriar
        _ruleGuides.RemoveAll(g => g.Kind == _guideKind && g.R < 0);   // R<0 marca "em arrasto"
        _ruleGuides.Add(new EngineExport.Guide(_guideKind, pos, 0, -1));
        RenderView(_playTimer is null ? 0 : _playT);
    }

    private void OnClearGuides(object? s, RoutedEventArgs e)
    { _ruleGuides.Clear(); RenderView(0); }

    private int _hoverGuide = -1;   // índice em _ruleGuides sob o cursor

    /// <summary>Hover perto de uma guia → mostra o botão ✕ para apagar.</summary>
    private void UpdateGuideHover(Point screen, Point canvasPt)
    {
        int found = -1;
        for (int i = 0; i < _ruleGuides.Count; i++)
        {
            var g = _ruleGuides[i];
            double d = g.Kind == 'h' ? Math.Abs(canvasPt.Y - g.A) : Math.Abs(canvasPt.X - g.A);
            if (d * _vs <= 5) { found = i; break; }   // 5px de ecrã
        }
        _hoverGuide = found;
        if (found < 0) { if (GuideX.IsVisible) GuideX.IsVisible = false; return; }
        var g2 = _ruleGuides[found];
        // posiciona o ✕ na guia, perto do cursor
        double gx = g2.Kind == 'v' ? FromCanvas(g2.A, 0).X : screen.X;
        double gy = g2.Kind == 'h' ? FromCanvas(0, g2.A).Y : screen.Y;
        Avalonia.Controls.Canvas.SetLeft(GuideX, gx - 10);
        Avalonia.Controls.Canvas.SetTop(GuideX, gy - 10);
        GuideX.IsVisible = true;
    }

    private void OnDeleteGuide(object? s, RoutedEventArgs e)
    {
        if (_hoverGuide >= 0 && _hoverGuide < _ruleGuides.Count) _ruleGuides.RemoveAt(_hoverGuide);
        _hoverGuide = -1; GuideX.IsVisible = false;
        RenderView(0);
    }

    /// <summary>Snap de um valor a uma guia próxima (mesma orientação). thr em coords de canvas.</summary>
    private double SnapToGuide(double v, char kind, double thr)
    {
        foreach (var g in _ruleGuides)
            if (g.Kind == kind && Math.Abs(v - g.A) <= thr) return g.A;
        return v;
    }
    private readonly List<(string id, double x, double y)> _anchors = new();

    private void BuildGuides()
    {
        _guides.Clear();
        _anchors.Clear();
        double cx = W / 2.0, cy = H / 2.0;
        var circles = new List<(double x, double y, double r)>();

        if (_gridMode is 1 or 3)
        {
            const double phi = 1.6180339887;
            double[] radii = { 260, 260 / phi, 260 / (phi * phi), 260 / (phi * phi * phi) };
            foreach (var r in radii) circles.Add((cx, cy, r));
            double sat = 260 / phi, off = 260 / (phi * phi);
            circles.Add((cx - off, cy, sat)); circles.Add((cx + off, cy, sat));
            circles.Add((cx, cy - off, sat)); circles.Add((cx, cy + off, sat));
            foreach (var (x, y, r) in circles) _guides.Add(new EngineExport.Guide('c', (float)x, (float)y, (float)r));
        }
        if (_gridMode is 2 or 3)
        {
            for (double x = 0; x <= W; x += 50) _guides.Add(new EngineExport.Guide('v', (float)x, 0, 0));
            for (double y = 0; y <= H; y += 50) _guides.Add(new EngineExport.Guide('h', (float)y, 0, 0));
        }

        // âncoras: centros + cardeais + INTERSEÇÕES círculo-círculo (as âncoras "intencionais")
        void Add(double x, double y)
        {
            foreach (var a in _anchors) if (Math.Abs(a.x - x) < 0.6 && Math.Abs(a.y - y) < 0.6) return;
            _anchors.Add(($"P{_anchors.Count + 1}", Math.Round(x, 1), Math.Round(y, 1)));
        }
        foreach (var (x, y, r) in circles)
        { Add(x, y); Add(x + r, y); Add(x - r, y); Add(x, y + r); Add(x, y - r); }
        for (int i = 0; i < circles.Count; i++)
            for (int j = i + 1; j < circles.Count; j++)
            {
                var (x1, y1, r1) = circles[i]; var (x2, y2, r2) = circles[j];
                double d = Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));
                if (d < 1e-6 || d > r1 + r2 || d < Math.Abs(r1 - r2)) continue;
                double a = (r1 * r1 - r2 * r2 + d * d) / (2 * d);
                double h2 = r1 * r1 - a * a;
                if (h2 < 0) continue;
                double h = Math.Sqrt(h2);
                double mx = x1 + a * (x2 - x1) / d, my = y1 + a * (y2 - y1) / d;
                Add(mx + h * (y2 - y1) / d, my - h * (x2 - x1) / d);
                Add(mx - h * (y2 - y1) / d, my + h * (x2 - x1) / d);
            }
        if (_gridMode is 2 or 3)
            for (double x = 0; x <= W; x += 100)
                for (double y = 0; y <= H; y += 100) Add(x, y);
    }

    private void OnGridCycle(object? s, RoutedEventArgs e)
    {
        _gridMode = (_gridMode + 1) % 4;
        BuildGuides();
        ToolTip.SetTip(GridBtn, "Grelha de construção: " + _gridMode switch { 1 => "φ", 2 => "□", 3 => "φ+□", _ => "off" });
        GridBtn.Foreground = _gridMode > 0
            ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#6D5EF6"))
            : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#5E5E5B"));
        RenderView(0);
    }

    private Point SnapPoint(Point c)
    {
        if (_gridMode == 0 || _anchors.Count == 0) return c;
        double best = double.MaxValue; Point bp = c;
        double thr = 12.0 / Math.Max(_vs, 0.01);                      // 12px de ecrã
        foreach (var a in _anchors)
        {
            double d = Math.Sqrt((a.x - c.X) * (a.x - c.X) + (a.y - c.Y) * (a.y - c.Y));
            if (d < best && d <= thr) { best = d; bp = new Point(a.x, a.y); }
        }
        return bp;
    }

    // ---- caneta (pen tool) ----
    private bool _penMode;
    private readonly List<(Point p, Point? hOut)> _penAnchors = new();
    private Point _penPress;
    private bool _penPressed;
    private Point? _penDragHandle;

    private void OnPenToggle(object? s, RoutedEventArgs e)
    {
        _penMode = !_penMode;
        if (!_penMode) CancelPen();
        PenBtn.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(_penMode ? "#6D5EF6" : "#5E5E5B"));
        Inspector.Text = _penMode
            ? "CANETA: clique = canto · clicar+arrastar = curva · clica no 1º ponto p/ fechar · Enter = aberto · Esc = cancela"
            : "(clica num objeto)";
    }

    private void CancelPen()
    {
        _penAnchors.Clear(); _penPressed = false; _penDragHandle = null;
        PenPreview.IsVisible = false;
    }

    private void UpdatePenPreview(Point? cursor)
    {
        var pts = new List<Point>();
        foreach (var a in _penAnchors) pts.Add(FromCanvas(a.p.X, a.p.Y));
        if (cursor is { } cu) pts.Add(FromCanvas(cu.X, cu.Y));
        PenPreview.Points = pts;
        PenPreview.IsVisible = pts.Count > 1;
    }

    private void CommitPen(bool close)
    {
        if (_penAnchors.Count < 2) { CancelPen(); return; }
        double ox = W / 2.0, oy = H / 2.0;                            // d em coords centradas
        string F(double v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        var sb = new System.Text.StringBuilder();
        sb.Append($"M {F(_penAnchors[0].p.X - ox)} {F(_penAnchors[0].p.Y - oy)} ");
        int n = _penAnchors.Count;
        int segs = close ? n : n - 1;
        for (int i = 0; i < segs; i++)
        {
            var a = _penAnchors[i]; var b = _penAnchors[(i + 1) % n];
            Point? c1 = a.hOut is { } h1 ? new Point(a.p.X + h1.X, a.p.Y + h1.Y) : null;
            Point? c2 = b.hOut is { } h2 ? new Point(b.p.X - h2.X, b.p.Y - h2.Y) : null;
            if (c1 is null && c2 is null)
                sb.Append($"L {F(b.p.X - ox)} {F(b.p.Y - oy)} ");
            else
            {
                var p1 = c1 ?? a.p; var p2 = c2 ?? b.p;
                sb.Append($"C {F(p1.X - ox)} {F(p1.Y - oy)} {F(p2.X - ox)} {F(p2.Y - oy)} {F(b.p.X - ox)} {F(b.p.Y - oy)} ");
            }
        }
        if (close) sb.Append('Z');
        var name = $"pen-{_nameSeq++}";
        var d = sb.ToString().Trim();
        Mutate(() =>
        {
            _layers.Add(close
                ? new Layer(name, MorphTrack.Static(d), NextColor())
                : new Layer(name, MorphTrack.Static(d), 0x00FFFFFF, StrokeArgb: 0xFF232326, StrokeWidth: 8));
            _selected = _layers.Count - 1;
        });
        CancelPen();
    }

    public object ApiSetGrid(string kind)
    {
        _gridMode = kind switch { "circles" => 1, "square" => 2, "both" => 3, _ => 0 };
        BuildGuides();
        ToolTip.SetTip(GridBtn, "Grelha de construção: " + _gridMode switch { 1 => "φ", 2 => "□", 3 => "φ+□", _ => "off" });
        RenderView(0);
        return new { ok = true, anchors = _anchors.Count };
    }

    public object ApiListAnchors() => _anchors
        .Select(a => new { a.id, x = Math.Round(a.x - W / 2.0, 1), y = Math.Round(a.y - H / 2.0, 1) })
        .ToArray();

    // ---- paleta automática: blocos masonry desiguais + hex, espaçamento perfeito ----
    public object ApiExtractPalette(string id, double x, double y)
    {
        var l = Sel(id);
        if (l.ImagePath is null) throw new InvalidOperationException("a camada não é uma imagem");
        var colors = PaletteExtractor.Extract(l.ImagePath, 5);
        if (colors.Count == 0) throw new InvalidOperationException("não consegui ler cores");

        // masonry desigual (gap 10): bloco alto à esq. + 2 empilhados + coluna estreita
        const double G = 10;
        (double bx, double by, double w, double h)[] blocks =
        {
            (0, 0, 180, 290),
            (180 + G, 0, 130, 140), (180 + G, 140 + G, 130, 140),
            (180 + 130 + 2 * G, 0, 105, 185), (180 + 130 + 2 * G, 185 + G, 105, 95),
        };
        double totW = 180 + 130 + 105 + 2 * G, totH = 290;

        Mutate(() =>
        {
            for (int i = 0; i < colors.Count && i < blocks.Length; i++)
            {
                var b = blocks[i];
                double cxB = x + b.bx + b.w / 2 - totW / 2;
                double cyB = y + b.by + b.h / 2 - totH / 2;
                uint col = colors[i];
                _layers.Add(new Layer($"swatch-{_nameSeq++}",
                    MorphTrack.Static(Shapes.Rect(b.w / 2, b.h / 2)), col,
                    PosX: Track.Const(cxB), PosY: Track.Const(cyB)));
                string hex = "#" + (col & 0xFFFFFF).ToString("X6");
                var d = TextShape.TextPathD(hex, 17);
                if (d is not null)
                    _layers.Add(new Layer($"hex-{_nameSeq++}", MorphTrack.Static(d),
                        PaletteExtractor.IsDark(col) ? 0xFFFFFFFF : 0xFF232326,
                        PosX: Track.Const(cxB - b.w / 2 + 46), PosY: Track.Const(cyB + b.h / 2 - 18)));
            }
        });
        return new { colors = colors.Select(c => "#" + (c & 0xFFFFFF).ToString("X6")).ToArray() };
    }

    public object ApiRemoveBackground(string id)
    {
        int ix = FindLayer(id);
        var l = Sel(id);
        if (l.ImagePath is null) throw new InvalidOperationException("a camada não é uma imagem");
        var (outPath, ms) = BgRemover.Remove(l.ImagePath);
        Mutate(() => _layers[ix] = l with { ImagePath = outPath });
        return new { path = outPath, ms };
    }

    // ================= FASE 9: SVG editável (nós) + rotoscoping =================

    private (int ix, Layer l, PathEdit e, int key) OpenEdit(string id, int keyIndex)
    {
        int ix = FindLayer(id);
        var l = Sel(id);
        var keys = l.Shape.Keys;
        if (keys.Count == 0) throw new InvalidOperationException("a camada não tem forma editável");
        int k = Math.Clamp(keyIndex, 0, keys.Count - 1);
        return (ix, l, PathEdit.Parse(keys[k].PathD), k);
    }
    private void CommitEdit(int ix, Layer l, int key, PathEdit e)
    {
        var ks = l.Shape.Keys.ToArray();
        ks[key] = ks[key] with { PathD = e.ToSvgPathData() };
        _layers[ix] = l with { Shape = new MorphTrack(ks) };
    }

    public object ApiListNodes(string id, int key)
    {
        var (_, _, e, _) = OpenEdit(id, key);
        var nodes = e.Enumerate().Select(n => new
        {
            i = n.i, contour = n.contour, x = n.node.Point.X, y = n.node.Point.Y,
            in_x = n.node.HandleIn.X, in_y = n.node.HandleIn.Y, out_x = n.node.HandleOut.X, out_y = n.node.HandleOut.Y,
            type = n.node.Type.ToString().ToLowerInvariant(),
        }).ToArray();
        return new { count = e.NodeCount, contours = e.Contours.Count, nodes };
    }

    public object ApiEditNode(string id, string op, int index, double dx, double dy, string? side, double t, string? type, int key)
    {
        var (ix, l, e, k) = OpenEdit(id, key);
        switch ((op ?? "").ToLowerInvariant())
        {
            case "move": e.MoveNode(index, dx, dy); break;
            case "insert": e.InsertNode(index, t <= 0 || t >= 1 ? 0.5 : t); break;
            case "delete": e.DeleteNode(index); break;
            case "set_handle": e.SetHandle(index, (side ?? "out").ToLowerInvariant() != "in", dx, dy); break;
            case "set_type": e.SetNodeType(index, (type ?? "corner").ToLowerInvariant() == "smooth" ? NodeType.Smooth : NodeType.Corner); break;
            default: throw new InvalidOperationException("op inválida (move|insert|delete|set_handle|set_type): " + op);
        }
        Mutate(() => CommitEdit(ix, l, k, e));
        return new { ok = true, count = e.NodeCount };
    }

    public object ApiSimplifyPath(string id, double tolerance, int key)
    {
        var (ix, l, e, k) = OpenEdit(id, key);
        int before = e.NodeCount;
        e.Simplify(tolerance <= 0 ? 2.0 : tolerance);
        int after = e.NodeCount;
        Mutate(() => CommitEdit(ix, l, k, e));
        return new { ok = true, before, after };
    }

    public object ApiImportSvg(string pathOrText, double x, double y)
    {
        var paths = SvgImport.CenterAll(SvgImport.ImportPaths(pathOrText));
        if (paths.Count == 0) throw new InvalidOperationException("nenhum <path> encontrado no SVG");
        var ids = new List<string>();
        Mutate(() =>
        {
            foreach (var p in paths)
            {
                var layer = new Layer($"svg-{_nameSeq++}", MorphTrack.Static(p.D), p.FillArgb ?? NextColor(),
                    PosX: Track.Const(x), PosY: Track.Const(y));
                _layers.Add(layer);
                ids.Add(layer.Name);
            }
            _selected = _layers.Count - 1;
        });
        return new { ids, count = paths.Count };
    }

    public object ApiTraceBitmap(string id, double threshold, double simplify, bool luma)
    {
        var l = Sel(id);
        if (l.ImagePath is null) throw new InvalidOperationException("a camada não é uma imagem/máscara");
        using var bmp = SkiaSharp.SKBitmap.Decode(l.ImagePath) ?? throw new InvalidOperationException("imagem ilegível");
        var d = BitmapTrace.AlphaToPath(bmp, (byte)Math.Clamp(threshold <= 0 ? 128 : threshold, 1, 255), simplify <= 0 ? 1.5 : simplify, luma);
        if (string.IsNullOrEmpty(d)) throw new InvalidOperationException("trace vazio — ajusta threshold/luma");
        string nid = "";
        Mutate(() =>
        {
            var layer = new Layer($"trace-{_nameSeq++}", MorphTrack.Static(d), NextColor());
            _layers.Add(layer); _selected = _layers.Count - 1; nid = layer.Name;
        });
        return new { id = nid, nodes = PathEdit.Parse(d).NodeCount, contours = BitmapTrace.ContourCount(d) };
    }

    public object ApiRoto(string id, double threshold, double simplify, bool asMatte, bool invert)
    {
        int ix = FindLayer(id);
        var l = Sel(id);
        if (l.ImagePath is null) throw new InvalidOperationException("a camada não é uma imagem");
        RotoResult r;
        try { r = RotoTrace.FromImage(l.ImagePath, (byte)Math.Clamp(threshold <= 0 ? 128 : threshold, 1, 255), simplify <= 0 ? 1.5 : simplify); }
        catch (Exception ex) { return new { ok = false, error = "roto: modelo em falta ou falhou — " + ex.Message }; }
        if (string.IsNullOrEmpty(r.D)) throw new InvalidOperationException("roto vazio (sujeito não isolado)");
        string clipId = Klip.Model.Ids.Next();
        Mutate(() =>
        {
            _layers.Add(new Layer($"roto-{_nameSeq++}", MorphTrack.Static(r.D), NextColor(), Id: clipId));
            if (asMatte) _layers[ix] = l with { MatteSourceId = clipId, Matte = invert ? MatteMode.AlphaInvert : MatteMode.AlphaNormal };
            _selected = _layers.Count - 1;
        });
        return new { clip_id = clipId, nodes = PathEdit.Parse(r.D).NodeCount, contours = r.Contours, ms = r.Ms };
    }

    public object ApiSetMatte(string id, string source, string mode)
    {
        int ix = FindLayer(id);
        var l = Sel(id);
        int six = FindLayer(source);
        if (six < 0) throw new InvalidOperationException("camada-fonte não encontrada: " + source);
        string key = _layers[six].Key;
        var m = (mode ?? "").ToLowerInvariant() switch
        {
            "alpha" or "alpha_normal" => MatteMode.AlphaNormal,
            "alpha_invert" => MatteMode.AlphaInvert,
            "luma" or "luma_normal" => MatteMode.LumaNormal,
            "luma_invert" => MatteMode.LumaInvert,
            _ => MatteMode.None,
        };
        Mutate(() => _layers[ix] = l with { MatteSourceId = key, Matte = m });
        return new { id, source = key, mode = m.ToString() };
    }

    // ================= FASE 10: emissor de partículas =================

    public object ApiSetParticles(string id, string? preset, double? rate, double? lifetime, double? speed,
        double? gravity, double? spread, double? direction, double? spin, double? spawnRadius, double? particleScale,
        double? fadeIn, double? fadeOut, string? colorA, string? colorB, int? seed)
    {
        int ix = FindLayer(id);
        var l = Sel(id);
        var p = l.Particles ?? new ParticleSpec();
        p = (preset ?? "").ToLowerInvariant() switch
        {
            "confetti" => p with { Rate = Track.Const(120), Lifetime = Track.Const(1.8), Speed = Track.Const(260), SpreadDeg = Track.Const(50), Gravity = Track.Const(520), DirectionDeg = -90, SpinDegPerSec = 360, ColorA = 0xFFE4162Bu, ColorB = 0xFF2D6CDFu, ColorByLife = false, FadeOut = 0.3 },
            "sparks" => p with { Rate = Track.Const(220), Lifetime = Track.Const(0.7), Speed = Track.Const(340), SpreadDeg = Track.Const(180), Gravity = Track.Const(700), ColorA = 0xFFFFF3B0u, ColorB = 0xFFE4162Bu, FadeOut = 0.5 },
            "smoke" => p with { Rate = Track.Const(40), Lifetime = Track.Const(2.5), Speed = Track.Const(60), SpreadDeg = Track.Const(35), Gravity = Track.Const(-40), DirectionDeg = -90, ParticleScale = Track.Const(2.2), ColorA = 0xFF9AA0A6u, ColorB = 0xFFCED2D6u, FadeIn = 0.2, FadeOut = 0.5 },
            "stars" => p with { Rate = Track.Const(60), Lifetime = Track.Const(1.5), Speed = Track.Const(40), SpreadDeg = Track.Const(180), Gravity = Track.Const(0), SpinDegPerSec = 120, SpawnRadius = Track.Const(60), FadeIn = 0.2, FadeOut = 0.4 },
            _ => p,
        };
        if (rate.HasValue) p = p with { Rate = Track.Const(rate.Value) };
        if (lifetime.HasValue) p = p with { Lifetime = Track.Const(lifetime.Value) };
        if (speed.HasValue) p = p with { Speed = Track.Const(speed.Value) };
        if (gravity.HasValue) p = p with { Gravity = Track.Const(gravity.Value) };
        if (spread.HasValue) p = p with { SpreadDeg = Track.Const(spread.Value) };
        if (direction.HasValue) p = p with { DirectionDeg = direction.Value };
        if (spin.HasValue) p = p with { SpinDegPerSec = spin.Value };
        if (spawnRadius.HasValue) p = p with { SpawnRadius = Track.Const(spawnRadius.Value) };
        if (particleScale.HasValue) p = p with { ParticleScale = Track.Const(particleScale.Value) };
        if (fadeIn.HasValue) p = p with { FadeIn = fadeIn.Value };
        if (fadeOut.HasValue) p = p with { FadeOut = fadeOut.Value };
        if (colorA != null) p = p with { ColorA = ParseColor(colorA, p.ColorA), ColorByLife = colorB != null || p.ColorByLife };
        if (colorB != null) p = p with { ColorB = ParseColor(colorB, p.ColorB), ColorByLife = false };
        if (seed.HasValue) p = p with { Seed = seed.Value };
        Mutate(() => _layers[ix] = l with { Particles = p });
        return new { ok = true, id, preset = preset ?? "" };
    }

    public object ApiClearParticles(string id)
    {
        int ix = FindLayer(id);
        var l = Sel(id);
        Mutate(() => _layers[ix] = l with { Particles = null });
        return new { ok = true };
    }

    public object ApiExportLottie(string path)
    {
        var (exported, skipped) = LottieExporter.Export(BuildComp(), path);
        return new { path, layers = exported, skipped };
    }

    public object ApiExportSvg(string path)
    {
        EngineExport.ExportSvg(BuildComp(), 0, path);
        return new { path };
    }

    public object ApiExportGif(string path)
    {
        var comp = BuildComp();
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try { Mp4Exporter.ExportGif(comp, path); }
            catch (Exception ex)
            { Avalonia.Threading.Dispatcher.UIThread.Post(() => AppendChat("✗", "GIF: " + ex.Message)); return; }
            Avalonia.Threading.Dispatcher.UIThread.Post(() => AppendChat("·", "GIF exportado: " + path));
        });
        return new { started = true, path };
    }

    private void OnExtractPalette(object? s, RoutedEventArgs e)
    {
        if (_selected < 0 || _layers[_selected].ImagePath is null)
        { AppendChat("✗", "seleciona uma camada de IMAGEM primeiro"); return; }
        try
        {
            var r = ApiExtractPalette(_layers[_selected].Name, 0, 0);
            AppendChat("·", "paleta extraída");
        }
        catch (Exception ex) { AppendChat("✗", ex.Message); }
    }

    // ---- ícone de sessão (efeito Chrome: logo + PFP na taskbar) ----
    private void SetSessionIcon()
    {
        try
        {
            var info = new SkiaSharp.SKImageInfo(256, 256, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Premul);
            using var surface = SkiaSharp.SKSurface.Create(info);
            var c = surface.Canvas;
            c.Clear(SkiaSharp.SKColors.Transparent);
            // logotipo: squircle violeta→magenta + K branco
            using (var sq = SkiaSharp.SKPath.ParseSvgPathData(Shapes.Superellipse(118, 118)))
            using (var p = new SkiaSharp.SKPaint { IsAntialias = true })
            {
                p.Shader = SkiaSharp.SKShader.CreateLinearGradient(new SkiaSharp.SKPoint(30, 20),
                    new SkiaSharp.SKPoint(226, 236),
                    new[] { new SkiaSharp.SKColor(0xFF6D5EF6), new SkiaSharp.SKColor(0xFFC13AF6) }, null,
                    SkiaSharp.SKShaderTileMode.Clamp);
                c.Save(); c.Translate(128, 128); c.DrawPath(sq!, p); c.Restore();
            }
            var kd = TextShape.TextPathD("K", 150, bold: true);
            if (kd is not null)
                using (var kp = SkiaSharp.SKPath.ParseSvgPathData(kd))
                using (var wp = new SkiaSharp.SKPaint { IsAntialias = true, Color = SkiaSharp.SKColors.White })
                { c.Save(); c.Translate(128, 128); c.DrawPath(kp!, wp); c.Restore(); }
            // avatar da sessão (Chrome-style) no canto
            if (File.Exists(Ai.AiConfig.PfpPath))
            {
                using var pf = SkiaSharp.SKBitmap.Decode(Ai.AiConfig.PfpPath);
                if (pf is not null)
                {
                    c.Save();
                    using var clip = new SkiaSharp.SKPath();
                    clip.AddCircle(186, 186, 58);
                    c.ClipPath(clip, antialias: true);
                    c.DrawBitmap(pf, new SkiaSharp.SKRect(128, 128, 244, 244));
                    c.Restore();
                    using var ring = new SkiaSharp.SKPaint
                    { IsAntialias = true, Style = SkiaSharp.SKPaintStyle.Stroke, StrokeWidth = 8, Color = SkiaSharp.SKColors.White };
                    c.DrawCircle(186, 186, 58, ring);
                }
            }
            c.Flush();
            using var img = surface.Snapshot();
            using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            Icon = new WindowIcon(new Bitmap(new MemoryStream(data.ToArray())));
        }
        catch { /* ícone é cosmético */ }
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        _viewUserAdjusted = true;
        var p = e.GetPosition(CanvasHost);
        bool zoomMod = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);

        if (zoomMod)
        {
            // ZOOM ancorado no cursor (Ctrl/⌘ + scroll, ou pinch mapeado p/ isto) — eased
            double f = e.Delta.Y > 0 ? 1.14 : 1 / 1.14;
            ZoomAtTarget(p, f);
        }
        else
        {
            // SCROLL LIVRE = PAN 2D (trackpad: 2 dedos ⇅⇆). Nunca zoom. Eased.
            const double S = 60;
            _voxT += e.Delta.X * S;
            _voyT += e.Delta.Y * S;
            KickViewAnim();
        }
        e.Handled = true;
    }

    private void ZoomAtTarget(Point p, double f)
    {
        double nz = Math.Clamp(_vsT * f, 0.02, 60);
        f = nz / _vsT;
        _voxT = p.X - (p.X - _voxT) * f;
        _voyT = p.Y - (p.Y - _voyT) * f;
        _vsT = nz;
        _viewUserAdjusted = true;
        KickViewAnim();
    }

    private void OnPinch(object? sender, Avalonia.Input.PinchEventArgs e)
    {
        // pinch do trackpad → zoom ancorado no centro do gesto
        var b = CanvasHost.Bounds;
        var center = new Point(e.ScaleOrigin.X * b.Width, e.ScaleOrigin.Y * b.Height);
        double f = _lastPinch <= 0 ? 1 : e.Scale / _lastPinch;
        _lastPinch = e.Scale;
        if (f is > 0.2 and < 5) ZoomAtTarget(center, f);
        e.Handled = true;
    }
    private double _lastPinch;
    private void OnPinchEnded(object? sender, Avalonia.Input.PinchEndedEventArgs e) => _lastPinch = 0;

    private void OnHandTool(object? s, RoutedEventArgs e)
    {
        _handTool = !_handTool;
        HandBtn.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(_handTool ? "#6D5EF6" : "#5E5E5B"));
        Cursor = new Avalonia.Input.Cursor(_handTool ? Avalonia.Input.StandardCursorType.Hand : Avalonia.Input.StandardCursorType.Arrow);
    }

    private void Refresh()
    {
        InvalidateComp();   // o doc pode ter mudado → reconstruir o comp
        RenderView(_previewT);
        if (_timelineOpen) RenderTimeline();
        LayerList.ItemsSource = _layers.Select((l, i) => $"{_layers.Count - 1 - i}. {_layers[_layers.Count - 1 - i].Name}").ToList();
        LayerList.SelectedIndex = _selected < 0 ? -1 : _layers.Count - 1 - _selected;
        UpdateInspector();
        UpdateOverlay();
    }

    private void PushHistory()
    {
        if (_histIx < _hist.Count - 1) _hist.RemoveRange(_histIx + 1, _hist.Count - _histIx - 1);
        _hist.Add(new List<Layer>(_layers));
        if (_hist.Count > 100) _hist.RemoveAt(0);
        _histIx = _hist.Count - 1;
    }

    private void Mutate(Action act) { act(); EnsureIds(); PushHistory(); Refresh(); }

    // ---------------- coords ----------------
    private (double scale, double offX, double offY) Fit() => (_vs, _vox, _voy);

    private Point ToCanvas(Point p) { var (s, ox, oy) = Fit(); return new((p.X - ox) / s, (p.Y - oy) / s); }
    private Point FromCanvas(double x, double y) { var (s, ox, oy) = Fit(); return new(ox + x * s, oy + y * s); }

    // ---------------- pointer ----------------
    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        var ctrl = e.GetPosition(CanvasHost);
        var props = e.GetCurrentPoint(CanvasHost).Properties;
        // pan: botão do meio, OU mão activa/espaço com o esquerdo
        if (props.IsMiddleButtonPressed || ((_handTool || _spaceDown) && props.IsLeftButtonPressed))
        { _panning = true; _panLast = ctrl; Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeAll); return; }

        if (_penMode && e.GetCurrentPoint(Canvas).Properties.IsLeftButtonPressed)
        {
            var pc = SnapPoint(ToCanvas(ctrl));
            if (e.ClickCount == 2) { CommitPen(false); return; }
            if (_penAnchors.Count >= 2)
            {
                var first = _penAnchors[0].p;
                double thr = 12.0 / Math.Max(_vs, 0.01);
                if (Math.Abs(pc.X - first.X) < thr && Math.Abs(pc.Y - first.Y) < thr)
                { CommitPen(true); return; }
            }
            _penPress = pc; _penPressed = true; _penDragHandle = null;
            return;
        }

        var c = ToCanvas(ctrl);

        if (_selected >= 0 && EngineExport.LayerBounds(_layers[_selected], W, H) is { } r)
        {
            var hp = FromCanvas(r.x + r.w, r.y + r.h);
            if (Math.Abs(ctrl.X - hp.X) < 14 && Math.Abs(ctrl.Y - hp.Y) < 14)
            {
                _drag = Drag.Resize;
                double cxL = r.x + r.w / 2, cyL = r.y + r.h / 2;
                _resizeStartDist = Math.Max(8, Dist(c.X, c.Y, cxL, cyL));
                _resizeStartScale = _layers[_selected].Scale?.Eval(0) ?? 1.0;
                return;
            }
        }

        // selecção TOPMOST-VISÍVEL: a forma REAL sob o cursor (não a bbox) → clica através de formas grandes
        _selected = HitTop(c.X, c.Y);
        if (_selected >= 0) { _drag = Drag.Move; _lastCanvas = c; }
        Refresh();
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_panning)
        {
            var p = e.GetPosition(CanvasHost);
            PanBy(p.X - _panLast.X, p.Y - _panLast.Y);   // segue o dedo, sem lag
            _panLast = p;
            return;
        }
        if (_penMode)
        {
            var cur = ToCanvas(e.GetPosition(CanvasHost));
            if (_penPressed)
            {
                _penDragHandle = new Point(cur.X - _penPress.X, cur.Y - _penPress.Y);
                UpdatePenPreview(cur);
            }
            else UpdatePenPreview(SnapPoint(cur));
            return;
        }
        if (_drag == Drag.None)
        {
            var sp = e.GetPosition(CanvasHost);
            var cp = ToCanvas(sp);
            UpdateHover(cp);            // Canva: hover realça o item sob o cursor
            UpdateGuideHover(sp, cp);   // hover perto de guia → botão ✕
            return;
        }
        if (_selected < 0) return;
        _interacting = true;   // arrasto de camada → render fast (sem blur pesado da sombra)
        InvalidateComp();      // o layer vai mudar → cache do comp fora
        var c = ToCanvas(e.GetPosition(CanvasHost));
        var l = _layers[_selected];

        if (_drag == Drag.Move)
        {
            double nx = (l.PosX?.Eval(0) ?? 0) + (c.X - _lastCanvas.X);
            double ny = (l.PosY?.Eval(0) ?? 0) + (c.Y - _lastCanvas.Y);
            if (_snap) { nx = Math.Round(nx / Grid) * Grid; ny = Math.Round(ny / Grid) * Grid; }
            // snap-to-guide: aproxima o CENTRO da camada às guias das réguas
            double thr = 7.0 / Math.Max(_vs, 0.01);
            nx = SnapToGuide(W / 2.0 + nx, 'v', thr) - W / 2.0;
            ny = SnapToGuide(H / 2.0 + ny, 'h', thr) - H / 2.0;
            _layers[_selected] = l with { PosX = Track.Const(nx), PosY = Track.Const(ny) };
            _lastCanvas = c;
        }
        else if (_drag == Drag.Resize && EngineExport.LayerBounds(l, W, H) is { } r)
        {
            double cxL = r.x + r.w / 2, cyL = r.y + r.h / 2;
            double f = Dist(c.X, c.Y, cxL, cyL) / _resizeStartDist;
            _layers[_selected] = l with { Scale = Track.Const(Math.Clamp(_resizeStartScale * f, 0.05, 20)) };
        }

        RenderView(0);
        UpdateInspector();
        UpdateOverlay();
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_panning)
        {
            _panning = false;
            Cursor = new Avalonia.Input.Cursor(_handTool ? Avalonia.Input.StandardCursorType.Hand : Avalonia.Input.StandardCursorType.Arrow);
        }
        SettleView();   // fim da interação → render nítido
        if (_penMode && _penPressed)
        {
            double len = _penDragHandle is { } h ? Math.Sqrt(h.X * h.X + h.Y * h.Y) : 0;
            _penAnchors.Add((_penPress, len > 6.0 / Math.Max(_vs, 0.01) ? _penDragHandle : null));
            _penPressed = false;
            _penDragHandle = null;
            UpdatePenPreview(null);
            return;
        }
        if (_drag != Drag.None) PushHistory();
        _drag = Drag.None;
    }

    private static double Dist(double x1, double y1, double x2, double y2)
        => Math.Sqrt((x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2));

    /// <summary>Hover estilo Canva: realça a caixa do item VISÍVEL sob o cursor (não o selecionado).</summary>
    private void UpdateHover(Point c)
    {
        int h = HitTop(c.X, c.Y);
        if (h < 0 || h == _selected || EngineExport.LayerBounds(_layers[h], W, H) is not { } r)
        { if (HoverBox.IsVisible) HoverBox.IsVisible = false; return; }
        var tl = FromCanvas(r.x, r.y);
        var br = FromCanvas(r.x + r.w, r.y + r.h);
        Avalonia.Controls.Canvas.SetLeft(HoverBox, tl.X - 2);
        Avalonia.Controls.Canvas.SetTop(HoverBox, tl.Y - 2);
        HoverBox.Width = Math.Max(0, br.X - tl.X) + 4;
        HoverBox.Height = Math.Max(0, br.Y - tl.Y) + 4;
        HoverBox.IsVisible = true;
    }

    /// <summary>Índice da camada VISÍVEL mais acima cuja forma real contém o ponto (comp-space). -1 = nenhuma.</summary>
    private int HitTop(double px, double py)
    {
        for (int i = _layers.Count - 1; i >= 0; i--)
            if (EngineExport.HitLayer(_layers[i], px, py, W, H)) return i;
        return -1;
    }

    // ---------------- overlay / inspector ----------------
    private (double s, double ox, double oy) _rulerFit = (-1, -1, -1);

    private void DrawRulers()
    {
        // só reconstruir quando o mapeamento muda — mutar children em LayoutUpdated sem guarda = layout loop infinito
        var fit = Fit();
        if (Math.Abs(fit.scale - _rulerFit.s) < 1e-9 && Math.Abs(fit.offX - _rulerFit.ox) < 1e-9
            && Math.Abs(fit.offY - _rulerFit.oy) < 1e-9) return;
        _rulerFit = (fit.scale, fit.offX, fit.offY);

        var tickBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#4A4A56"));
        var textBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#8A8A93"));
        RulerTop.Children.Clear();
        RulerLeft.Children.Clear();
        for (int v = 0; v <= Math.Max(W, H); v += 50)
        {
            bool major = v % 100 == 0;
            if (v <= W)
            {
                double x = FromCanvas(v, 0).X;
                RulerTop.Children.Add(new Avalonia.Controls.Shapes.Line
                { StartPoint = new Point(x, major ? 6 : 12), EndPoint = new Point(x, 20), Stroke = tickBrush, StrokeThickness = 1 });
                if (major && v < W)
                {
                    var tb = new TextBlock { Text = v.ToString(), FontSize = 8.5, Foreground = textBrush };
                    Avalonia.Controls.Canvas.SetLeft(tb, x + 2);
                    Avalonia.Controls.Canvas.SetTop(tb, -1);
                    RulerTop.Children.Add(tb);
                }
            }
            if (v <= H)
            {
                double y = FromCanvas(0, v).Y;
                RulerLeft.Children.Add(new Avalonia.Controls.Shapes.Line
                { StartPoint = new Point(major ? 6 : 12, y), EndPoint = new Point(20, y), Stroke = tickBrush, StrokeThickness = 1 });
                if (major && v < H)
                {
                    var tb = new TextBlock { Text = v.ToString(), FontSize = 8.5, Foreground = textBrush };
                    Avalonia.Controls.Canvas.SetLeft(tb, 1);
                    Avalonia.Controls.Canvas.SetTop(tb, y + 1);
                    RulerLeft.Children.Add(tb);
                }
            }
        }
    }

    private void UpdateOverlay()
    {
        DrawRulers();
        if (_selected < 0 || EngineExport.LayerBounds(_layers[_selected], W, H) is not { } r)
        { SelBox.IsVisible = false; Handle.IsVisible = false; return; }
        var tl = FromCanvas(r.x, r.y);
        var br = FromCanvas(r.x + r.w, r.y + r.h);
        Avalonia.Controls.Canvas.SetLeft(SelBox, tl.X); Avalonia.Controls.Canvas.SetTop(SelBox, tl.Y);
        SelBox.Width = Math.Max(0, br.X - tl.X); SelBox.Height = Math.Max(0, br.Y - tl.Y);
        SelBox.IsVisible = true;
        Avalonia.Controls.Canvas.SetLeft(Handle, br.X - 5.5); Avalonia.Controls.Canvas.SetTop(Handle, br.Y - 5.5);
        Handle.IsVisible = true;
    }

    private void UpdateInspector()
    {
        Sync3DPanel();                    // painel 3D acompanha a seleção/tempo
        SyncCtxBar();                     // barra contextual (Canva) reflete o que está selecionado
        if (_selected < 0) { Inspector.Text = "(clica num objeto; arrasta p/ mover; pega no canto p/ escalar)"; return; }
        var l = _layers[_selected];
        Inspector.Text = $"{l.Name}\nx {(l.PosX?.Eval(0) ?? 0):0}   y {(l.PosY?.Eval(0) ?? 0):0}\n" +
                         $"escala {(l.Scale?.Eval(0) ?? 1):0.##}   rot {(l.Rotation?.Eval(0) ?? 0):0}°" +
                         (l.ThreeD is { } t3 ? $"\n[3D  prof {t3.Depth:0.##}  rough {t3.Rough:0.##}  metal {t3.Metal:0.##}]" : "") +
                         (l.ClipD != null ? "\n[PowerClip ativo]" : "");
    }

    // ---------------- layer helpers ----------------
    private uint NextColor() => Palette[_palIx++ % Palette.Length];

    private void AddLayer(string name, string d)
        => Mutate(() => { _layers.Add(new Layer($"{name}-{_nameSeq++}", MorphTrack.Static(d), NextColor())); _selected = _layers.Count - 1; });

    private string CanvasSpaceD(Layer l)
        => PathBoolean.TransformD(l.Shape.Keys[0].PathD,
            l.PosX?.Eval(0) ?? 0, l.PosY?.Eval(0) ?? 0, l.Rotation?.Eval(0) ?? 0, l.Scale?.Eval(0) ?? 1);

    // ---------------- toolbar: creation ----------------
    private void OnAddStar(object? s, RoutedEventArgs e) => AddLayer("estrela", Shapes.Star(150, 62, 5));
    private void OnAddCircle(object? s, RoutedEventArgs e) => AddLayer("circulo", Shapes.Circle(120));
    private void OnAddRect(object? s, RoutedEventArgs e) => AddLayer("retangulo", Shapes.Rect(150, 95));
    private void OnAddSquircle(object? s, RoutedEventArgs e) => AddLayer("squircle", Shapes.Superellipse(120, 120));

    private void OnAddText(object? s, RoutedEventArgs e)
    {
        var d = TextShape.TextPathD(TextInput.Text ?? "", 120);
        if (d != null) AddLayer("texto", d);
    }

    // ---------------- toolbar: boolean / powerclip ----------------
    private void Boolean(string op)
    {
        if (_selected < 1) return;
        var a = _layers[_selected];
        var b = _layers[_selected - 1];
        var d = PathBoolean.Op(CanvasSpaceD(a), CanvasSpaceD(b), op);
        if (d is null) return;
        Mutate(() =>
        {
            var result = new Layer($"{op}-{_nameSeq++}", MorphTrack.Static(d), a.FillArgb,
                FillArgb2: a.FillArgb2, FillRadial: a.FillRadial, Shadow: a.Shadow,
                SpecularStrength: a.SpecularStrength);
            int lo = _selected - 1;
            _layers.RemoveAt(_selected);
            _layers.RemoveAt(lo);
            _layers.Insert(lo, result);
            _selected = lo;
        });
    }

    private void OnSubtract(object? s, RoutedEventArgs e) => Boolean("subtract");
    private void OnUnion(object? s, RoutedEventArgs e) => Boolean("union");
    private void OnIntersect(object? s, RoutedEventArgs e) => Boolean("intersect");

    private void OnPowerClip(object? s, RoutedEventArgs e)
    {
        if (_selected < 1) return;
        var container = _layers[_selected - 1];
        Mutate(() => _layers[_selected] = _layers[_selected] with { ClipD = CanvasSpaceD(container) });
    }

    // ---------------- toolbar: style / transform ----------------
    private void OnGradient(object? s, RoutedEventArgs e)
    {
        if (_selected < 0) return;
        var l = _layers[_selected];
        Mutate(() => _layers[_selected] = (l.FillArgb2, l.FillRadial) switch
        {
            (null, _) => l with { FillArgb2 = 0xFFC13AF6, FillRadial = false },              // linear violeta
            ({ } f2, false) when f2 == 0xFFC13AF6 => l with { FillArgb = 0xFF2D7FF9, FillArgb2 = 0xFF16C7B9 }, // linear azul→teal
            (_, false) => l with { FillArgb = 0xFFF8D24A, FillArgb2 = 0xFFE88A00, FillRadial = true },          // radial ouro
            (_, true) => l with { FillArgb = Palette[0], FillArgb2 = null, FillRadial = false },                // volta a sólido
        });
    }

    private void Rotate(double deg)
    {
        if (_selected < 0) return;
        var l = _layers[_selected];
        Mutate(() => _layers[_selected] = l with { Rotation = Track.Const((l.Rotation?.Eval(0) ?? 0) + deg) });
    }
    private void OnRotL(object? s, RoutedEventArgs e) => Rotate(-15);
    private void OnRotR(object? s, RoutedEventArgs e) => Rotate(15);

    private void OnSnap(object? s, RoutedEventArgs e)
    {
        _snap = !_snap;
        ToolTip.SetTip(SnapBtn, "Snap à grelha: " + (_snap ? "ON" : "off"));
        SnapBtn.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(_snap ? "#6D5EF6" : "#5E5E5B"));
    }

    // ---------------- layers panel ----------------
    private void OnLayerPicked(object? s, SelectionChangedEventArgs e)
    {
        int ix = LayerList.SelectedIndex;
        if (ix < 0) return;
        int mapped = _layers.Count - 1 - ix;
        if (mapped != _selected && mapped >= 0 && mapped < _layers.Count)
        { _selected = mapped; UpdateInspector(); UpdateOverlay(); }
    }

    private void OnLayerUp(object? s, RoutedEventArgs e)
    {
        if (_selected < 0 || _selected >= _layers.Count - 1) return;
        Mutate(() => { (_layers[_selected], _layers[_selected + 1]) = (_layers[_selected + 1], _layers[_selected]); _selected++; });
    }

    private void OnLayerDown(object? s, RoutedEventArgs e)
    {
        if (_selected < 1) return;
        Mutate(() => { (_layers[_selected], _layers[_selected - 1]) = (_layers[_selected - 1], _layers[_selected]); _selected--; });
    }

    private void OnDuplicate(object? s, RoutedEventArgs e)
    {
        if (_selected < 0) return;
        var l = _layers[_selected];
        Mutate(() =>
        {
            _layers.Insert(_selected + 1, l with
            {
                Name = l.Name + "-copia",
                PosX = Track.Const((l.PosX?.Eval(0) ?? 0) + 24),
                PosY = Track.Const((l.PosY?.Eval(0) ?? 0) + 24),
            });
            _selected++;
        });
    }

    private void OnDelete(object? s, RoutedEventArgs e)
    {
        if (_selected < 0) return;
        Mutate(() => { _layers.RemoveAt(_selected); _selected = Math.Min(_selected, _layers.Count - 1); });
    }

    // ---------------- undo/redo ----------------
    private void OnUndo(object? s, RoutedEventArgs e)
    {
        if (_histIx <= 0) return;
        _histIx--;
        _layers = new List<Layer>(_hist[_histIx]);
        _selected = Math.Min(_selected, _layers.Count - 1);
        Refresh();
    }

    private void OnRedo(object? s, RoutedEventArgs e)
    {
        if (_histIx >= _hist.Count - 1) return;
        _histIx++;
        _layers = new List<Layer>(_hist[_histIx]);
        _selected = Math.Min(_selected, _layers.Count - 1);
        Refresh();
    }

    private bool _spaceDown;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        // §BETA — modais abertos: o teclado é DELES. Sem isto, "S" atrás do modal inseria uma
        // estrela na tela e o Espaço ligava a mão (as hotkeys de letra só olham para TextBox).
        if (TutorialPanel.IsVisible)
        {
            switch (e.Key)
            {
                case Key.Escape: CloseTutorial(); e.Handled = true; break;
                case Key.Left: OnTutorialBack(null, new RoutedEventArgs()); e.Handled = true; break;
                // Enter/Espaço só chegam aqui se NENHUM botão tiver foco (senão o próprio botão trata-os)
                case Key.Right: case Key.Enter: case Key.Space:
                    OnTutorialNext(null, new RoutedEventArgs()); e.Handled = true; break;
                case Key.Tab: break;                // NÃO tocar: é a navegação de foco entre os 3 botões
                default: e.Handled = true; break;   // o resto morre aqui (não passa para a tela)
            }
            return;
        }
        if (ComplaintPanel.IsVisible) return;   // o formulário trata as suas próprias teclas
        // O paywall tem exactamente o mesmo buraco e é anterior a isto: com ele aberto e o foco num
        // botão de plano, "S" ainda inseria uma estrela na tela por trás. Mesmo ficheiro, mesmo bug.
        if (PaymentPanel.IsVisible) return;

        if (e.Key == Key.Space && !(FocusManager?.GetFocusedElement() is TextBox))
        {
            _spaceDown = true;
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
            e.Handled = true; return;
        }
        if (_penMode && e.Key == Key.Enter) { CommitPen(false); e.Handled = true; return; }
        if (_penMode && e.Key == Key.Escape) { CancelPen(); e.Handled = true; return; }

        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (ctrl && e.Key == Key.V) { PasteClipboardImage(); e.Handled = true; return; }
        if (e.Key == Key.Delete) { OnDelete(null, new RoutedEventArgs()); e.Handled = true; return; }
        if (ctrl && e.Key == Key.Z) { OnUndo(null, new RoutedEventArgs()); e.Handled = true; return; }
        if (ctrl && e.Key == Key.Y) { OnRedo(null, new RoutedEventArgs()); e.Handled = true; return; }
        if (ctrl && e.Key == Key.D) { OnDuplicate(null, new RoutedEventArgs()); e.Handled = true; return; }

        // hotkeys de LETRA — só quando NÃO se está a escrever num TextBox
        if (!ctrl && !e.KeyModifiers.HasFlag(KeyModifiers.Alt) && FocusManager?.GetFocusedElement() is not TextBox)
        {
            var act = _hotkeys.TryGetValue(e.Key, out var name) ? name : null;
            if (act is not null) { RunHotkey(act); e.Handled = true; }
        }
    }

    // ---------------- hotkeys (configuráveis) ----------------
    private readonly Dictionary<Key, string> _hotkeys = new()
    {
        [Key.V] = "select", [Key.H] = "hand", [Key.P] = "pen", [Key.M] = "center",
        [Key.R] = "rect", [Key.C] = "circle", [Key.S] = "star", [Key.X] = "squircle",
        [Key.G] = "grid", [Key.F] = "fit", [Key.T] = "text",
    };
    private static readonly (string key, string action, string desc)[] HotkeyHelp =
    {
        ("V", "Selecionar", "ferramenta de seleção/mover"),
        ("H", "Mão", "arrastar a tela (ou segura Espaço)"),
        ("P", "Caneta", "desenhar caminhos"),
        ("M", "Centrar", "centra a camada selecionada"),
        ("R", "Retângulo", "inserir retângulo"),
        ("C", "Círculo", "inserir círculo"),
        ("S", "Estrela", "inserir estrela"),
        ("X", "Squircle", "inserir squircle"),
        ("T", "Texto", "inserir texto"),
        ("G", "Grelha", "cicla a grelha de construção"),
        ("F", "Ajustar", "ajustar a vista à janela"),
        ("Ctrl+V", "Colar", "colar imagem do clipboard"),
        ("Ctrl+D", "Duplicar", "duplicar a camada"),
        ("Ctrl+Z / Ctrl+Y", "Desfazer/Refazer", ""),
        ("Del", "Apagar", "apagar a camada selecionada"),
        ("Espaço", "Pan", "segura p/ arrastar a tela"),
    };

    private void RunHotkey(string action)
    {
        switch (action)
        {
            case "select": if (_penMode) OnPenToggle(null, new()); if (_handTool) OnHandTool(null, new()); break;
            case "hand": OnHandTool(null, new RoutedEventArgs()); break;
            case "pen": OnPenToggle(null, new RoutedEventArgs()); break;
            case "center": CenterSelected(); break;
            case "rect": OnAddRect(null, new RoutedEventArgs()); break;
            case "circle": OnAddCircle(null, new RoutedEventArgs()); break;
            case "star": OnAddStar(null, new RoutedEventArgs()); break;
            case "squircle": OnAddSquircle(null, new RoutedEventArgs()); break;
            case "text": TextInput.Focus(); break;
            case "grid": OnGridCycle(null, new RoutedEventArgs()); break;
            case "fit": OnFitView(null, new RoutedEventArgs()); break;
        }
    }

    private void CenterSelected()
    {
        if (_selected < 0) return;
        var l = _layers[_selected];
        Mutate(() => _layers[_selected] = l with { PosX = Track.Const(0), PosY = Track.Const(0) });
    }

    private void PopulateHotkeysList()
    {
        var items = new List<Control>();
        foreach (var (key, action, _) in HotkeyHelp)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var k = new Border
            {
                Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#F1F1EF")),
                CornerRadius = new Avalonia.CornerRadius(5), Padding = new Thickness(7, 2),
                Child = new TextBlock { Text = key, FontSize = 10.5, FontWeight = Avalonia.Media.FontWeight.Bold,
                                        Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#3A3A38")) },
            };
            DockPanel.SetDock(k, Dock.Left);
            row.Children.Add(k);
            row.Children.Add(new TextBlock { Text = action, FontSize = 11.5,
                Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#4A4A47")) });
            items.Add(row);
        }
        HotkeysList.ItemsSource = items;
    }

    // atalhos configuráveis via %APPDATA%\Klip\hotkeys.json  ("V":"select", ...)
    private void LoadHotkeyConfig()
    {
        try
        {
            var path = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Klip", "hotkeys.json");
            if (!System.IO.File.Exists(path)) return;
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(System.IO.File.ReadAllText(path));
            if (map is null) return;
            foreach (var (k, v) in map)
                if (Enum.TryParse<Key>(k, true, out var key)) _hotkeys[key] = v;
        }
        catch { /* usa defaults */ }
    }

    // ---------------- clipboard paste (Ctrl+V) ----------------
    private void PasteClipboardImage()
    {
        try
        {
            var png = ClipboardImage.TryGetPng();
            if (png is null) { AppendChat("·", "clipboard sem imagem (copia uma imagem/screenshot primeiro)"); return; }
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Klip");
            System.IO.Directory.CreateDirectory(dir);
            var path = System.IO.Path.Combine(dir, "paste_" + Environment.TickCount64 + ".png");
            System.IO.File.WriteAllBytes(path, png);
            ApiInsertImage(path);
            AppendChat("·", "imagem colada do clipboard");
        }
        catch (Exception ex) { AppendChat("✗", "colar: " + ex.Message); }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key == Key.Space)
        {
            _spaceDown = false;
            if (!_panning) Cursor = new Avalonia.Input.Cursor(_handTool ? Avalonia.Input.StandardCursorType.Hand : Avalonia.Input.StandardCursorType.Arrow);
        }
    }

    // ---------------- save / load (.klip = gzip json) ----------------
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };
    private static readonly FilePickerFileType KlipType = new("KLIP design") { Patterns = new[] { "*.klip" } };

    private async void OnSave(object? s, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        { SuggestedFileName = "design.klip", FileTypeChoices = new[] { KlipType } });
        if (file is null) return;
        await using var stream = await file.OpenWriteAsync();
        await using var gz = new GZipStream(stream, CompressionLevel.Optimal);
        await JsonSerializer.SerializeAsync(gz, _layers, JsonOpts);
    }

    private async void OnOpen(object? s, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        { AllowMultiple = false, FileTypeFilter = new[] { KlipType } });
        if (files.Count == 0) return;
        try
        {
            await using var stream = await files[0].OpenReadAsync();
            await using var gz = new GZipStream(stream, CompressionMode.Decompress);
            var layers = await JsonSerializer.DeserializeAsync<List<Layer>>(gz, JsonOpts);
            if (layers is null) return;
            _layers = layers;
            _selected = -1;
            PushHistory();
            Refresh();
        }
        catch (Exception ex) { Inspector.Text = "Falha a abrir: " + ex.Message; }
    }

    // ================= AI command-bus API (called on UI thread via ActionRegistry) =================
    private static uint ParseColor(string? hex, uint fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        var h = hex.TrimStart('#');
        if (h.Length == 6 && uint.TryParse(h, System.Globalization.NumberStyles.HexNumber, null, out var rgb))
            return 0xFF000000 | rgb;
        if (h.Length == 8 && uint.TryParse(h, System.Globalization.NumberStyles.HexNumber, null, out var argb))
            return argb;   // #AARRGGBB (alpha próprio — ex. 00FFFFFF = invisível)
        return fallback;
    }

    private int FindLayer(string id) => PropRegistry.Find(_layers, id);   // Id estável primeiro, Name como fallback

    /// <summary>Atribui um Id estável a camadas que ainda não têm (aponta-e-instrui, endereçamento).</summary>
    private void EnsureIds()
    {
        for (int i = 0; i < _layers.Count; i++)
            if (string.IsNullOrEmpty(_layers[i].Id))
                _layers[i] = _layers[i] with { Id = Ids.Next() };
    }

    private Layer Sel(string id)
    {
        int ix = FindLayer(id);
        if (ix < 0) throw new InvalidOperationException($"camada '{id}' não existe");
        return _layers[ix];
    }

    public object ApiState() => new
    {
        canvas = new { w = W, h = H },
        layers = _layers.Count,
        selected = _selected >= 0 && _selected < _layers.Count ? _layers[_selected].Name : null,
        can_undo = _histIx > 0,
        can_redo = _histIx < _hist.Count - 1,
        motion = new { duration = _motionDur, fps = _motionFps },
        camera = _camTracks.Count > 0,
        engine3d = Klip.Engine.ThreeD.Hybrid3D.GpuError ?? "ok",
    };

    public object ApiListItems() => _layers.Select(l => new
    {
        id = l.Name,
        layer_id = l.Key,          // Id estável endereçável (aponta-e-instrui / IA)
        x = l.PosX?.Eval(0) ?? 0,
        y = l.PosY?.Eval(0) ?? 0,
        scale = l.Scale?.Eval(0) ?? 1.0,
        rotation = l.Rotation?.Eval(0) ?? 0,
        fill = "#" + (l.FillArgb & 0xFFFFFF).ToString("X6"),
        gradient = l.FillArgb2 is not null,
        clipped = l.ClipD is not null,
    }).ToArray();

    public object ApiInsertShape(string shape, double size, string? fill, double x, double y)
    {
        string d = shape switch
        {
            "star" => Shapes.Star(size, size * 0.41, 5),
            "rect" => Shapes.Rect(size, size * 0.66),
            "squircle" => Shapes.Superellipse(size, size),
            _ => Shapes.Circle(size),
        };
        var name = $"{shape}-{_nameSeq++}";
        Mutate(() =>
        {
            _layers.Add(new Layer(name, MorphTrack.Static(d), ParseColor(fill, NextColor()),
                PosX: Track.Const(x), PosY: Track.Const(y)));
            _selected = _layers.Count - 1;
        });
        return new { id = name };
    }

    public object ApiInsertText(string text, double size, string? fill, double x, double y, string? family = null)
    {
        var tf = FontRegistry.Shared.Resolve(string.IsNullOrWhiteSpace(family) ? _defaultFamily : family, bold: true);
        var d = TextShape.TextPathD(text, (float)size, tf)
            ?? throw new InvalidOperationException("texto vazio/inválido");
        var name = $"texto-{_nameSeq++}";
        _textMeta[name] = (text, (float)size);
        Mutate(() =>
        {
            _layers.Add(new Layer(name, MorphTrack.Static(d), ParseColor(fill, 0xFF232326),
                PosX: Track.Const(x), PosY: Track.Const(y)));
            _selected = _layers.Count - 1;
        });
        return new { id = name };
    }

    /// <summary>Carrega/baixa uma fonte (nome→Google Fonts, caminho .ttf, ou URL) e regista-a p/ uso.</summary>
    public object ApiLoadFont(string spec)
    {
        var r = Task.Run(() => FontRegistry.Shared.LoadAsync(spec)).GetAwaiter().GetResult();
        return new { family = r.Family, source = r.Source, cached = r.CachePath, from_cache = r.FromCache };
    }

    /// <summary>Muda a fonte de uma camada de texto (re-bake) OU, sem id, a fonte por omissão.</summary>
    public object ApiSetFont(string? id, string family)
    {
        if (string.IsNullOrWhiteSpace(id)) { _defaultFamily = family; return new { ok = true, default_family = family }; }
        if (!_textMeta.TryGetValue(id!, out var meta))
            throw new InvalidOperationException($"camada '{id}' não é texto criado nesta sessão (sem meta p/ re-bake)");
        var tf = FontRegistry.Shared.Resolve(family, bold: true);
        var d = TextShape.TextPathD(meta.text, meta.size, tf) ?? throw new InvalidOperationException("re-bake falhou");
        int ix = FindLayer(id!);
        if (ix < 0) throw new InvalidOperationException($"camada '{id}' não existe");
        Mutate(() => _layers[ix] = _layers[ix] with { Shape = MorphTrack.Static(d) });
        return new { ok = true, id, family };
    }

    // ===== Fase 3: z-index + duplicar-com-keyframes =====
    /// <summary>Reordena a camada (z-index). A ordem da lista = ordem de desenho (fim = topo).</summary>
    public object ApiReorder(string id, string mode)
    {
        int ix = FindLayer(id);
        if (ix < 0) throw new InvalidOperationException($"camada '{id}' não existe");
        Mutate(() =>
        {
            var l = _layers[ix];
            _layers.RemoveAt(ix);
            int ni = mode.ToLowerInvariant() switch
            {
                "front" or "bring_to_front" or "top" => _layers.Count,       // fim da lista = topo do stack
                "back" or "send_to_back" or "bottom" => 0,
                "forward" or "up" => System.Math.Min(_layers.Count, ix + 1),
                "backward" or "down" => System.Math.Max(0, ix - 1),
                _ => throw new InvalidOperationException("mode: front|back|forward|backward"),
            };
            _layers.Insert(ni, l);
            _selected = ni;
        });
        return new { ok = true, id, mode };
    }

    /// <summary>Duplica uma camada COM todos os keyframes/animação (record with → Tracks partilhadas imutáveis).</summary>
    public object ApiDuplicate(string id)
    {
        int ix = FindLayer(id);
        if (ix < 0) throw new InvalidOperationException($"camada '{id}' não existe");
        var src = _layers[ix];
        var name = $"{src.Name}-copy-{_nameSeq++}";
        var dup = src with { Name = name, Id = Ids.Next() };
        if (_textMeta.TryGetValue(src.Name, out var meta)) _textMeta[name] = meta;   // clone de texto herda a meta
        Mutate(() => { _layers.Insert(ix + 1, dup); _selected = ix + 1; });
        return new { id = name };
    }

    // ===== Fase 4: stagger — a mesma animação a N camadas com desfasamento no tempo =====
    /// <summary>Aplica keyframes from→to a cada camada, cada uma atrasada offset*i. Funciona p/ qualquer
    /// propriedade (incl. cor via #hex). É o clássico stagger — 80% do trabalho manual num verbo.</summary>
    public object ApiStagger(string[] ids, string path, string from, string to, double duration, double offset, string ease = "linear")
    {
        var e = ParseEase(ease);
        var pvFrom = ParsePropValue(path, from);
        var pvTo = ParsePropValue(path, to);
        int applied = 0;
        Mutate(() =>
        {
            for (int i = 0; i < ids.Length; i++)
            {
                int ix = FindLayer(ids[i]);
                if (ix < 0) continue;
                double delay = i * offset;
                var l = PropRegistry.AddKeyframe(_layers[ix], path, delay, pvFrom, e);
                l = PropRegistry.AddKeyframe(l, path, delay + duration, pvTo, e);
                _layers[ix] = l;
                applied++;
            }
        });
        return new { ok = true, applied, path };
    }

    public object ApiInsertPath(string d, string? fill)
    {
        if (SkiaSharp.SKPath.ParseSvgPathData(d) is null)
            throw new InvalidOperationException("path 'd' inválido");
        var name = $"path-{_nameSeq++}";
        Mutate(() =>
        {
            _layers.Add(new Layer(name, MorphTrack.Static(d), ParseColor(fill, NextColor())));
            _selected = _layers.Count - 1;
        });
        return new { id = name };
    }

    public object ApiSetTransform(string id, double? x, double? y, double? scale, double? rotation)
    {
        int ix = FindLayer(id);
        var l = Sel(id);
        Mutate(() => _layers[ix] = l with
        {
            PosX = x is { } vx ? Track.Const(vx) : l.PosX,
            PosY = y is { } vy ? Track.Const(vy) : l.PosY,
            Scale = scale is { } vs ? Track.Const(vs) : l.Scale,
            Rotation = rotation is { } vr ? Track.Const(vr) : l.Rotation,
        });
        return new { ok = true };
    }

    public object ApiSetFill(string id, string fill, string? fill2, bool radial,
                             double? angle = null, double? mid = null, double? spread = null)
    {
        int ix = FindLayer(id);
        var l = Sel(id);
        Mutate(() => _layers[ix] = l with
        {
            FillArgb = ParseColor(fill, l.FillArgb),
            FillArgb2 = string.IsNullOrWhiteSpace(fill2) ? null : ParseColor(fill2, l.FillArgb),
            FillRadial = radial,
            GradAngle = angle ?? l.GradAngle,
            GradMid = mid ?? l.GradMid,
            GradSpread = spread ?? l.GradSpread,
        });
        return new { ok = true };
    }

    public object ApiBoolean(string aId, string bId, string op)
    {
        int ia = FindLayer(aId), ib = FindLayer(bId);
        var la = Sel(aId); var lb = Sel(bId);
        var d = PathBoolean.Op(CanvasSpaceD(la), CanvasSpaceD(lb), op)
            ?? throw new InvalidOperationException("resultado vazio");
        var name = $"{op}-{_nameSeq++}";
        Mutate(() =>
        {
            var result = new Layer(name, MorphTrack.Static(d), la.FillArgb,
                FillArgb2: la.FillArgb2, FillRadial: la.FillRadial, Shadow: la.Shadow,
                SpecularStrength: la.SpecularStrength);
            int lo = Math.Min(ia, ib), hi = Math.Max(ia, ib);
            _layers.RemoveAt(hi);
            _layers.RemoveAt(lo);
            _layers.Insert(lo, result);
            _selected = lo;
        });
        return new { id = name };
    }

    public object ApiPowerClip(string contentId, string containerId)
    {
        int ic = FindLayer(contentId);
        var content = Sel(contentId); var container = Sel(containerId);
        Mutate(() => _layers[ic] = content with { ClipD = CanvasSpaceD(container) });
        return new { ok = true };
    }

    public object ApiRemove(string id)
    {
        int ix = FindLayer(id); Sel(id);
        Mutate(() => { _layers.RemoveAt(ix); _selected = Math.Min(_selected, _layers.Count - 1); });
        return new { ok = true };
    }

    public object ApiClear()
    {
        Mutate(() => { _layers.Clear(); _selected = -1; });
        return new { ok = true };
    }

    public object ApiUndo() { OnUndo(null, new RoutedEventArgs()); return new { ok = true }; }
    public object ApiRedo() { OnRedo(null, new RoutedEventArgs()); return new { ok = true }; }

    public object ApiExport(string path) => ApiExport(path, 0, 1.0);

    /// <summary>Export PNG resolvendo resolution (1080|2k|4k|altura) para scale vetorial.</summary>
    public object ApiExportImage(string path, string? resolution, double t)
        => ApiExport(path, t, ResolveScale(resolution, H));

    /// <summary>Export PNG a um tempo t e resolução (scale). scale=null/0 → auto-4K se o canvas for pequeno.</summary>
    public object ApiExport(string path, double t, double scale)
    {
        var comp = BuildComp();
        File.WriteAllBytes(path, EngineExport.RenderPng(comp, t, scale <= 0 ? 1.0 : scale));
        return new { path, width = (int)(comp.Width * (scale <= 0 ? 1 : scale)), height = (int)(comp.Height * (scale <= 0 ? 1 : scale)) };
    }

    // ---- CMYK (editor completo de print) ----
    public object ApiSetCmyk(string id, double c, double m, double y, double k)
    {
        int ix = FindLayer(id);
        var l = Sel(id);
        uint argb = CmykExport.CmykToArgb(c, m, y, k);
        Mutate(() => _layers[ix] = l with { FillArgb = argb, FillArgb2 = null });
        return new { rgb = "#" + (argb & 0xFFFFFF).ToString("X6") };
    }

    public object ApiExportCmyk(string path)
    {
        var profile = CmykExport.ExportTiff(BuildComp(), 0, path);
        return new { path, profile = profile is null ? "builtin" : Path.GetFileName(profile) };
    }

    // ---- MOTION (timeline embutida) ----
    public object ApiSetMotion(double duration, double fps)
    {
        _motionDur = Math.Clamp(duration, 0.2, 120);
        _motionFps = Math.Clamp(fps, 1, 60);
        return new { duration = _motionDur, fps = _motionFps };
    }

    public object ApiAddKeyframe(string id, string prop, double time, double value, string ease, string? bez = null)
    {
        int ix = FindLayer(id);
        var l = Sel(id);
        var e = ParseEase(ease);
        var bezArr = ParseBez(bez);
        Track WithKey(Track? tr, double defVal)
        {
            var keys = (tr?.Keys ?? new List<Keyframe> { }).Where(kf => Math.Abs(kf.Time - time) > 1e-6).ToList();
            keys.Add(new Keyframe(time, value, e, bezArr));
            keys.Sort((a, b) => a.Time.CompareTo(b.Time));
            // semântica AE: constante até ao 1º keyframe (o Track.Eval já segura o valor do 1º key antes dele)
            return new Track(keys);
        }
        Mutate(() => _layers[ix] = prop switch
        {
            "x" => l with { PosX = WithKey(l.PosX, l.PosX?.Eval(0) ?? 0) },
            "y" => l with { PosY = WithKey(l.PosY, l.PosY?.Eval(0) ?? 0) },
            "scale" => l with { Scale = WithKey(l.Scale, l.Scale?.Eval(0) ?? 1) },
            "rotation" => l with { Rotation = WithKey(l.Rotation, l.Rotation?.Eval(0) ?? 0) },
            "opacity" => l with { Opacity = WithKey(l.Opacity, l.Opacity?.Eval(0) ?? 1) },
            "blur" => l with { BlurRadius = WithKey(l.BlurRadius, l.BlurRadius?.Eval(0) ?? 0) },
            "trim_start" => l with { TrimStart = WithKey(l.TrimStart, 0) },
            "trim_end" => l with { TrimEnd = WithKey(l.TrimEnd, 1) },
            _ => throw new InvalidOperationException("prop desconhecida: " + prop),
        });
        return new { ok = true, prop, time, value };
    }

    // ===== Fase 1: sistema de propriedades UNIFORME — endereça QUALQUER prop (incl. COR) por path =====
    private static PropValue ParsePropValue(string path, string value)
    {
        if (!PropRegistry.TryGet(path, out var d))
            throw new InvalidOperationException("propriedade desconhecida: " + path);
        return d.Kind == ChannelKind.Color
            ? PropValue.Of(ParseColor(value, 0xFF000000))
            : PropValue.Of(double.Parse(value, System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Keyframe GENÉRICO em qualquer propriedade (escalar OU cor). value = número ou "#RRGGBB".</summary>
    public object ApiSetKeyframe(string id, string path, double time, string value, string ease = "linear", string? bez = null)
    {
        int ix = FindLayer(id); var l = Sel(id);
        var pv = ParsePropValue(path, value);
        Mutate(() => _layers[ix] = PropRegistry.AddKeyframe(l, path, time, pv, ParseEase(ease), ParseBez(bez)));
        return new { ok = true, id, path, time };
    }

    /// <summary>Valor ESTÁTICO de qualquer propriedade (colapsa o canal). value = número ou "#RRGGBB".</summary>
    public object ApiSetProp(string id, string path, string value)
    {
        int ix = FindLayer(id); var l = Sel(id);
        var pv = ParsePropValue(path, value);
        Mutate(() => _layers[ix] = PropRegistry.SetStatic(l, path, pv));
        return new { ok = true, id, path };
    }

    /// <summary>Lê o valor de qualquer propriedade no tempo t (escalar ou cor).</summary>
    public object ApiGetProp(string id, string path, double t = 0)
    {
        var v = PropRegistry.GetValue(Sel(id), path, t);
        return v.Kind == ChannelKind.Color
            ? new { kind = "color", color = "#" + (v.Argb & 0xFFFFFF).ToString("X6") }
            : (object)new { kind = "scalar", value = v.Scalar };
    }

    public object ApiRemoveKeyframe(string id, string path, double time)
    {
        int ix = FindLayer(id); var l = Sel(id);
        Mutate(() => _layers[ix] = PropRegistry.RemoveKeyframe(l, path, time));
        return new { ok = true };
    }

    /// <summary>Lista todas as propriedades animáveis endereçáveis da camada (descoberta p/ a IA).</summary>
    public object ApiListProps(string id)
        => PropRegistry.Describe(Sel(id))
            .Select(p => new { path = p.path, kind = p.kind.ToString().ToLowerInvariant(), animated = p.animated })
            .ToArray();

    /// <summary>Ponto-âncora (pivô de rotação/escala) em coords locais da forma.</summary>
    public object ApiSetAnchor(string id, double x, double y)
    {
        int ix = FindLayer(id);
        var l = Sel(id);
        Mutate(() => _layers[ix] = l with { AnchorX = x, AnchorY = y });
        return new { ok = true, anchor = new[] { x, y } };
    }

    /// <summary>PARENTING: liga a camada a uma mãe (transform relativo). parent=null/"" desliga.</summary>
    public object ApiSetParent(string id, string? parent)
    {
        int ix = FindLayer(id);
        var l = Sel(id);
        if (!string.IsNullOrEmpty(parent))
        {
            if (parent == id) throw new InvalidOperationException("uma camada não pode ser mãe de si própria");
            if (FindLayer(parent) < 0) throw new InvalidOperationException($"camada-mãe '{parent}' não existe");
        }
        Mutate(() => _layers[ix] = l with { Parent = string.IsNullOrEmpty(parent) ? null : parent });
        return new { ok = true, id, parent };
    }

    /// <summary>NULL object (controlador): camada só-transform, não desenha. Filhos parenteiam a ele.</summary>
    public object ApiInsertNull(double x, double y)
    {
        var name = $"null-{_nameSeq++}";
        Mutate(() =>
        {
            _layers.Add(new Layer(name, MorphTrack.Static(Shapes.Circle(1)), 0x00FFFFFF,
                PosX: Track.Const(x), PosY: Track.Const(y), Controller: true));
            _selected = _layers.Count - 1;
        });
        return new { id = name };
    }

    /// <summary>Expressions engine: aplica spring (bounce/overshoot) ou wiggle a uma propriedade.
    /// spring: p1=freq(bounciness ~12-30), p2=decay(settle ~5-12). wiggle: p1=freq(Hz), p2=amp.</summary>
    public object ApiSetExpression(string id, string prop, string kind, double? p1, double? p2, string? code = null)
    {
        int ix = FindLayer(id);
        var l = Sel(id);
        var k = kind.ToLowerInvariant() switch
        {
            "spring" or "bounce" => ExprKind.Spring,
            "wiggle" or "noise" => ExprKind.Wiggle,
            "code" or "expr" or "js" or "expression" => ExprKind.Code,
            "none" or "off" => ExprKind.None,
            _ => throw new InvalidOperationException("expressão desconhecida: " + kind),
        };
        if (k == ExprKind.Code && string.IsNullOrWhiteSpace(code))
            throw new InvalidOperationException("kind=code precisa do parâmetro 'code' (JavaScript estilo AE)");
        double d1 = p1 ?? (k == ExprKind.Spring ? 18 : 2);
        double d2 = p2 ?? (k == ExprKind.Spring ? 8 : (prop == "rotation" ? 4 : 12));
        TrackExpr? expr = k switch
        {
            ExprKind.None => null,
            ExprKind.Code => new TrackExpr(ExprKind.Code, 0, 0, code),
            _ => new TrackExpr(k, d1, d2),
        };

        Track WithExpr(Track? tr, double defVal)
            => (tr ?? Track.Const(defVal)) with { Expr = expr };

        Mutate(() => _layers[ix] = prop switch
        {
            "x" => l with { PosX = WithExpr(l.PosX, 0) },
            "y" => l with { PosY = WithExpr(l.PosY, 0) },
            "scale" => l with { Scale = WithExpr(l.Scale, 1) },
            "scale_x" => l with { ScaleX = WithExpr(l.ScaleX, 1) },
            "scale_y" => l with { ScaleY = WithExpr(l.ScaleY, 1) },
            "rotation" => l with { Rotation = WithExpr(l.Rotation, 0) },
            "opacity" => l with { Opacity = WithExpr(l.Opacity, 1) },
            "trim_end" => l with { TrimEnd = WithExpr(l.TrimEnd, 1) },
            "trim_start" => l with { TrimStart = WithExpr(l.TrimStart, 0) },
            _ => throw new InvalidOperationException("prop desconhecida: " + prop),
        });
        return new { ok = true, prop, expr = k.ToString().ToLowerInvariant(), freq = d1, decay_amp = d2, code };
    }

    // ---- 3D REAL: câmara animável + extrude por camada ----
    private readonly Dictionary<string, Track> _camTracks = new();

    private CameraRig? BuildCamera() => _camTracks.Count == 0 ? null : new CameraRig(
        _camTracks.GetValueOrDefault("x"), _camTracks.GetValueOrDefault("y"), _camTracks.GetValueOrDefault("z"),
        _camTracks.GetValueOrDefault("tx"), _camTracks.GetValueOrDefault("ty"), _camTracks.GetValueOrDefault("tz"),
        _camTracks.GetValueOrDefault("fov"));

    public object ApiSetCamera(double? x, double? y, double? z, double? tx, double? ty, double? tz, double? fov)
    {
        void Set(string k, double? v) { if (v is { } d) _camTracks[k] = Track.Const(d); }
        Set("x", x); Set("y", y); Set("z", z); Set("tx", tx); Set("ty", ty); Set("tz", tz); Set("fov", fov);
        Refresh();
        return new { ok = true, tracks = _camTracks.Keys };
    }

    public object ApiCameraKeyframe(string prop, double time, double value, string ease, string? bez)
    {
        if (!new[] { "x", "y", "z", "tx", "ty", "tz", "fov" }.Contains(prop))
            throw new InvalidOperationException("prop de câmara desconhecida: " + prop);
        var e = ParseEase(ease);
        var bezArr = ParseBez(bez);
        var existing = _camTracks.GetValueOrDefault(prop);
        var keys = (existing?.Keys ?? new List<Keyframe>()).Where(kf => Math.Abs(kf.Time - time) > 1e-6).ToList();
        keys.Add(new Keyframe(time, value, e, bezArr));
        keys.Sort((a, b) => a.Time.CompareTo(b.Time));
        _camTracks[prop] = new Track(keys);
        Refresh();
        return new { ok = true, prop, keys = keys.Count };
    }

    public object ApiSet3D(string id, double depth, double bevel)
    {
        int ix = FindLayer(id);
        var l = Sel(id);
        // preserva material/texturas ao mexer só em profundidade/bevel
        Mutate(() => _layers[ix] = l with
        {
            ThreeD = depth <= 0 ? null : (l.ThreeD ?? new Extrude3D()) with { Depth = depth, Bevel = bevel }
        });
        return new { ok = true, threeD = depth > 0 };
    }

    /// <summary>Material PBR da camada 3D: rough 0.04(espelho)–1(mate), metal 0(plástico)–1(metal).</summary>
    public object ApiSetMaterial(string id, double? rough, double? metal)
    {
        int ix = FindLayer(id);
        var l = Sel(id);
        var t = l.ThreeD ?? new Extrude3D();
        double r = Math.Clamp(rough ?? t.Rough, 0.04, 1.0);
        double m = Math.Clamp(metal ?? t.Metal, 0.0, 1.0);
        Mutate(() => _layers[ix] = l with { ThreeD = t with { Rough = r, Metal = m } });
        return new { ok = true, rough = r, metal = m };
    }

    /// <summary>Textura na FACE do produto (ex. arte de cartão): imagem frente/verso + cor da borda (núcleo de papel).</summary>
    public object ApiSetFaceTexture(string id, string? front, string? back, string? edge)
    {
        int ix = FindLayer(id);
        var l = Sel(id);
        var t = l.ThreeD ?? new Extrude3D();
        if (front is { Length: > 0 } && !System.IO.File.Exists(front))
            throw new InvalidOperationException("ficheiro da textura frontal não existe: " + front);
        if (back is { Length: > 0 } && !System.IO.File.Exists(back))
            throw new InvalidOperationException("ficheiro da textura do verso não existe: " + back);
        var nt = t with
        {
            FrontTex = string.IsNullOrWhiteSpace(front) ? t.FrontTex : front,
            BackTex = string.IsNullOrWhiteSpace(back) ? t.BackTex : back,
            EdgeArgb = string.IsNullOrWhiteSpace(edge) ? t.EdgeArgb : ParseColor(edge!, 0xFFEDEDED),
        };
        Mutate(() => _layers[ix] = l with { ThreeD = nt });
        return new { ok = true, front = nt.FrontTex, back = nt.BackTex };
    }

    public object ApiSetStroke(string id, string color, double width)
    {
        int ix = FindLayer(id);
        var l = Sel(id);
        Mutate(() => _layers[ix] = width <= 0
            ? l with { StrokeArgb = null, StrokeWidth = 0 }
            : l with { StrokeArgb = ParseColor(color, 0xFF232326), StrokeWidth = width });
        return new { ok = true };
    }

    private static Easing ParseEase(string ease) => ease switch
    {
        "hold" => Easing.Hold, "in" => Easing.EaseIn, "out" => Easing.EaseOut,
        "inout" => Easing.EaseInOut, "outback" => Easing.EaseOutBack, _ => Easing.Linear,
    };

    private static double[]? ParseBez(string? bez)
    {
        if (string.IsNullOrWhiteSpace(bez)) return null;
        var parts = bez.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 4) return null;
        var arr = new double[4];
        for (int i = 0; i < 4; i++)
            if (!double.TryParse(parts[i], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out arr[i])) return null;
        return arr;
    }

    public object ApiExportAnimation(string path) => ApiExportAnimation(path, null);

    /// <summary>Exporta MP4. resolution: "1080"|"4k"|"2k"|número = altura alvo; escala vetorial SEM perda.</summary>
    public object ApiExportAnimation(string path, string? resolution)
    {
        var comp = BuildComp();
        double scale = ResolveScale(resolution, comp.Height);
        int outW = (int)Math.Round(comp.Width * scale), outH = (int)Math.Round(comp.Height * scale);
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try { Mp4Exporter.Export(comp, path, scale: scale); }
            catch (Exception ex)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => AppendChat("✗", "export animação: " + ex.Message));
                return;
            }
            Avalonia.Threading.Dispatcher.UIThread.Post(() => AppendChat("·", $"animação {outW}×{outH} exportada: " + path));
        });
        return new { started = true, path, duration = _motionDur, fps = _motionFps, width = outW, height = outH };
    }

    private static double ResolveScale(string? resolution, int compHeight)
    {
        if (string.IsNullOrWhiteSpace(resolution)) return 1.0;
        int targetH = resolution.Trim().ToLowerInvariant() switch
        {
            "4k" or "2160" or "uhd" => 2160,
            "2k" or "1440" or "qhd" => 1440,
            "1080" or "fhd" or "hd" => 1080,
            "720" => 720,
            var s when int.TryParse(s, out var n) && n > 0 => n,
            _ => compHeight,
        };
        return Math.Clamp((double)targetH / Math.Max(1, compHeight), 0.25, 8.0);
    }
}
