using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Klip.Admin.Ai;

/// <summary>
/// Chat de admin conduzido pelo Sonnet 5 (claude-sonnet-5), com tool-use sobre a AdminActionRegistry.
/// Ferramentas que mutam disparam um pedido de confirmação (awaiting_confirmation) para o painel; o
/// utilizador aprova via aiConfirm → só então executam. Emite eventos para window.klipOnAiEvent.
/// </summary>
public sealed class AnthropicAdminBackend
{
    private const string Url = "https://api.anthropic.com/v1/messages";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };
    private readonly List<JsonNode> _history = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _confirms = new();
    private CancellationTokenSource? _cts;
    private int _confirmSeq;

    public Action<JsonObject>? Emit;   // → MainWindow → window.klipOnAiEvent

    private void Send(string type, Action<JsonObject>? more = null)
    { var o = new JsonObject { ["type"] = type }; more?.Invoke(o); Emit?.Invoke(o); }

    public void Confirm(string id, bool ok)
    { if (_confirms.TryRemove(id, out var tcs)) tcs.TrySetResult(ok); }

    public void Cancel() { _cts?.Cancel(); }

    public async Task SendAsync(string userText)
    {
        var key = AdminAiConfig.ApiKey;
        if (string.IsNullOrWhiteSpace(key)) { Send("error", o => o["error"] = "Configura a tua chave Anthropic (BYOK) nas definições para usar o assistente."); Send("done"); return; }
        _cts = new CancellationTokenSource();
        _history.Add(new JsonObject { ["role"] = "user", ["content"] = userText });

        try
        {
            for (int hop = 0; hop < 12; hop++)
            {
                var body = new JsonObject
                {
                    ["model"] = AdminAiConfig.Model,
                    ["max_tokens"] = 4096,
                    ["thinking"] = new JsonObject { ["type"] = "adaptive" },
                    ["system"] = SystemPrompt(),
                    ["tools"] = AdminActionRegistry.AnthropicTools(),
                    ["messages"] = Messages(),
                };
                var req = new HttpRequestMessage(HttpMethod.Post, Url) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
                req.Headers.TryAddWithoutValidation("x-api-key", key);
                req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                var res = await Http.SendAsync(req, _cts.Token);
                var txt = await res.Content.ReadAsStringAsync();
                if (!res.IsSuccessStatusCode) { Send("error", o => o["error"] = "API " + (int)res.StatusCode + ": " + Trunc(txt)); Send("done"); return; }

                var root = JsonNode.Parse(txt)!.AsObject();
                var content = root["content"]?.AsArray() ?? new JsonArray();
                // preserva os blocos do assistente (inclui thinking) antes de tool_result
                _history.Add(new JsonObject { ["role"] = "assistant", ["content"] = content.DeepClone() });

                var toolUses = new List<JsonObject>();
                foreach (var block in content)
                {
                    var b = block!.AsObject(); var bt = b["type"]?.GetValue<string>();
                    if (bt == "text") { var t = b["text"]?.GetValue<string>() ?? ""; if (t.Length > 0) Send("text", o => o["text"] = t); }
                    else if (bt == "thinking") { var t = b["thinking"]?.GetValue<string>() ?? ""; if (t.Length > 0) Send("thinking", o => o["text"] = t); }
                    else if (bt == "tool_use") toolUses.Add(b);
                }

                if ((root["stop_reason"]?.GetValue<string>()) != "tool_use" || toolUses.Count == 0) { Send("done"); return; }

                var results = new JsonArray();
                foreach (var tu in toolUses)
                {
                    var name = tu["name"]?.GetValue<string>() ?? "";
                    var id = tu["id"]?.GetValue<string>() ?? "";
                    var input = tu["input"] ?? new JsonObject();
                    var tool = AdminActionRegistry.Find(name);
                    Send("tool", o => { o["tool"] = name; o["args"] = input.DeepClone(); });

                    bool allow = true;
                    if (tool != null && tool.Mutates)
                    {
                        var cid = "c" + (++_confirmSeq);
                        var tcs = new TaskCompletionSource<bool>();
                        _confirms[cid] = tcs;
                        Send("awaiting_confirmation", o => { o["confirmId"] = cid; o["tool"] = name; o["args"] = input.DeepClone(); });
                        allow = await tcs.Task;   // espera o utilizador aprovar no painel
                    }

                    JsonNode? outp;
                    if (tool != null && tool.Mutates && !allow) outp = new JsonObject { ["cancelled"] = true, ["reason"] = "recusado pelo utilizador" };
                    else outp = await AdminActionRegistry.Execute(name, ToElement(input), allow);

                    Send("tool_result", o => { o["tool"] = name; o["result"] = (outp ?? new JsonObject()).DeepClone(); });
                    results.Add(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = id, ["content"] = (outp ?? new JsonObject()).ToJsonString() });
                }
                _history.Add(new JsonObject { ["role"] = "user", ["content"] = results });
            }
            Send("done");
        }
        catch (OperationCanceledException) { Send("done"); }
        catch (Exception e) { Send("error", o => o["error"] = e.Message); Send("done"); }
    }

    private JsonArray Messages()
    {
        var arr = new JsonArray();
        foreach (var m in _history) arr.Add(m!.DeepClone());
        return arr;
    }
    private static JsonElement ToElement(JsonNode n) => JsonDocument.Parse(n.ToJsonString()).RootElement;
    private static string Trunc(string s) => s.Length > 300 ? s.Substring(0, 300) : s;

    private static string SystemPrompt() =>
        "És o assistente do KLIP Administrador — o painel de gestão do KLIP Animator (produto de design/motion do Leonel Ferreira, Angola; marca FERREIRA KORP). "
      + "Ajudas o Leonel a tomar decisões: lês KPIs financeiros, vendas, problemas de pagamento e analytics do website, e podes conduzir o painel (navegar, destacar). "
      + "Podes também publicar no blog, definir o hero do site, resolver problemas e enviar notificações — mas essas acções pedem confirmação humana no painel antes de executar. "
      + "Responde em português de Portugal, direto e conciso. Usa as ferramentas para obter dados reais antes de afirmar números. Moeda: Kwanzas (Kz).";
}
