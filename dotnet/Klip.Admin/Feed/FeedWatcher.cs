using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Klip.Admin.Ai;
using Klip.Admin.Native;

namespace Klip.Admin.Feed;

/// <summary>
/// Vigia o feed do worker e dispara toasts do Windows para vendas (VERDADEIRO) e problemas (REQUER_HUMANO)
/// mesmo com a janela minimizada. É a ÚNICA fonte de toasts no desktop (o painel só actualiza badges).
/// Cursor persistido; primeira execução "prime" silencioso para não inundar com histórico.
/// </summary>
public static class FeedWatcher
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static Timer? _timer;
    public static Action? OnNewEvents;   // MainWindow → refresca badges/KPIs

    public static async void Start()
    {
        if (AdminAiConfig.Get("feed.primed") != "1")
        {
            for (int i = 0; i < 60; i++) { if (!await Poll(silent: true)) break; }
            AdminAiConfig.Set("feed.primed", "1");
        }
        _timer = new Timer(async _ => await Poll(silent: false), null, 6000, 20000);
    }

    public static void Stop() { _timer?.Dispose(); _timer = null; }

    private static async Task<bool> Poll(bool silent)
    {
        var tok = AdminAiConfig.AdminToken;
        if (string.IsNullOrEmpty(tok)) return false;
        try
        {
            var since = AdminAiConfig.FeedCursor;
            var req = new HttpRequestMessage(HttpMethod.Get, AdminAiConfig.WorkerUrl + "/admin/feed?since=" + since + "&limit=50");
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + tok);
            var res = await Http.SendAsync(req);
            if (!res.IsSuccessStatusCode) return false;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            if (!root.TryGetProperty("events", out var evs) || evs.ValueKind != JsonValueKind.Array) return false;

            int n = 0;
            foreach (var e in evs.EnumerateArray())
            {
                n++;
                if (silent) continue;
                var type = e.TryGetProperty("type", out var ty) ? ty.GetString() : "";
                var valorFmt = e.TryGetProperty("valorFmt", out var vf) ? vf.GetString() : "";
                string who = "";
                if (e.TryGetProperty("nome", out var nm) && nm.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(nm.GetString())) who = nm.GetString()!;
                else if (e.TryGetProperty("email", out var em) && em.ValueKind == JsonValueKind.String) who = em.GetString()!;
                if (type == "sale") Toasts.Show("Nova venda KLIP ✓", (valorFmt + " — " + who).Trim(' ', '—'));
                else Toasts.Show("Problema de pagamento", (valorFmt + " — " + who).Trim(' ', '—'));
            }
            if (root.TryGetProperty("cursor", out var cur) && cur.TryGetInt64(out var c) && c > 0) AdminAiConfig.FeedCursor = c;
            if (n > 0 && !silent) OnNewEvents?.Invoke();
            return n > 0;
        }
        catch { return false; }
    }
}
