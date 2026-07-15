using System;
using System.IO;
using System.Text.Json;

namespace Klip.App.Ai;

/// <summary>As 4 modalidades de IA do KLIP (.NET): Créditos (worker FK) · BYOK · Claude Code (CLI)
/// · conectores externos (MCP/API — o ControlServer). Config em %APPDATA%\Klip\ai.json,
/// PARTILHADA com a app legacy (mesmas chaves: mode, api_key, klip_email, worker_ai_url).</summary>
public static class AiConfig
{
    public const string ModeCredits = "credits";
    public const string ModeByok = "byok";
    public const string ModeCli = "cli";

    public const string DefaultWorkerUrl =
        "https://ferreirakorp-licensing.leonelferreira0373.workers.dev/ai";

    private static string CfgPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Klip", "ai.json");

    private static JsonElement? Load()
    {
        try { return JsonDocument.Parse(File.ReadAllText(CfgPath)).RootElement.Clone(); }
        catch { return null; }
    }

    private static string? Get(string key)
        => Load() is { } r && r.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
           ? v.GetString() : null;

    public static string ResolveApiKey()
        => Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? Get("api_key") ?? "";

    public static string ResolveEmail()
        => Environment.GetEnvironmentVariable("KLIP_EMAIL") ?? Get("klip_email") ?? "";

    public static string ResolveWorkerUrl()
        => Environment.GetEnvironmentVariable("KLIP_WORKER_AI_URL") ?? Get("worker_ai_url") ?? DefaultWorkerUrl;

    // ---- perfil local (nome/pfp) — otimista na UI, persistido aqui; sync ao worker no roadmap ----
    public static string? GetProfile(string key) => Get(key);

    public static void SetProfile(string key, string value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CfgPath)!);

            System.Text.Json.Nodes.JsonObject root;
            bool existe = File.Exists(CfgPath);
            try
            {
                root = existe
                    ? System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(CfgPath))!.AsObject()
                    : new System.Text.Json.Nodes.JsonObject();
            }
            catch
            {
                // Ficheiro ilegível. Antes, isto recomeçava do zero e a gravação seguinte levava com
                // ela a api_key, o klip_email, o mode e o worker_ai_url — apagados sem aviso.
                // Guardamos os bytes de lado para o utilizador (ou nós) poder recuperar a chave à mão.
                try { if (existe) File.Copy(CfgPath, CfgPath + ".corrompido", overwrite: true); } catch { }
                root = new System.Text.Json.Nodes.JsonObject();
            }

            root[key] = value;

            // Escrita atómica: um corte de energia a meio de um WriteAllText deixava o ficheiro truncado,
            // e o arranque seguinte lia-o como corrompido — era assim que a bola de neve começava.
            string tmp = CfgPath + ".tmp";
            File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            if (existe) File.Replace(tmp, CfgPath, null);
            else File.Move(tmp, CfgPath);
        }
        catch { /* otimista: a UI já refletiu */ }
    }

    public static string PfpPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Klip", "pfp.png");
}
