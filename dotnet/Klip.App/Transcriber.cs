using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Whisper.net;

namespace Klip.App;

/// <summary>
/// Transcrição de voz on-device — a "mesma tech" com que a IA lê imagens, mas para áudio.
/// Fluxo: ffmpeg (qualquer áudio/vídeo → 16kHz mono WAV) → Whisper.net (ggml, CPU) → texto.
/// O modelo (~142MB) é descarregado só na 1ª utilização para %APPDATA%\Klip\models — nada
/// carrega no arranque, portanto uma falha do runtime nativo NUNCA derruba o editor.
/// </summary>
public static class Transcriber
{
    // base multilingue: bom equilíbrio tamanho/qualidade (PT+EN). Trocável por small/medium.
    private const string ModelName = "ggml-base.bin";
    private const string ModelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin";

    private static WhisperFactory? _factory;
    private static readonly SemaphoreSlim _gate = new(1, 1);
    private static bool _nativeReady;

    /// <summary>
    /// Extrai as DLLs nativas do Whisper (embutidas como recurso) para {BaseDirectory}\runtimes\win-x64\.
    /// O loader do Whisper.net 1.9 SÓ procura aí (o RuntimeOptions.LibraryPath é ignorado nesta versão) e
    /// o pacote NÃO copia as nativas win-x64 no publish single-file — por isso tratamo-las nós, igual ao
    /// truque do modelo ONNX embutido do BgRemover. (Confirmado por teste: jfk.wav transcreve.)
    /// </summary>
    private static void EnsureNative()
    {
        if (_nativeReady) return;
        var dir = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64");
        Directory.CreateDirectory(dir);
        const string tag = ".Native.whisper.";
        var asm = typeof(Transcriber).Assembly;
        foreach (var res in asm.GetManifestResourceNames())
        {
            var ix = res.IndexOf(tag, StringComparison.Ordinal);
            if (ix < 0 || !res.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
            var outp = Path.Combine(dir, res[(ix + tag.Length)..]);
            if (File.Exists(outp) && new FileInfo(outp).Length > 0) continue;
            using var s = asm.GetManifestResourceStream(res)!;
            using var fs = File.Create(outp);
            s.CopyTo(fs);
        }
        _nativeReady = true;
    }

    private static string ModelPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Klip", "models");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, ModelName);
    }

    public static bool ModelReady()
    {
        var p = ModelPath();
        return File.Exists(p) && new FileInfo(p).Length > 1_000_000;
    }

    /// <summary>Descarrega o modelo (idempotente). Usa-se p/ tirar o download do caminho crítico.</summary>
    public static Task PrepareModel(Action<string>? progress = null) => EnsureModel(progress);

    private static async Task<string> EnsureModel(Action<string>? progress)
    {
        var path = ModelPath();
        if (ModelReady()) return path;
        progress?.Invoke("a descarregar o modelo de voz (~142MB, só na 1ª vez)…");
        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        var bytes = await http.GetByteArrayAsync(ModelUrl);
        var tmp = path + ".part";
        await File.WriteAllBytesAsync(tmp, bytes);
        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);
        return path;
    }

    /// <summary>Transcreve um ficheiro de áudio OU vídeo. Devolve o texto (auto-detecta a língua).</summary>
    public static async Task<string> Transcribe(string mediaPath, Action<string>? progress = null)
    {
        if (!File.Exists(mediaPath)) throw new FileNotFoundException("ficheiro não encontrado", mediaPath);

        // 1) ffmpeg → WAV 16kHz mono (o formato que o Whisper quer)
        var wav = Path.Combine(Path.GetTempPath(), "klip_tr_" + Environment.TickCount64 + ".wav");
        var psi = new ProcessStartInfo("ffmpeg",
            $"-y -i \"{mediaPath}\" -vn -ar 16000 -ac 1 -f wav \"{wav}\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        using (var pr = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg não encontrado no PATH"))
        {
            var err = await pr.StandardError.ReadToEndAsync();
            await pr.WaitForExitAsync();
            if (!File.Exists(wav) || new FileInfo(wav).Length < 1024)
                throw new InvalidOperationException("ffmpeg não extraiu áudio: " + Tail(err));
        }

        try
        {
            // 2) Whisper — factory partilhada (custosa de criar), processor por chamada
            var model = await EnsureModel(progress);
            await _gate.WaitAsync();
            try { EnsureNative(); _factory ??= WhisperFactory.FromPath(model); }
            finally { _gate.Release(); }

            progress?.Invoke("a transcrever…");
            var sb = new StringBuilder();
            await using var proc = _factory.CreateBuilder().WithLanguage("auto").Build();
            await using var fs = File.OpenRead(wav);
            await foreach (var seg in proc.ProcessAsync(fs))
                sb.Append(seg.Text);
            return sb.ToString().Trim();
        }
        finally { try { File.Delete(wav); } catch { } }
    }

    private static string Tail(string s) => s.Length <= 200 ? s : s[^200..];
}
