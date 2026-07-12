using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Klip.App.Ai;

/// <summary>
/// Modalidade "Claude Code" — conduz o claude.exe instalado (SUBSCRIÇÃO, sem chave/créditos) em
/// stream-json, isolado no MCP klip (o bridge klip-mcp.exe lê %TEMP%\klip_mcp.json → que ESTE
/// app escreve → a conversa edita ESTE editor). Port C# da técnica provada na app legacy.
/// </summary>
public sealed class ClaudeCliBackend
{
    public string ModelAlias { get; set; } = "opus";   // opus | sonnet | haiku
    public string? SessionId { get; private set; }
    private Process? _proc;
    private readonly Dictionary<string, string> _toolNames = new();

    private static readonly string Steer =
        "Estás embutido no editor KLIP Animator e falas com o utilizador DENTRO da app. As ferramentas " +
        "mcp__klip__* controlam o documento ABERTO ao vivo. Canvas 1000x700; x,y = offsets do CENTRO. " +
        "Chama mcp__klip__get_state / list_items antes de alterar. Age; tudo é undoável. Responde em PT, curto.\n\n" +
        AnthropicBackend.Skills;

    public static string? FindClaude()
    {
        var env = Environment.GetEnvironmentVariable("KLIP_CLAUDE_BIN");
        if (env is not null && File.Exists(env)) return env;
        var appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var candidates = new[]
        {
            Path.Combine(appdata, "npm", "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe"),
        };
        foreach (var c in candidates) if (File.Exists(c)) return c;
        return null;
    }

    /// <summary>Ficheiro de porta do editor a que este CLI pertence (multi-composição). Null = partilhado.</summary>
    public string? PortFile { get; set; }

    private string? WriteMcpConfig()
    {
        // o bridge É o próprio exe (`--mcp-stdio`) — um só ficheiro, sem dependências externas
        var self = Environment.ProcessPath;
        if (self is null || !File.Exists(self)) return null;
        var tag = PortFile is null ? "" : "_" + Path.GetFileNameWithoutExtension(PortFile);
        var path = Path.Combine(Path.GetTempPath(), $"klip_cli_mcp_dotnet{tag}.json");
        object klip = PortFile is null
            ? new { type = "stdio", command = self, args = new[] { "--mcp-stdio" } }
            : new { type = "stdio", command = self, args = new[] { "--mcp-stdio" },
                    env = new Dictionary<string, string> { ["KLIP_PORT_FILE"] = PortFile } };
        File.WriteAllText(path, JsonSerializer.Serialize(new { mcpServers = new { klip } }));
        return path;
    }

    public void Stop() { try { _proc?.Kill(entireProcessTree: true); } catch { } }

    public async Task Send(string prompt, Action<string, string> onEvent, CancellationToken ct)
    {
        var claude = FindClaude();
        if (claude is null)
        { onEvent("error", "Claude Code (CLI) não encontrado. npm i -g @anthropic-ai/claude-code"); return; }
        var cfg = WriteMcpConfig();

        var psi = new ProcessStartInfo(claude)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in new[] { "-p", prompt, "--output-format", "stream-json", "--verbose",
                                  "--permission-mode", "bypassPermissions",
                                  "--append-system-prompt", Steer,
                                  "--max-turns", "40", "--disallowedTools", "ToolSearch" })
            psi.ArgumentList.Add(a);
        if (cfg is not null) { psi.ArgumentList.Add("--mcp-config"); psi.ArgumentList.Add(cfg); psi.ArgumentList.Add("--strict-mcp-config"); }
        psi.ArgumentList.Add("--model"); psi.ArgumentList.Add(ModelAlias);
        if (SessionId is not null) { psi.ArgumentList.Add("--resume"); psi.ArgumentList.Add(SessionId); }

        try { _proc = Process.Start(psi); }
        catch (Exception ex) { onEvent("error", "não lancei o claude: " + ex.Message); return; }
        if (_proc is null) { onEvent("error", "processo não arrancou"); return; }

        _ = _proc.StandardError.ReadToEndAsync(ct);
        bool gotResult = false;
        try
        {
            while (!_proc.StandardOutput.EndOfStream)
            {
                if (ct.IsCancellationRequested) { Stop(); onEvent("done", "parado"); return; }
                var line = await _proc.StandardOutput.ReadLineAsync(ct);
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonDocument doc;
                try { doc = JsonDocument.Parse(line); } catch { continue; }
                using (doc) gotResult |= Parse(doc.RootElement, onEvent);
            }
        }
        catch (OperationCanceledException) { Stop(); onEvent("done", "parado"); return; }

        try { await _proc.WaitForExitAsync(CancellationToken.None); } catch { }
        if (!gotResult && _proc.ExitCode != 0)
            onEvent("error", $"claude saiu com código {_proc.ExitCode}");
        else onEvent("done", "ok");
    }

    private bool Parse(JsonElement o, Action<string, string> emit)
    {
        string? type = o.TryGetProperty("type", out var t) ? t.GetString() : null;
        switch (type)
        {
            case "system":
                if (o.TryGetProperty("subtype", out var st) && st.GetString() == "init")
                {
                    if (o.TryGetProperty("session_id", out var sid)) SessionId = sid.GetString();
                    string model = o.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";
                    emit("meta", $"Claude Code ligado · {model} · subscrição");
                }
                return false;
            case "assistant":
                foreach (var b in Content(o))
                {
                    var bt = b.TryGetProperty("type", out var btv) ? btv.GetString() : null;
                    if (bt == "text")
                    {
                        var txt = b.GetProperty("text").GetString()?.Trim();
                        if (!string.IsNullOrEmpty(txt)) emit("text", txt!);
                    }
                    else if (bt == "tool_use")
                    {
                        string name = b.GetProperty("name").GetString() ?? "?";
                        if (b.TryGetProperty("id", out var idv) && idv.GetString() is { } id) _toolNames[id] = name;
                        string inp = b.TryGetProperty("input", out var iv) ? iv.GetRawText() : "{}";
                        emit("tool", $"{name}({(inp.Length > 110 ? inp[..110] + "…" : inp)})");
                    }
                }
                return false;
            case "user":
                foreach (var b in Content(o))
                    if (b.TryGetProperty("type", out var ut) && ut.GetString() == "tool_result")
                    {
                        string content = b.TryGetProperty("content", out var cv)
                            ? (cv.ValueKind == JsonValueKind.String ? cv.GetString() ?? "" : cv.GetRawText())
                            : "";
                        emit("tool_result", content.Length > 150 ? content[..150] + "…" : content);
                    }
                return false;
            case "result":
                if (o.TryGetProperty("session_id", out var rs)) SessionId = rs.GetString();
                if (o.TryGetProperty("total_cost_usd", out var tc) && tc.ValueKind == JsonValueKind.Number)
                    emit("usage_cost", tc.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture));
                return true;
            default:
                return false;
        }
    }

    private static IEnumerable<JsonElement> Content(JsonElement o)
    {
        if (o.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var content)
            && content.ValueKind == JsonValueKind.Array)
            foreach (var b in content.EnumerateArray()) yield return b;
    }
}
