using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Klip.Admin.Ai;

/// <summary>
/// Config partilhada em %APPDATA%\Klip.Admin\config.json (chave→valor).
/// Guarda a sessão admin (klip.session), a chave BYOK do Anthropic, o cursor do feed e o token do bus local.
/// Nota: v1 em texto simples numa pasta por-utilizador; DPAPI é hardening da fase de segurança (Step 12).
/// </summary>
public static class AdminAiConfig
{
    public const string WorkerUrl = "https://ferreirakorp-licensing.leonelferreira0373.workers.dev";
    public const string Model = "claude-sonnet-5";

    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Klip.Admin");
    private static readonly string File_ = Path.Combine(Dir, "config.json");
    private static readonly object Lock = new();
    private static Dictionary<string, string> _d = Load();

    private static Dictionary<string, string> Load()
    {
        try { if (File.Exists(File_)) return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(File_)) ?? new(); }
        catch { }
        return new();
    }
    private static void Save()
    {
        try { Directory.CreateDirectory(Dir); File.WriteAllText(File_, JsonSerializer.Serialize(_d)); } catch { }
    }

    public static string? Get(string key) { lock (Lock) return _d.TryGetValue(key, out var v) ? v : null; }
    public static void Set(string key, string? val)
    {
        lock (Lock) { if (val == null) _d.Remove(key); else _d[key] = val; Save(); }
    }
    public static void Remove(string key) { lock (Lock) { _d.Remove(key); Save(); } }

    public static string? AdminToken => Get("klip.session");
    public static string? ApiKey => Get("api_key");
    public static string Email => Get("klip.email") ?? "";

    public static long FeedCursor
    {
        get => long.TryParse(Get("feed.cursor"), out var v) ? v : 0;
        set => Set("feed.cursor", value.ToString());
    }

    /// <summary>Token do bus local (127.0.0.1). Gerado uma vez por instalação.</summary>
    public static string BusToken
    {
        get
        {
            var t = Get("bus.token");
            if (string.IsNullOrEmpty(t)) { t = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"); Set("bus.token", t); }
            return t;
        }
    }
}
