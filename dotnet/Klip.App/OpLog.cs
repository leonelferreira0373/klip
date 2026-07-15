using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.SharpZipLib.Zip;

namespace Klip.App;

/// <summary>
/// Regista TODAS as operações da app (.md + .txt) + métricas (RAM/CPU/uptime). Uma vez por semana,
/// zipa com password klip{ano} e envia para o dev (via worker → email + BCC). O registo LOCAL é sempre
/// activo; o ENVIO (que leva email + device info) só acontece se o utilizador ligou a opção (consentimento).
/// </summary>
public static class OpLog
{
    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Klip", "oplog");
    private static string MdPath => Path.Combine(Dir, "ops.md");
    private static string TxtPath => Path.Combine(Dir, "ops.txt");
    private static readonly object _lock = new();
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };
    private static readonly DateTime _start = DateTime.UtcNow;
    private static long _ops, _errors;
    private static Timer? _sampler;
    private static bool _on;

    public static string ZipPassword => "klip" + DateTime.UtcNow.Year;   // auto pelo ano

    public static void Start()
    {
        if (_on) return; _on = true;
        try
        {
            Directory.CreateDirectory(Dir);
            Op("SESSION START", $"ver={Updater.CurrentVersion} · OS={RuntimeInformation.OSDescription} · "
                + $"CPU={Environment.ProcessorCount} cores · RAM={TotalRamGb():0.0}GB · arch={RuntimeInformation.OSArchitecture}");
            _sampler = new Timer(_ => Sample(), null, 60_000, 120_000);   // métricas a cada 2 min
        }
        catch { }
    }

    /// <summary>Regista uma operação (ação do bus, export, etc.).</summary>
    public static void Op(string kind, string detail = "")
    {
        Interlocked.Increment(ref _ops);
        Write($"- `{DateTime.UtcNow:HH:mm:ss}` **{kind}** {detail}".TrimEnd(),
              $"[{DateTime.UtcNow:HH:mm:ss}] {kind} {detail}".TrimEnd());
    }

    public static void Error(string msg)
    {
        Interlocked.Increment(ref _errors);
        Write($"- `{DateTime.UtcNow:HH:mm:ss}` ⚠️ **ERROR** {msg}", $"[{DateTime.UtcNow:HH:mm:ss}] ERROR {msg}");
    }

    /// <summary>Contadores da sessão — fonte do campo `usage` das reclamações (§BETA).
    /// Só sai da app com consentimento explícito; ver Complaints.Usage.</summary>
    public static (long ops, long errors, double uptimeMin, long ramMb) Snapshot()
    {
        long ram = 0;
        try { var p = Process.GetCurrentProcess(); p.Refresh(); ram = p.WorkingSet64 / 1048576; } catch { }
        return (Interlocked.Read(ref _ops), Interlocked.Read(ref _errors),
                (DateTime.UtcNow - _start).TotalMinutes, ram);
    }

    private static void Sample()
    {
        try
        {
            var p = Process.GetCurrentProcess(); p.Refresh();
            Op("METRIC", $"RAM={p.WorkingSet64 / 1048576}MB · CPU={p.TotalProcessorTime.TotalSeconds:0}s · "
                + $"uptime={(DateTime.UtcNow - _start).TotalMinutes:0}min · ops={_ops} · errors={_errors}");
        }
        catch { }
    }

    private static void Write(string md, string txt)
    {
        try { lock (_lock) { File.AppendAllText(MdPath, md + "\n"); File.AppendAllText(TxtPath, txt + "\n"); } }
        catch { }
    }

    /// <summary>Envio semanal (gated por opt-in): zip com password → worker → email dev + BCC. Depois limpa.</summary>
    public static async Task WeeklySendIfDue(string workerRoot, string email)
    {
        if (!Telemetry.OptIn) return;   // consentimento: só envia se o utilizador ligou
        try
        {
            if (!File.Exists(MdPath) || new FileInfo(MdPath).Length < 10) return;
            long.TryParse(Ai.AiConfig.GetProfile("oplog_last"), out var last);
            var nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (nowSec - last < 7L * 24 * 3600) return;

            Op("REPORT", $"a enviar relatório semanal (ops={_ops}, errors={_errors})");
            var zip = BuildZip();
            var body = new System.Text.Json.Nodes.JsonObject
            {
                ["email"] = string.IsNullOrEmpty(email) ? "anónimo" : email,
                ["filename"] = $"klip-oplog-{DateTime.UtcNow:yyyyMMdd}.zip",
                ["zipB64"] = Convert.ToBase64String(zip),
            }.ToJsonString();
            var resp = await Http.PostAsync(workerRoot.TrimEnd('/') + "/klip/oplog",
                new StringContent(body, Encoding.UTF8, "application/json"));
            if (resp.IsSuccessStatusCode)
            {
                lock (_lock) { File.WriteAllText(MdPath, ""); File.WriteAllText(TxtPath, ""); }
                Ai.AiConfig.SetProfile("oplog_last", nowSec.ToString());
            }
        }
        catch { }
    }

    private static byte[] BuildZip()
    {
        var ms = new MemoryStream();
        using (var zip = new ZipOutputStream(ms) { IsStreamOwner = false })
        {
            zip.Password = ZipPassword;   // klip{ano}
            zip.SetLevel(6);
            lock (_lock)
            {
                foreach (var f in new[] { MdPath, TxtPath })
                {
                    if (!File.Exists(f)) continue;
                    var bytes = File.ReadAllBytes(f);
                    var e = new ZipEntry(Path.GetFileName(f)) { DateTime = DateTime.Now, Size = bytes.Length };
                    zip.PutNextEntry(e);
                    zip.Write(bytes, 0, bytes.Length);
                    zip.CloseEntry();
                }
            }
        }
        return ms.ToArray();
    }

    internal static double TotalRamGb()
    {
        try { var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
              return GlobalMemoryStatusEx(ref m) ? m.ullTotalPhys / 1073741824.0 : 0; }
        catch { return 0; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
