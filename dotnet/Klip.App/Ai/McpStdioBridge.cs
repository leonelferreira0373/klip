using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Klip.App.Ai;

/// <summary>
/// Modo bridge MCP embutido: `KLIP.exe --mcp-stdio` fala JSON-RPC (MCP) no stdin/stdout e faz
/// proxy para o editor VIVO (porta em %TEMP%\klip_mcp.json). O mesmo .exe é editor E bridge —
/// UM ficheiro, zero dependências externas. Lançado pelo claude/Claude Desktop como subprocesso.
/// </summary>
public static class McpStdioBridge
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(35) };

    private static string? EditorUrl()
    {
        try
        {
            // multi-composição: cada editor passa o SEU ficheiro de porta via env; fallback ao partilhado
            var f = Environment.GetEnvironmentVariable("KLIP_PORT_FILE");
            if (string.IsNullOrEmpty(f) || !File.Exists(f))
                f = Path.Combine(Path.GetTempPath(), "klip_mcp.json");
            var j = JsonNode.Parse(File.ReadAllText(f));
            return j?["url"]?.GetValue<string>();
        }
        catch { return null; }
    }

    public static int Run()
    {
        // FIX UTF-8 (acentos): ler/escrever stdin/stdout SEMPRE em UTF-8 (a consola Windows usa CP1252
        // → "coração" viria mojibake). Wrappers sobre os streams crus = à prova de pipes redirecionados.
        var utf8 = new System.Text.UTF8Encoding(false);
        using var stdin = new System.IO.StreamReader(Console.OpenStandardInput(), utf8, detectEncodingFromByteOrderMarks: false);
        var stdout = new System.IO.StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true };
        string? line;
        while ((line = stdin.ReadLine()) is not null)
        {
            line = line.Trim();
            if (line.Length == 0) continue;
            JsonNode? msg;
            try { msg = JsonNode.Parse(line); }
            catch { Emit(stdout, Err(null, -32700, "Parse error")); continue; }
            var resp = Dispatch(msg!);
            if (resp is not null) Emit(stdout, resp);
        }
        return 0;
    }

    private static JsonNode? Dispatch(JsonNode msg)
    {
        string method = msg["method"]?.GetValue<string>() ?? "";
        var id = msg["id"];
        if (id is null && method.StartsWith("notifications/")) return null;

        try
        {
            switch (method)
            {
                case "initialize":
                    return Ok(id, new JsonObject
                    {
                        ["protocolVersion"] = msg["params"]?["protocolVersion"]?.GetValue<string>() ?? "2025-06-18",
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                        ["serverInfo"] = new JsonObject { ["name"] = "klip", ["title"] = "KLIP Animator", ["version"] = "1.0" },
                    });
                case "ping":
                    return Ok(id, new JsonObject());
                case "tools/list":
                    return Ok(id, new JsonObject { ["tools"] = ToolList() });
                case "tools/call":
                {
                    string name = msg["params"]?["name"]?.GetValue<string>() ?? "";
                    var args = msg["params"]?["arguments"] ?? new JsonObject();
                    var (ok, payload) = CallEditor(name, args);
                    return Ok(id, new JsonObject
                    {
                        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = payload }),
                        ["isError"] = !ok,
                    });
                }
                default:
                    return id is null ? null : Err(id, -32601, "método desconhecido: " + method);
            }
        }
        catch (Exception ex)
        {
            return Err(id, -32000, ex.Message);
        }
    }

    private static JsonArray ToolList()
    {
        var url = EditorUrl() ?? throw new InvalidOperationException("O KLIP não está aberto. Abre o KLIP Animator primeiro.");
        var man = JsonNode.Parse(Http.GetStringAsync(url + "/manifest").GetAwaiter().GetResult())!;
        var tools = new JsonArray();
        foreach (var a in man["actions"]!.AsArray())
        {
            tools.Add(new JsonObject
            {
                ["name"] = a!["name"]!.GetValue<string>(),
                ["description"] = a["description"]?.GetValue<string>() ?? "",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = a["params"]?.DeepClone() ?? new JsonObject(),
                    ["required"] = a["required"]?.DeepClone() ?? new JsonArray(),
                    ["additionalProperties"] = true,
                },
            });
        }
        return tools;
    }

    private static (bool ok, string payload) CallEditor(string action, JsonNode args)
    {
        var url = EditorUrl();
        if (url is null) return (false, "O KLIP não está aberto. Abre o KLIP Animator primeiro.");
        var body = new JsonObject { ["action"] = action, ["params"] = args.DeepClone() };
        var resp = Http.PostAsync(url + "/call",
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
        var text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        var j = JsonNode.Parse(text);
        bool ok = j?["ok"]?.GetValue<bool>() ?? false;
        return ok
            ? (true, j!["result"]?.ToJsonString() ?? "{}")
            : (false, j?["error"]?.GetValue<string>() ?? "erro desconhecido");
    }

    private static JsonNode Ok(JsonNode? id, JsonNode result)
        => new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result };

    private static JsonNode Err(JsonNode? id, int code, string message)
        => new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        };

    private static void Emit(TextWriter w, JsonNode obj)
    {
        w.WriteLine(obj.ToJsonString());
        w.Flush();
    }
}
