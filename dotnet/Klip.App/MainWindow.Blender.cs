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

        // PROGRESSO AO VIVO: o Cycles imprime "Sample 12/96" no stdout. Sem isto o utilizador
        // fica minutos a olhar para nada, sem saber se está a trabalhar ou pendurado.
        int lastPct = -1;
        void OnLine(string line)
        {
            var m = System.Text.RegularExpressions.Regex.Match(line, @"Sample (\d+)/(\d+)");
            if (!m.Success) return;
            if (!int.TryParse(m.Groups[1].Value, out int cur) || !int.TryParse(m.Groups[2].Value, out int tot) || tot <= 0) return;
            int pct = (int)(100.0 * cur / tot);
            if (pct / 10 == lastPct / 10) return;      // só de 10 em 10% — senão inunda o chat
            lastPct = pct;
            var rem = System.Text.RegularExpressions.Regex.Match(line, @"Remaining:([\d:.]+)");
            string tail = rem.Success ? $" · faltam {rem.Groups[1].Value}" : "";
            UiChat("·", $"Blender a renderizar… {pct}%{tail}");
        }

        var outp = BlenderBridge.RenderStill(script, path,
            null, TimeSpan.FromSeconds(Math.Clamp(timeoutSec ?? 900, 5, 7200)), OnLine);
        sw.Stop();

        long bytes = new System.IO.FileInfo(outp).Length;

        // A IMAGEM ENTRA SOZINHA NA TELA. Corremos em background, por isso o documento
        // só pode ser mexido na UI thread — daí o Invoke.
        string? layerId = null;
        try
        {
            layerId = Avalonia.Threading.Dispatcher.UIThread.Invoke(() => ApiInsertImage(outp));
        }
        catch (Exception ex)
        {
            UiChat("·", "render pronto, mas não entrou na tela: " + ex.Message);
        }

        UiChat("·", $"Blender: {System.IO.Path.GetFileName(outp)} ({bytes / 1024} KB) em {sw.Elapsed.TotalSeconds:0.0}s"
                    + (layerId is null ? "" : " → na tela"));
        return new
        {
            ok = true,
            _image = outp,               // a IA recebe a imagem e pode criticá-la
            path = outp,
            id = layerId,                // camada criada na tela (null se falhou a inserção)
            bytes,
            seconds = Math.Round(sw.Elapsed.TotalSeconds, 2),
            blender = BlenderBridge.Version,
        };
    }
}
