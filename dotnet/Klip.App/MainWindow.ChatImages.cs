using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using SkiaSharp;

namespace Klip.App;

/// <summary>
/// IMAGENS NO CHAT — separadas das da tela.
///
/// A distinção é o ponto todo: uma referência que se mostra à IA ("faz parecido com isto") não tem
/// nada que ir parar ao documento. Antes, largar um ficheiro na janela punha-o SEMPRE na tela; não
/// havia maneira de dizer "olha para isto" sem sujar o trabalho.
///
/// Agora há quatro caminhos, e o destino é sempre o sítio onde se larga/cola:
///   · colar ou largar SOBRE O CHAT   → anexo da mensagem (a IA vê, a tela não muda)
///   · colar ou largar sobre a TELA   → camada, como sempre
///   · arrastar uma camada → chat     → a camada é renderizada e vai como anexo
///   · arrastar um anexo → tela       → entra como camada
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>Caminhos das imagens anexadas à PRÓXIMA mensagem. Esvazia-se ao enviar.</summary>
    private readonly List<string> _chatAnexos = new();

    private StackPanel? _anexoBar;

    private static string AnexoDir
    {
        get
        {
            var d = Path.Combine(Path.GetTempPath(), "klip_chat");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    /// <summary>Liga o colar/largar/arrastar do chat. Chamado uma vez no arranque.</summary>
    private void WireChatImages()
    {
        if (ChatHost is null) return;

        // O chat aceita ficheiros largados — e trata-os como ANEXO, não como camada.
        DragDrop.SetAllowDrop(ChatHost, true);
        ChatHost.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            bool ok = e.DataTransfer?.Contains(DataFormat.File) == true || ChatArrastoTemImagem(e);
            e.DragEffects = ok ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = ok;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        ChatHost.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            int n = 0;
            // camada arrastada de dentro do KLIP
            if (e.DataTransfer?.TryGetText() is { Length: > 0 } t && t.StartsWith("klip-layer:"))
            {
                var id = t["klip-layer:".Length..];
                if (AnexarCamada(id)) n++;
            }
            var fics = e.DataTransfer?.TryGetFiles();
            if (fics is not null)
                foreach (var f in fics)
                    if (f.TryGetLocalPath() is { } p && EhImagem(p) && Anexar(p)) n++;

            if (n > 0) { e.Handled = true; SyncAnexos(); }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // Colar: o destino depende de quem tem o foco. Com o cursor no chat, a imagem é anexo.
        AddHandler(KeyDownEvent, async (_, e) =>
        {
            if (e.Key != Key.V || !e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
            var clip = Clipboard;
            if (clip is null) return;

            // O DESTINO É ONDE ESTÁ O FOCO: com o cursor na caixa de escrita (ou na aba Chat) a imagem
            // é referência para a IA; em qualquer outro sítio é uma camada nova. É o que separa
            // "olha para isto" de "põe isto no meu trabalho".
            bool paraChat = ChatInput?.IsFocused == true || _tab == "chat";
            try
            {
                var dados = await clip.TryGetDataAsync();
                if (dados is null) return;

                var files = await dados.TryGetFilesAsync();
                if (files is not null)
                {
                    int n = 0;
                    foreach (var f in files)
                        if (f.TryGetLocalPath() is { } p && EhImagem(p))
                        { if (paraChat) { if (Anexar(p)) n++; } else { ImportFile(p); n++; } }
                    if (n > 0) { if (paraChat) SyncAnexos(); e.Handled = true; return; }
                }

                // imagem em bruto na área de transferência (print screen, copiar do browser):
                // não vem como ficheiro, por isso grava-se antes de a poder usar
                var bmp = await dados.TryGetBitmapAsync();
                if (bmp is not null)
                {
                    var p = Path.Combine(AnexoDir, "colado_" + Guid.NewGuid().ToString("N")[..8] + ".png");
                    using (var fs = File.Create(p)) bmp.Save(fs);
                    if (paraChat) { if (Anexar(p)) SyncAnexos(); } else ImportFile(p);
                    e.Handled = true;
                }
            }
            catch (Exception ex) { AppendChat("✗", "colar falhou: " + ex.Message); }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    /// <summary>
    /// Arrastar da LISTA DE CAMADAS para o chat. Fica na lista e não no canvas de propósito: no canvas
    /// o arrasto já significa MOVER a camada, e roubar esse gesto tornaria o editor imprevisível.
    /// </summary>
    private void WireLayerDrag()
    {
        if (LayerList is null) return;

        // O arrasto NÃO pode começar no press: isso roubaria o clique que serve para SELECIONAR uma
        // camada. Guarda-se o press e só se inicia o arrasto depois do ponteiro andar uns píxeis —
        // é o limiar que distingue "cliquei" de "estou a arrastar".
        PointerPressedEventArgs? press = null;
        Point p0 = default;

        LayerList.AddHandler(PointerPressedEvent, (_, ev) =>
        { press = ev; p0 = ev.GetPosition(LayerList); }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        LayerList.AddHandler(PointerReleasedEvent, (_, _) => press = null,
            Avalonia.Interactivity.RoutingStrategies.Tunnel);

        LayerList.AddHandler(PointerMovedEvent, async (_, ev) =>
        {
            if (press is null) return;
            var p = ev.GetPosition(LayerList);
            if (Math.Abs(p.X - p0.X) + Math.Abs(p.Y - p0.Y) < 8) return;
            var arg = press; press = null;
            if (_selected < 0 || _selected >= _layers.Count) return;
            var dt = new DataTransfer();
            dt.Add(DataTransferItem.Create(DataFormat.Text, "klip-layer:" + _layers[_selected].Key));
            try { await DragDrop.DoDragDropAsync(arg, dt, DragDropEffects.Copy); } catch { }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private static bool ChatArrastoTemImagem(DragEventArgs e)
        => e.DataTransfer?.TryGetText() is { Length: > 0 } t && t.StartsWith("klip-layer:");

    private static bool EhImagem(string p)
        => Path.GetExtension(p).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif";

    /// <summary>Junta uma imagem aos anexos da próxima mensagem.</summary>
    private bool Anexar(string path)
    {
        if (!File.Exists(path)) return false;
        if (_chatAnexos.Count >= 8) { AppendChat("✗", "máximo de 8 imagens por mensagem"); return false; }
        if (_chatAnexos.Contains(path, StringComparer.OrdinalIgnoreCase)) return false;
        _chatAnexos.Add(path);
        return true;
    }

    /// <summary>Renderiza uma camada para PNG e anexa-a — é como se manda um elemento da tela à IA.</summary>
    private bool AnexarCamada(string id)
    {
        try
        {
            int ix = FindLayer(id);
            if (ix < 0) return false;
            var p = Path.Combine(AnexoDir, "camada_" + Guid.NewGuid().ToString("N")[..8] + ".png");
            ApiExportImage(p, null, _previewT);
            return Anexar(p);
        }
        catch (Exception e) { AppendChat("✗", e.Message); return false; }
    }

    /// <summary>Barra de miniaturas por cima da caixa de escrita. Clicar numa remove-a.</summary>
    private void SyncAnexos()
    {
        if (ChatAnexos is null) return;
        _anexoBar ??= ChatAnexos;
        ChatAnexos.Children.Clear();
        ChatAnexos.IsVisible = _chatAnexos.Count > 0;
        if (_chatAnexos.Count == 0) return;

        foreach (var p in _chatAnexos.ToList())
        {
            var thumb = new Border
            {
                Width = 46, Height = 46, CornerRadius = new CornerRadius(8),
                BorderBrush = new SolidColorBrush(Color.Parse("#DDDDDA")), BorderThickness = new Thickness(1),
                ClipToBounds = true, Cursor = new Cursor(StandardCursorType.Hand),
                Background = new SolidColorBrush(Color.Parse("#F4F4F2")),
            };
            try
            {
                using var bmp = SKBitmap.Decode(p);
                if (bmp is not null)
                {
                    // miniatura pequena: carregar o original para a UI a cada sync comia memória à toa
                    var s = 92.0 / Math.Max(bmp.Width, bmp.Height);
                    using var small = bmp.Resize(new SKImageInfo(Math.Max(1, (int)(bmp.Width * s)),
                                                                 Math.Max(1, (int)(bmp.Height * s))),
                                                 new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
                    using var img = SKImage.FromBitmap(small ?? bmp);
                    using var data = img.Encode(SKEncodedImageFormat.Png, 88);
                    using var ms = new MemoryStream(data.ToArray());
                    thumb.Child = new Image { Source = new Bitmap(ms), Stretch = Stretch.UniformToFill };
                }
            }
            catch { }

            var caminho = p;
            ToolTip.SetTip(thumb, Path.GetFileName(caminho) + "  ·  clica para tirar, arrasta para a tela");

            // Um só gesto, dois significados: clicar TIRA o anexo, arrastar MANDA-O para a tela.
            // Distinguem-se pelo movimento — sem este limiar, arrastar apagaria a miniatura a meio.
            PointerPressedEventArgs? pr = null;
            Point ini = default;
            thumb.PointerPressed += (_, ev) => { pr = ev; ini = ev.GetPosition(thumb); };
            thumb.PointerMoved += async (_, ev) =>
            {
                if (pr is null) return;
                var q = ev.GetPosition(thumb);
                if (Math.Abs(q.X - ini.X) + Math.Abs(q.Y - ini.Y) < 8) return;
                var arg = pr; pr = null;
                var dt = new DataTransfer();
                dt.Add(DataTransferItem.Create(DataFormat.Text, "klip-anexo:" + caminho));
                try { await DragDrop.DoDragDropAsync(arg, dt, DragDropEffects.Copy); } catch { }
            };
            thumb.PointerReleased += (_, _) =>
            {
                if (pr is null) return;              // já virou arrasto
                pr = null;
                _chatAnexos.Remove(caminho); SyncAnexos();
            };

            ChatAnexos.Children.Add(thumb);
        }
    }

    /// <summary>Chamado pelo OnSendChat: entrega os anexos e limpa-os.</summary>
    private List<string> TomarAnexos()
    {
        var lista = new List<string>(_chatAnexos);
        _chatAnexos.Clear();
        SyncAnexos();
        return lista;
    }
}
