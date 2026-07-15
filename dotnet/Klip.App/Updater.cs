using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Klip.App;

/// <summary>
/// Auto-update probe embutido em CADA cópia: no arranque verifica a última release no GitHub;
/// se houver versão nova, descarrega o .exe em background e deixa pronto para aplicar (swap seguro
/// via batch que espera o processo fechar, copia por cima e relança).
/// </summary>
public static class Updater
{
    public const string CurrentVersion = "1.0.3";
    private const string LatestApi = "https://api.github.com/repos/leonelferreira0373/klip/releases/latest";
    private const string AssetName = "KLIP-Animator.exe";
    private const string ExeName = "KLIP Animator.exe";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(6) };

    private static string UpdateExe => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Klip", "update", AssetName);

    /// <summary>Verifica o GitHub. Devolve (temNova, tag, urlDownload).</summary>
    public static async Task<(bool newer, string tag, string url)> CheckAsync()
    {
        try
        {
            if (!Http.DefaultRequestHeaders.UserAgent.TryParseAdd("KLIP-Updater")) { }
            var json = await Http.GetStringAsync(LatestApi);
            var n = JsonNode.Parse(json);
            var tag = n?["tag_name"]?.GetValue<string>() ?? "";
            var url = "";
            if (n?["assets"] is JsonArray arr)
                foreach (var a in arr)
                    if (a?["name"]?.GetValue<string>() == AssetName)
                        url = a["browser_download_url"]?.GetValue<string>() ?? "";
            bool newer = Cmp(tag.TrimStart('v', 'V'), CurrentVersion) > 0 && !string.IsNullOrEmpty(url);
            return (newer, tag, url);
        }
        catch { return (false, "", ""); }
    }

    /// <summary>Descarrega o novo .exe para a pasta de update. Devolve o caminho ou null.</summary>
    public static async Task<string?> DownloadAsync(string url)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(UpdateExe)!);
            var bytes = await Http.GetByteArrayAsync(url);
            if (bytes.Length < 1_000_000) return null;   // sanidade
            await File.WriteAllBytesAsync(UpdateExe, bytes);
            return UpdateExe;
        }
        catch { return null; }
    }

    public static bool UpdateReady => File.Exists(UpdateExe) && new FileInfo(UpdateExe).Length > 1_000_000;

    /// <summary>Aplica: escreve um batch que espera o KLIP fechar, copia o novo por cima e relança.</summary>
    public static void Apply()
    {
        if (!UpdateReady) return;
        var target = Path.Combine(AppContext.BaseDirectory, ExeName);
        var bat = Path.Combine(Path.GetTempPath(), "klip_update.bat");
        var script =
            "@echo off\r\n" +
            "timeout /t 2 /nobreak >nul\r\n" +
            ":wait\r\n" +
            $"tasklist /fi \"imagename eq {ExeName}\" | find /i \"{ExeName}\" >nul && (timeout /t 1 /nobreak >nul & goto wait)\r\n" +
            $"copy /y \"{UpdateExe}\" \"{target}\" >nul\r\n" +
            $"start \"\" \"{target}\"\r\n" +
            "del \"%~f0\"\r\n";
        File.WriteAllText(bat, script);
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{bat}\"") { UseShellExecute = false, CreateNoWindow = true });
        Environment.Exit(0);
    }

    // compara "1.0.2" vs "1.0.10" numericamente por segmento
    private static int Cmp(string a, string b)
    {
        var pa = a.Split('.'); var pb = b.Split('.');
        for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            int x = i < pa.Length && int.TryParse(pa[i], out var xi) ? xi : 0;
            int y = i < pb.Length && int.TryParse(pb[i], out var yi) ? yi : 0;
            if (x != y) return x - y;
        }
        return 0;
    }
}
