using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Klip.Admin.Ai;

/// <summary>
/// Bus local (127.0.0.1 apenas) para qualquer IA/programa externo conduzir a app.
/// GET /health é aberto; GET /manifest e POST /call exigem Authorization: Bearer &lt;install-token&gt;.
/// Escreve porta+token+pid em %APPDATA%\Klip.Admin\bus.json (lido pela ponte MCP). Ações que mutam
/// pela via headless devolvem {status:"awaiting_confirmation"} — precisam do painel aberto p/ aprovar.
/// </summary>
public sealed class AdminControlServer
{
    private HttpListener? _listener;
    public int Port { get; private set; }
    public string Token => AdminAiConfig.BusToken;

    private static string BusFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Klip.Admin", "bus.json");

    public void Start()
    {
        for (int p = 47810; p < 47840; p++)
        {
            try
            {
                var l = new HttpListener();
                l.Prefixes.Add($"http://127.0.0.1:{p}/");
                l.Start();
                _listener = l; Port = p; break;
            }
            catch { }
        }
        if (_listener == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(BusFile)!);
            File.WriteAllText(BusFile, new JsonObject { ["port"] = Port, ["token"] = Token, ["pid"] = Environment.ProcessId }.ToJsonString());
        }
        catch { }
        _ = Task.Run(Loop);
    }

    private async Task Loop()
    {
        while (_listener != null && _listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); } catch { break; }
            _ = HandleAsync(ctx);
        }
    }

    private static bool BearerOk(HttpListenerContext ctx)
    {
        var auth = ctx.Request.Headers["Authorization"] ?? "";
        if (!auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
        var tok = auth.Substring(7).Trim();
        var a = Encoding.UTF8.GetBytes(tok); var b = Encoding.UTF8.GetBytes(AdminAiConfig.BusToken);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            if (ctx.Request.HttpMethod == "GET" && path == "/health") { await Write(ctx, 200, new JsonObject { ["ok"] = true, ["app"] = "KLIP Administrador", ["pid"] = Environment.ProcessId }); return; }

            if (!BearerOk(ctx)) { await Write(ctx, 401, new JsonObject { ["error"] = "unauthorized" }); return; }

            if (ctx.Request.HttpMethod == "GET" && path == "/manifest")
            { await Write(ctx, 200, new JsonObject { ["app"] = "KLIP Administrador", ["tools"] = AdminActionRegistry.Manifest() }); return; }

            if (ctx.Request.HttpMethod == "POST" && path == "/call")
            {
                string raw; using (var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) raw = await sr.ReadToEndAsync();
                JsonElement body; try { body = JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw).RootElement; }
                catch { await Write(ctx, 400, new JsonObject { ["error"] = "json inválido" }); return; }
                var tool = body.TryGetProperty("tool", out var t) ? t.GetString() ?? "" : (body.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "");
                var args = body.TryGetProperty("args", out var ar) ? ar : (body.TryGetProperty("params", out var pr) ? pr : default);
                var result = await AdminActionRegistry.Execute(tool, args, allowMutations: false); // headless: mutações → awaiting_confirmation
                await Write(ctx, 200, result);
                return;
            }
            await Write(ctx, 404, new JsonObject { ["error"] = "not found" });
        }
        catch { try { ctx.Response.Abort(); } catch { } }
    }

    private static async Task Write(HttpListenerContext ctx, int status, JsonNode? body)
    {
        var bytes = Encoding.UTF8.GetBytes((body ?? new JsonObject()).ToJsonString());
        ctx.Response.StatusCode = status; ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }
}
