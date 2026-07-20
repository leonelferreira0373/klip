using System;
using System.Diagnostics;
using Avalonia.Controls;
using Klip.Engine.Blender;

namespace Klip.App;

/// <summary>
/// PONTE BLENDER (verbo blender_render do bus de IA). O KLIP é 2D/2.5D em Skia; quando o pedido
/// exige path-tracing a sério (produto fotorreal, vidro, GI), a IA escreve um script Python e
/// manda-o ao Blender headless — o PNG que volta entra na tela como qualquer outro asset.
/// Fica em ficheiro próprio para não engordar o MainWindow.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>
    /// blender_render: script Python → PNG. Corre em background (Background: true no bus) porque
    /// um render de Cycles em CPU leva minutos e travaria a UI thread inteira.
    /// Devolve "_image" para a IA VER o resultado sem ter de o inserir na tela primeiro.
    /// </summary>
    public object ApiBlenderRender(string script, string path, double? timeoutSec)
    {
        if (string.IsNullOrWhiteSpace(script)) throw new InvalidOperationException("script vazio");
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("path de saída vazio");
        if (!BlenderBridge.IsAvailable)
            throw new InvalidOperationException(
                "Blender não encontrado nesta máquina. Instala o Blender ou define KLIP_BLENDER com o caminho do blender.exe.");

        // A ponte entrega o destino ao script em sys.argv (depois de '--'), por isso não o inventamos aqui.
        path = System.IO.Path.GetFullPath(path);
        UiChat("·", $"Blender {BlenderBridge.Version} a renderizar… (CPU pode demorar)");

        var sw = Stopwatch.StartNew();
        var outp = BlenderBridge.RenderStill(script, path,
            null, TimeSpan.FromSeconds(Math.Clamp(timeoutSec ?? 900, 5, 7200)));
        sw.Stop();

        long bytes = new System.IO.FileInfo(outp).Length;
        UiChat("·", $"Blender: {System.IO.Path.GetFileName(outp)} ({bytes / 1024} KB) em {sw.Elapsed.TotalSeconds:0.0}s");
        return new
        {
            ok = true,
            _image = outp,               // a IA recebe a imagem e pode criticá-la
            path = outp,
            bytes,
            seconds = Math.Round(sw.Elapsed.TotalSeconds, 2),
            blender = BlenderBridge.Version,
        };
    }
}
