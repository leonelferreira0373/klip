using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Klip.Admin.Ai;

/// <summary>
/// `--mcp-stdio`: expõe o KLIP Administrador como servidor MCP (JSON-RPC por stdio) a qualquer cliente
/// MCP (Claude Code, etc.). Faz proxy para o bus local (bus.json) da app em execução; as ferramentas são
/// as MESMAS 16 da AdminActionRegistry. Se a app não estiver aberta, devolve erro claro.
/// </summary>
public static class AdminMcpStdioBridge
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(40) };
    private static string BusFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Klip.Admin", "bus.json");

    private static (int port, string token)? Bus()
    {
        try { var o = JsonNode.Parse(File.ReadAllText(BusFile))!.AsObject(); return ((int)o["port"]!, o["token"]!.GetValue<string>()!); }
        catch { return null; }
    }

    public static int Run()
    {
        var stdout = Console.Out;
        string? line;
        while ((line = Console.In.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonObject req;
            try { req = JsonNode.Parse(line)!.AsObject(); } catch { continue; }
            var id = req["id"]?.DeepClone();
            var method = req["method"]?.GetValue<string>() ?? "";
            JsonObject resp = new() { ["jsonrpc"] = "2.0" };
            if (id != null) resp["id"] = id;

            try
            {
                if (method == "initialize")
                    resp["result"] = new JsonObject { ["protocolVersion"] = "2024-11-05", ["serverInfo"] = new JsonObject { ["name"] = "klip-admin", ["version"] = "1.0.0" }, ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() } };
                else if (method == "notifications/initialized") continue;
                else if (method == "tools/list")
                    resp["result"] = new JsonObject { ["tools"] = McpTools() };
                else if (method == "tools/call")
                {
                    var p = req["params"]?.AsObject();
                    var name = p?["name"]?.GetValue<string>() ?? "";
                    var args = p?["arguments"] ?? new JsonObject();
                    var result = CallBus(name, args);
                    resp["result"] = new JsonObject { ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = result } } };
                }
                else { resp["error"] = new JsonObject { ["code"] = -32601, ["message"] = "method not found" }; }
            }
            catch (Exception e) { resp["error"] = new JsonObject { ["code"] = -32000, ["message"] = e.Message }; }

            if (id != null) { stdout.WriteLine(resp.ToJsonString()); stdout.Flush(); }
        }
        return 0;
    }

    private static JsonArray McpTools()
    {
        var bus = Bus();
        if (bus == null) return new JsonArray();
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{bus.Value.port}/manifest");
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bus.Value.token);
            var res = Http.Send(req);
            var o = JsonNode.Parse(res.Content.ReadAsStringAsync().Result)!.AsObject();
            var tools = o["tools"]?.AsArray() ?? new JsonArray();
            var arr = new JsonArray();
            foreach (var t in tools)
            {
                var to = t!.AsObject();
                arr.Add(new JsonObject { ["name"] = to["name"]!.GetValue<string>(), ["description"] = to["description"]?.GetValue<string>() ?? "", ["inputSchema"] = to["inputSchema"]?.DeepClone() ?? new JsonObject { ["type"] = "object" } });
            }
            return arr;
        }
        catch { return new JsonArray(); }
    }

    private static string CallBus(string name, JsonNode args)
    {
        var bus = Bus();
        if (bus == null) return "{\"error\":\"A app KLIP Administrador não está aberta. Abre-a para usar as ferramentas.\"}";
        try
        {
            var body = new JsonObject { ["tool"] = name, ["args"] = args.DeepClone() };
            var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{bus.Value.port}/call") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bus.Value.token);
            var res = Http.Send(req);
            return res.Content.ReadAsStringAsync().Result;
        }
        catch (Exception e) { return "{\"error\":\"" + e.Message.Replace("\"", "'") + "\"}"; }
    }
}
