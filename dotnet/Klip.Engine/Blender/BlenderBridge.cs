using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace Klip.Engine.Blender;

/// <summary>Resultado cru de uma invocação do Blender headless.</summary>
public readonly record struct BlenderResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;

    /// <summary>Cauda do stderr (o Blender despeja megabytes de progresso; só o fim interessa ao erro).</summary>
    public string ErrorTail(int max = 1200)
    {
        var s = string.IsNullOrWhiteSpace(StdErr) ? StdOut : StdErr;
        s = s.Trim();
        return s.Length <= max ? s : s[^max..];
    }
}

/// <summary>
/// PONTE KLIP → BLENDER HEADLESS. O KLIP não embute um renderizador de path-tracing;
/// delega no Blender exatamente como já delega no ffmpeg: um processo filho, sem janela,
/// stdout/stderr capturados, e o ficheiro produzido no disco é a única prova que conta.
/// Nada aqui toca no documento do KLIP — é I/O puro, seguro de chamar de threads de fundo.
/// </summary>
public static class BlenderBridge
{
    // Descoberta cara (varre o disco e lança um processo p/ ler a versão): faz-se UMA vez por sessão.
    private static readonly object _gate = new();
    private static bool _probed;
    private static string? _exe;
    private static string? _version;

    /// <summary>Caminho do blender.exe descoberto (null = não há Blender nesta máquina).</summary>
    public static string? Executable { get { Probe(); return _exe; } }

    /// <summary>True se há um Blender utilizável — o chamador deve verificar ANTES de prometer um render.</summary>
    public static bool IsAvailable { get { Probe(); return _exe is not null; } }

    /// <summary>Versão reportada pelo próprio binário (ex. "5.2.0 LTS"); null se indisponível.</summary>
    public static string? Version { get { Probe(); return _version; } }

    /// <summary>Força nova descoberta (o utilizador pode instalar/apontar o Blender com o KLIP já aberto).</summary>
    public static void Rescan() { lock (_gate) { _probed = false; _exe = null; _version = null; } }

    private static void Probe()
    {
        lock (_gate)
        {
            if (_probed) return;
            _probed = true;
            _exe = Discover();
            if (_exe is null) return;
            // `--version` é barato (não abre cena) e é a única fonte fiável da versão:
            // o nome da pasta mente quando alguém renomeia a instalação.
            try
            {
                var r = Run(_exe, new[] { "--version" }, TimeSpan.FromSeconds(30));
                foreach (var line in r.StdOut.Split('\n'))
                {
                    var t = line.Trim();
                    if (t.StartsWith("Blender ", StringComparison.OrdinalIgnoreCase))
                    { _version = t["Blender ".Length..].Trim(); break; }
                }
            }
            catch { /* binário partido ou bloqueado: fica sem versão, mas ainda pode servir */ }
        }
    }

    private static string? Discover()
    {
        // 1) KLIP_BLENDER manda sempre — é a válvula de escape p/ builds portáteis ou versões de teste.
        var env = Environment.GetEnvironmentVariable("KLIP_BLENDER");
        if (!string.IsNullOrWhiteSpace(env))
        {
            env = env.Trim().Trim('"');
            if (File.Exists(env)) return env;
            // aceita também a PASTA da instalação (engano comum de quem configura à mão)
            var inDir = Path.Combine(env, ExeName);
            if (Directory.Exists(env) && File.Exists(inDir)) return inDir;
        }

        // 2) Instalações normais do Windows: "…\Blender Foundation\Blender X.Y\blender.exe".
        //    Há máquinas com 3 versões lado a lado — escolhemos a MAIS ALTA, não a primeira que aparece.
        string? best = null; Version bestV = new(0, 0);
        foreach (var root in InstallRoots())
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> dirs;
            try { dirs = Directory.EnumerateDirectories(root, "Blender *"); } catch { continue; }
            foreach (var d in dirs)
            {
                var exe = Path.Combine(d, ExeName);
                if (!File.Exists(exe)) continue;
                var v = ParseVersion(Path.GetFileName(d));
                if (best is null || v > bestV) { best = exe; bestV = v; }
            }
        }
        if (best is not null) return best;

        // 3) PATH em último lugar: quem o pôs lá pode ter posto um wrapper qualquer.
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                var exe = Path.Combine(dir.Trim().Trim('"'), ExeName);
                if (File.Exists(exe)) return exe;
            }
            catch { /* entradas inválidas no PATH são vulgares — ignora */ }
        }
        return null;
    }

    private static string ExeName =>
        OperatingSystem.IsWindows() ? "blender.exe" : "blender";

    private static IEnumerable<string> InstallRoots()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var f in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            {
                var p = Environment.GetFolderPath(f);
                if (!string.IsNullOrEmpty(p)) yield return Path.Combine(p, "Blender Foundation");
            }
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(local)) yield return Path.Combine(local, "Programs", "Blender Foundation");
        }
        else
        {
            yield return "/usr/share";       // blender-X.Y de distribuições
            yield return "/opt";             // tarball oficial
        }
    }

    private static Version ParseVersion(string dirName)
    {
        // "Blender 5.2" → 5.2 ; nomes esquisitos caem em 0.0 e perdem a corrida (mas continuam candidatos)
        var s = dirName.StartsWith("Blender ", StringComparison.OrdinalIgnoreCase) ? dirName["Blender ".Length..] : dirName;
        var buf = new StringBuilder();
        foreach (var c in s.Trim()) { if (char.IsDigit(c) || c == '.') buf.Append(c); else break; }
        return System.Version.TryParse(buf.ToString().Trim('.'), out var v) ? v : new Version(0, 0);
    }

    /// <summary>
    /// Corre um script Python no Blender headless e devolve (exitCode, stdout, stderr).
    /// O script vai para um .py temporário — o Blender não aceita código por stdin de forma fiável.
    /// `args` chegam ao script depois de `--` (leem-se com sys.argv[sys.argv.index("--")+1:]).
    /// </summary>
    public static BlenderResult RunScript(string pythonCode, string[]? args = null, TimeSpan? timeout = null,
                                          Action<string>? onLine = null)
    {
        if (string.IsNullOrWhiteSpace(pythonCode)) throw new ArgumentException("script vazio", nameof(pythonCode));
        var exe = Executable ?? throw new InvalidOperationException(
            "Blender não encontrado. Instala-o ou aponta a variável de ambiente KLIP_BLENDER ao blender.exe.");

        var dir = Path.Combine(Path.GetTempPath(), "klip_blender");
        Directory.CreateDirectory(dir);
        var script = Path.Combine(dir, "klip_" + Guid.NewGuid().ToString("N")[..12] + ".py");
        // UTF-8 SEM BOM: o Python do Blender lê UTF-8 por omissão e o BOM só cria ruído nos diffs/logs.
        File.WriteAllText(script, pythonCode, new UTF8Encoding(false));

        var argv = new List<string> { "-b", "--factory-startup", "-P", script };
        if (args is { Length: > 0 }) { argv.Add("--"); argv.AddRange(args); }

        try { return Run(exe, argv, timeout ?? TimeSpan.FromMinutes(30), onLine); }
        finally { try { File.Delete(script); } catch { } }
    }

    /// <summary>
    /// Render de uma imagem: corre o script e só devolve se o ficheiro REALMENTE apareceu.
    /// Apaga o alvo antes de correr — senão um render falhado passaria por bom à custa de um PNG velho.
    /// Sem `args`, o script recebe o destino como único argumento (o contrato mais simples possível:
    /// `out = sys.argv[sys.argv.index("--")+1:][0]` → `scene.render.filepath = out`).
    /// </summary>
    public static string RenderStill(string pythonCode, string outPath, string[]? args = null, TimeSpan? timeout = null,
                                     Action<string>? onLine = null)
    {
        if (string.IsNullOrWhiteSpace(outPath)) throw new ArgumentException("caminho de saída vazio", nameof(outPath));
        outPath = Path.GetFullPath(outPath);
        var outDir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);
        try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }

        var r = RunScript(pythonCode, args is { Length: > 0 } ? args : new[] { outPath }, timeout, onLine);
        if (!r.Ok)
            throw new InvalidOperationException($"Blender falhou (exit {r.ExitCode}):\n{r.ErrorTail()}");
        // MEDIDO: com -P, uma exceção no Python ainda sai com código 0. O exit code NÃO é prova de nada —
        // o ficheiro é. Por isso o stderr (traceback) vai na mensagem: sem ele o erro seria mudo.
        if (!File.Exists(outPath) || new FileInfo(outPath).Length == 0)
            throw new InvalidOperationException(
                $"o Blender terminou bem mas não escreveu {outPath} — confirma o render.filepath no script.\n{r.ErrorTail(600)}");
        return outPath;
    }

    /// <summary>Motor de processo partilhado. Lê stdout/stderr em paralelo — ler em série enche o pipe e tranca o Blender.</summary>
    private static BlenderResult Run(string exe, IEnumerable<string> argv, TimeSpan timeout, Action<string>? onLine = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory,
        };
        foreach (var a in argv) psi.ArgumentList.Add(a);   // ArgumentList = zero problemas de aspas/espaços

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("não consegui lançar o Blender: " + exe);
        var so = new StringBuilder(); var se = new StringBuilder();
        using var doneOut = new ManualResetEventSlim(false);
        using var doneErr = new ManualResetEventSlim(false);
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) { doneOut.Set(); return; }
            lock (so) so.AppendLine(e.Data);
            // o Cycles cospe o progresso por aqui — é o que deixa a UI mostrar o render a andar
            if (onLine is not null) { try { onLine(e.Data); } catch { } }
        };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is null) doneErr.Set(); else lock (se) se.AppendLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        if (!proc.WaitForExit((int)Math.Min(int.MaxValue, timeout.TotalMilliseconds)))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"o Blender excedeu {timeout.TotalSeconds:0}s e foi terminado.");
        }
        // As callbacks podem chegar depois do exit; espera-as ou perdes as últimas linhas (incl. o erro real).
        doneOut.Wait(TimeSpan.FromSeconds(5));
        doneErr.Wait(TimeSpan.FromSeconds(5));
        lock (so) lock (se) return new BlenderResult(proc.ExitCode, so.ToString(), se.ToString());
    }
}
