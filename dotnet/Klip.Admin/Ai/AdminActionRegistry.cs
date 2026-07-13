using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Klip.Admin.Ai;

public sealed record AdminTool(string Name, string Desc, string Schema, bool Mutates, Func<JsonElement, Task<JsonNode?>> Run);

/// <summary>
/// A superfície de controlo do KLIP Administrador — as MESMAS 16 ferramentas para o chat interno,
/// o bus HTTP local e o MCP. Ferramentas de LEITURA correm livres; ferramentas que MUTAM (Mutates=true)
/// passam pela ConfirmGate. Grounded nos endpoints /admin/* do worker + no window.KlipDash do painel.
/// </summary>
public static class AdminActionRegistry
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(35) };

    /// <summary>Ligado pela MainWindow: invoca window.KlipDash.&lt;method&gt;(argsJson) no WebView.</summary>
    public static Func<string, string, Task<string?>>? UiDriver;

    private static HttpRequestMessage Req(HttpMethod m, string path, string? body = null)
    {
        var r = new HttpRequestMessage(m, AdminAiConfig.WorkerUrl + path);
        r.Headers.TryAddWithoutValidation("Authorization", "Bearer " + (AdminAiConfig.AdminToken ?? ""));
        if (body != null) r.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return r;
    }
    private static async Task<JsonNode?> Wget(string path)
    {
        var res = await Http.SendAsync(Req(HttpMethod.Get, path));
        return JsonNode.Parse(await res.Content.ReadAsStringAsync());
    }
    private static async Task<JsonNode?> Wpost(string path, JsonElement body)
    {
        var res = await Http.SendAsync(Req(HttpMethod.Post, path, body.GetRawText()));
        return JsonNode.Parse(await res.Content.ReadAsStringAsync());
    }
    private static async Task<JsonNode?> Ui(string method, string args)
    {
        var r = UiDriver != null ? await UiDriver(method, args) : null;
        return new JsonObject { ["ok"] = true, ["result"] = r };
    }
    private static string Num(JsonElement a, string k, string def) => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) ? v.ToString() : def;
    private static string Str(JsonElement a, string k, string def) => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : def;

    public static readonly List<AdminTool> Tools = new()
    {
        new("get_kpis", "KPIs financeiros: receita total/hoje/7d/30d, vendas, ticket médio, conversão, créditos por usar, pagamentos pendentes, receita por produto.", "{\"type\":\"object\",\"properties\":{}}", false, _ => Wget("/admin/kpi")),
        new("get_financeiro", "Detalhe financeiro (igual a get_kpis, para a aba Financeiro).", "{\"type\":\"object\",\"properties\":{}}", false, _ => Wget("/admin/financeiro")),
        new("get_analytics", "Analytics do website: visitas, únicos, cliques, referências, telemóvel-vs-computador, cliques de descarregar e CTR.", "{\"type\":\"object\",\"properties\":{\"days\":{\"type\":\"number\",\"description\":\"janela em dias (default 30)\"}}}", false, a => Wget("/admin/analytics?days=" + Num(a, "days", "30"))),
        new("list_sales", "Últimas vendas confirmadas (comprovativos VERDADEIRO).", "{\"type\":\"object\",\"properties\":{\"limit\":{\"type\":\"number\"}}}", false, a => Wget("/admin/sales?limit=" + Num(a, "limit", "20"))),
        new("list_issues", "Problemas de pagamento (comprovativos que precisam de atenção humana).", "{\"type\":\"object\",\"properties\":{}}", false, _ => Wget("/admin/issues")),
        new("get_feed", "Eventos recentes (vendas/problemas) desde um cursor (id).", "{\"type\":\"object\",\"properties\":{\"since\":{\"type\":\"number\"}}}", false, a => Wget("/admin/feed?since=" + Num(a, "since", "0"))),
        new("list_posts", "Novidades/posts publicados no blog.", "{\"type\":\"object\",\"properties\":{}}", false, _ => Wget("/admin/blog")),
        new("get_ui_state", "Estado atual do painel (aba activa, KPIs visíveis).", "{\"type\":\"object\",\"properties\":{}}", false, _ => Ui("currentState", "")),
        new("navigate", "Muda a aba visível do painel.", "{\"type\":\"object\",\"properties\":{\"tab\":{\"type\":\"string\",\"enum\":[\"overview\",\"financeiro\",\"vendas\",\"problemas\",\"website\",\"blog\"]}},\"required\":[\"tab\"]}", false, a => Ui("go", "\"" + Str(a, "tab", "overview") + "\"")),
        new("highlight", "Destaca visualmente um elemento do painel por selector CSS.", "{\"type\":\"object\",\"properties\":{\"selector\":{\"type\":\"string\"}},\"required\":[\"selector\"]}", false, a => Ui("highlight", JsonSerializer.Serialize(Str(a, "selector", "")))),
        new("refresh_ui", "Recarrega os dados do painel.", "{\"type\":\"object\",\"properties\":{}}", false, _ => Ui("refresh", "")),
        new("export_report", "Resumo em texto (markdown) dos KPIs + analytics para copiar/enviar.", "{\"type\":\"object\",\"properties\":{}}", false, _ => ExportReport()),
        // ---- MUTAM (ConfirmGate) ----
        new("resolve_issue", "Marca um problema de pagamento como resolvido.", "{\"type\":\"object\",\"properties\":{\"txn\":{\"type\":\"string\"},\"note\":{\"type\":\"string\"}},\"required\":[\"txn\"]}", true, a => Wpost("/admin/issue/handle", a)),
        new("publish_post", "Publica uma novidade no blog (aparece no site).", "{\"type\":\"object\",\"properties\":{\"title\":{\"type\":\"string\"},\"body_html\":{\"type\":\"string\"}},\"required\":[\"body_html\"]}", true, a => Wpost("/admin/blog", a)),
        new("set_hero", "Define o conteúdo em destaque (hero) da homepage do site.", "{\"type\":\"object\",\"properties\":{\"content\":{\"type\":\"string\",\"description\":\"HTML do hero\"}},\"required\":[\"content\"]}", true, a => Wpost("/admin/hero", a)),
        new("send_notification", "Regista uma notificação (title, body) — aparece no feed do admin.", "{\"type\":\"object\",\"properties\":{\"title\":{\"type\":\"string\"},\"body\":{\"type\":\"string\"}},\"required\":[\"title\"]}", true, a => Wpost("/admin/notify", a)),
    };

    public static AdminTool? Find(string name) => Tools.FirstOrDefault(t => t.Name == name);

    /// <summary>Executa. Ferramentas que mutam sem autorização → {status:"awaiting_confirmation"}.</summary>
    public static async Task<JsonNode?> Execute(string name, JsonElement args, bool allowMutations)
    {
        var tool = Find(name);
        if (tool == null) return new JsonObject { ["error"] = "ferramenta desconhecida: " + name };
        if (tool.Mutates && !allowMutations) return new JsonObject { ["status"] = "awaiting_confirmation", ["tool"] = name };
        try { return await tool.Run(args); }
        catch (Exception e) { return new JsonObject { ["error"] = e.Message }; }
    }

    /// <summary>Definições no formato tool-use do Anthropic.</summary>
    public static JsonArray AnthropicTools()
    {
        var arr = new JsonArray();
        foreach (var t in Tools)
            arr.Add(new JsonObject { ["name"] = t.Name, ["description"] = t.Desc + (t.Mutates ? " (requer confirmação)" : ""), ["input_schema"] = JsonNode.Parse(t.Schema) });
        return arr;
    }

    /// <summary>Manifesto legível (bus /manifest + MCP tools/list + llms.txt).</summary>
    public static JsonArray Manifest()
    {
        var arr = new JsonArray();
        foreach (var t in Tools)
            arr.Add(new JsonObject { ["name"] = t.Name, ["description"] = t.Desc, ["mutates"] = t.Mutates, ["inputSchema"] = JsonNode.Parse(t.Schema) });
        return arr;
    }

    private static async Task<JsonNode?> ExportReport()
    {
        var k = await Wget("/admin/kpi"); var a = await Wget("/admin/analytics?days=30");
        string S(JsonNode? n, string p) { try { return n?[p]?.ToString() ?? "?"; } catch { return "?"; } }
        var sb = new StringBuilder();
        sb.AppendLine("# Relatório KLIP");
        sb.AppendLine($"- Receita total: {S(k, "totalKz")} Kz  ·  Vendas: {S(k, "saleCount")}");
        sb.AppendLine($"- Hoje: {S(k?["today"], "kz")} Kz  ·  30 dias: {S(k?["d30"], "kz")} Kz");
        sb.AppendLine($"- Créditos por usar: ${S(k, "creditsOutstandingUsd")}  ·  Pendentes: {S(k, "pendingPayRequests")}");
        sb.AppendLine($"- Visitas (30d): via analytics  ·  Cliques descarregar: {S(a, "downloadClicks")}  ·  CTR: {S(a, "ctr")}");
        return new JsonObject { ["ok"] = true, ["report_md"] = sb.ToString() };
    }
}
