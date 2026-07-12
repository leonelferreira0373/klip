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
            System.Text.Json.Nodes.JsonObject root;
            try { root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(CfgPath))!.AsObject(); }
            catch { root = new System.Text.Json.Nodes.JsonObject(); }
            root[key] = value;
            Directory.CreateDirectory(Path.GetDirectoryName(CfgPath)!);
            File.WriteAllText(CfgPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* otimista: a UI já refletiu */ }
    }

    public static string PfpPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Klip", "pfp.png");
}
