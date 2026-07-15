using System;
using System.Globalization;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Klip.App;

/// <summary>
/// §BETA — reclamações: POST → worker FK (/klip/complaint, produto fixado no servidor).
/// O backend acumula em D1 e envia UMA folha .xlsx ao dev às quintas 08:00 (Luanda) — não há
/// resposta instantânea, e a UI diz isso em vez de fingir um ticket.
///
/// Consentimento: o diagnóstico (máquina + uso) só é RECOLHIDO se o utilizador marcar a caixa.
/// Sem marca não se chama Machine()/Usage() sequer, e os campos nem vão no corpo do POST — o
/// servidor também os deitaria fora, mas não se recolhe o que não se vai enviar.
/// </summary>
public static class Complaints
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(45) };

    /// <summary>
    /// Dados da máquina. BARATO de propósito: variáveis de ambiente + 2 P/Invokes diretos.
    /// Nada de WMI (Win32_VideoController bloqueia segundos na primeira query) — a folha do
    /// Leonel não vale um congelamento da app.
    /// </summary>
    public static JsonObject Machine() => new()
    {
        ["os"] = RuntimeInformation.OSDescription,
        ["arch"] = RuntimeInformation.OSArchitecture.ToString(),
        ["cpu"] = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "?",
        ["cpu_cores"] = Environment.ProcessorCount,
        ["ram_gb"] = Math.Round(OpLog.TotalRamGb(), 1),
        ["gpu"] = PrimaryGpu(),
        ["dotnet"] = RuntimeInformation.FrameworkDescription,
        ["locale"] = CultureInfo.CurrentCulture.Name,
        ["app_version"] = Updater.CurrentVersion,
    };

    /// <summary>Uso desta sessão — vem dos contadores que o OpLog já mantém.</summary>
    public static JsonObject Usage(string aiMode, int layers)
    {
        var (ops, errors, uptimeMin, ramMb) = OpLog.Snapshot();
        return new JsonObject
        {
            ["ops"] = ops,
            ["errors"] = errors,
            ["uptime_min"] = Math.Round(uptimeMin, 1),
            ["ram_mb"] = ramMb,
            ["ai_mode"] = aiMode,
            ["layers"] = layers,
        };
    }

    /// <summary>
    /// Envia. Devolve (ok, info): em sucesso info = quando é a entrega; em falha = o motivo real.
    /// Nunca devolve ok em cima de um erro — a UI diz a verdade ao utilizador.
    /// </summary>
    public static async Task<(bool ok, string info)> SubmitAsync(
        string workerRoot, string name, string email, string category, string message,
        bool consent, JsonObject? machine, JsonObject? usage)
    {
        var body = new JsonObject
        {
            ["name"] = name,
            ["email"] = email,
            ["category"] = category,
            ["message"] = message,
            ["consent"] = consent,
            ["app_version"] = Updater.CurrentVersion,
            // sem `product`: a rota /klip/ fixa "KLIP" no servidor
        };
        if (consent)
        {
            body["machine"] = machine ?? new JsonObject();
            body["usage"] = usage ?? new JsonObject();
        }

        try
        {
            var resp = await Http.PostAsync(workerRoot.TrimEnd('/') + "/klip/complaint",
                new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
            var raw = await resp.Content.ReadAsStringAsync();
            JsonNode? n = null;
            try { n = JsonNode.Parse(raw); } catch { /* worker em baixo → HTML/vazio */ }
            if (n?["ok"]?.GetValue<bool>() == true)
                return (true, n["delivery"]?.GetValue<string>() ?? "quinta-feira 08:00");
            return (false, n?["error"]?.GetValue<string>() ?? $"o servidor respondeu {(int)resp.StatusCode}");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /* ── GPU: EnumDisplayDevices (user32) em vez de WMI — microssegundos, sem dependências ── */

    private const uint PrimaryDeviceFlag = 0x4;

    private static string PrimaryGpu()
    {
        try
        {
            string first = "";
            for (uint i = 0; i < 8; i++)
            {
                var d = new DISPLAY_DEVICE { cb = (uint)Marshal.SizeOf<DISPLAY_DEVICE>() };
                if (!EnumDisplayDevices(null, i, ref d, 0)) break;
                if (string.IsNullOrWhiteSpace(d.DeviceString)) continue;
                if ((d.StateFlags & PrimaryDeviceFlag) != 0) return d.DeviceString;
                if (first.Length == 0) first = d.DeviceString;
            }
            return first.Length > 0 ? first : "?";
        }
        catch { return "?"; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public uint cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum,
        ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);
}
