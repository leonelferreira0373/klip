using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Klip.App.Ai;

/// <summary>
/// The AI-first control server of the .NET KLIP — HTTP/JSON on 127.0.0.1, SAME protocol and
/// SAME port file (%TEMP%\klip_mcp.json) as the legacy Python app, so the existing klip-mcp.exe
/// stdio bridge + user-scope MCP registration drive THIS editor with zero new plumbing.
/// GET /health · GET /manifest · POST /call {action, params}.
/// </summary>
public sealed class ControlServer : IDisposable
{
    private readonly HttpListener _http = new();
    private readonly ActionRegistry _registry;
    public int Port { get; }
    public string Url => $"http://127.0.0.1:{Port}";
    public string PortFilePath { get; }   // ficheiro de porta ÚNICO deste editor (multi-composição)

    public ControlServer(ActionRegistry registry, string instanceId = "main")
    {
        _registry = registry;
        Port = FreePort();
        _http.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _http.Start();
        _ = Task.Run(Loop);
        PortFilePath = Path.Combine(Path.GetTempPath(), $"klip_mcp_{instanceId}.json");
        WritePortFile();
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private void WritePortFile()
    {
        var info = JsonSerializer.Serialize(new { port = Port, url = Url, pid = Environment.ProcessId });
        try { File.WriteAllText(PortFilePath, info); } catch { }
        // também o ficheiro partilhado (ferramentas externas encontram "um" KLIP; última janela ganha)
        try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "klip_mcp.json"), info); } catch { }
    }

    private async Task Loop()
    {
        while (_http.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _http.GetContextAsync(); }
            catch { break; }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    private async Task Handle(HttpListenerContext ctx)
    {
        try
        {
            var req = ctx.Request;
            string path = req.Url?.AbsolutePath ?? "/";
            if (req.HttpMethod == "GET" && path == "/health")
            { await Send(ctx, new { ok = true, service = "klip-dotnet", engine = ".NET" }); return; }
            if (req.HttpMethod == "GET" && path == "/manifest")
            { await Send(ctx, new { service = "klip", actions = _registry.Manifest() }); return; }
            if (req.HttpMethod == "POST" && path == "/call")
            {
                using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync();
                JsonDocument doc;
                try { doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body); }
                catch (Exception ex)
                {
                    // corpo mal-formado (ex.: caminho Windows com \ não escapado) → erro CLARO, não reset da ligação.
                    await Send(ctx, new { ok = false, error =
                        "JSON inválido no corpo. Escapa as barras de caminhos (C:\\\\a\\\\b) ou usa barras normais (C:/a/b). " + ex.Message }, 400);
                    return;
                }
                using (doc)
                {
                    string? action = doc.RootElement.TryGetProperty("action", out var a) ? a.GetString() : null;
                    if (action is null)
                    { await Send(ctx, new { ok = false, error = "falta 'action'" }, 400); return; }
                    JsonElement pars = doc.RootElement.TryGetProperty("params", out var p)
                        ? p.Clone() : JsonDocument.Parse("{}").RootElement.Clone();
                    try
                    {
                        var result = await _registry.Execute(action, pars);
                        await Send(ctx, new { ok = true, result });
                    }
                    catch (Exception ex)
                    { try { await Send(ctx, new { ok = false, error = ex.Message }, 500); } catch { } }
                }
                return;
            }
            await Send(ctx, new { error = "not found" }, 404);
        }
        catch
        {
            try { ctx.Response.Abort(); } catch { }
        }
    }

    private static async Task Send(HttpListenerContext ctx, object obj, int code = 200)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(obj);
        ctx.Response.StatusCode = code;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    public void Dispose()
    {
        try { _http.Stop(); } catch { }
    }
}
