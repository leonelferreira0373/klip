using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace Klip.App;

/// <summary>
/// Error logging OPT-IN: acumula exceções não tratadas num ficheiro local. SE o utilizador ligar a
/// opção, envia o relatório uma vez por semana para o dev (worker → email + BCC ferreira.korp) e limpa.
/// Nada é enviado sem opt-in.
/// </summary>
public static class Telemetry
{
    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Klip");
    private static string LogPath => Path.Combine(Dir, "errors.log");
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static bool _installed;

    public static bool OptIn
    {
        get => Ai.AiConfig.GetProfile("errlog_optin") == "1";
        set => Ai.AiConfig.SetProfile("errlog_optin", value ? "1" : "0");
    }

    /// <summary>Regista handlers globais → escreve sempre no ficheiro (o envio é que é opt-in + semanal).</summary>
    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log("UnhandledException", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log("UnobservedTask", e.Exception); e.SetObserved(); };
    }

    public static void Log(string kind, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.AppendAllText(LogPath, $"[{DateTime.UtcNow:u}] {kind}: {ex}\n\n");
        }
        catch { }
    }

    /// <summary>Arranque (fire-and-forget): se opt-in + ≥7 dias + há log → envia ao worker e limpa.</summary>
    public static async Task WeeklySendIfDue(string workerRoot, string email)
    {
        if (!OptIn) return;
        try
        {
            if (!File.Exists(LogPath)) return;
            long.TryParse(Ai.AiConfig.GetProfile("errlog_last"), out var last);
            var nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (nowSec - last < 7L * 24 * 3600) return;
            var log = File.ReadAllText(LogPath);
            if (string.IsNullOrWhiteSpace(log)) { Ai.AiConfig.SetProfile("errlog_last", nowSec.ToString()); return; }
            var body = new System.Text.Json.Nodes.JsonObject
            { ["email"] = string.IsNullOrEmpty(email) ? "anónimo" : email, ["version"] = "1.0", ["log"] = log }.ToJsonString();
            var resp = await Http.PostAsync(workerRoot.TrimEnd('/') + "/klip/errorlog",
                new StringContent(body, Encoding.UTF8, "application/json"));
            if (resp.IsSuccessStatusCode)
            {
                File.WriteAllText(LogPath, "");
                Ai.AiConfig.SetProfile("errlog_last", nowSec.ToString());
            }
        }
        catch { }
    }
}
