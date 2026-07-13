using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;

namespace Klip.Engine;

/// <summary>Resultado de carregar/baixar uma fonte (payload p/ o bus/IA).</summary>
public sealed record FontResult(string Family, string Source, string? CachePath, bool FromCache);

/// <summary>
/// Registo de fontes: a IA pode USAR e BAIXAR qualquer fonte — carregar .ttf/.otf do disco,
/// ir buscar ao Google Fonts por nome, ou por URL. Cache em memória + disco (%APPDATA%\Klip\fonts).
/// As SKTypeface são PROPRIEDADE do registry e NUNCA se dispõem (o TextShape não faz `using` nelas).
/// </summary>
public sealed class FontRegistry
{
    public static FontRegistry Shared { get; } = new();

    private readonly ConcurrentDictionary<string, SKTypeface> _mem = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Klip", "fonts");

    private FontRegistry() { try { Directory.CreateDirectory(CacheDir); } catch { } }

    private static string Sanitize(string s) => Regex.Replace(s, @"[^A-Za-z0-9_-]+", "_").Trim('_');

    /// <summary>family → SKTypeface. NUNCA lança: mem → cache disco → sistema → Default.</summary>
    public SKTypeface Resolve(string? family, bool bold)
    {
        if (string.IsNullOrWhiteSpace(family)) return SKTypeface.Default;
        if (_mem.TryGetValue(family, out var hit)) return hit;
        try
        {
            var p = Path.Combine(CacheDir, Sanitize(family) + ".ttf");
            if (File.Exists(p)) { var t = SKTypeface.FromFile(p); if (t != null) return _mem[family] = t; }
        }
        catch { }
        try
        {
            var sys = SKTypeface.FromFamilyName(family, bold ? SKFontStyle.Bold : SKFontStyle.Normal);
            if (sys != null && string.Equals(sys.FamilyName, family, StringComparison.OrdinalIgnoreCase))
                return _mem[family] = sys;          // match exato → cacheia sob o nome pedido
            if (sys != null) return sys;            // família próxima → usa mas não cacheia com nome errado
        }
        catch { }
        return SKTypeface.Default;
    }

    public string RegisterFile(string path, string? alias = null)
    {
        var tf = SKTypeface.FromFile(path) ?? throw new InvalidOperationException($"não é uma fonte válida: {path}");
        var fam = alias ?? tf.FamilyName;
        _mem[fam] = tf;
        return fam;
    }

    public string RegisterData(byte[] bytes, string alias)
    {
        if (!IsFont(bytes)) throw new InvalidOperationException("dados não são uma fonte TTF/OTF (talvez woff2 ou HTML de erro)");
        var tf = SKTypeface.FromData(SKData.CreateCopy(bytes)) ?? throw new InvalidOperationException("fonte inválida");
        _mem[alias] = tf;
        try
        {
            var dst = Path.Combine(CacheDir, Sanitize(alias) + ".ttf");
            var tmp = dst + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, dst, true);              // escrita atómica
        }
        catch { }
        return alias;
    }

    /// <summary>Carrega por caminho de disco, URL, ou NOME (→ Google Fonts). Idempotente (cache).</summary>
    public async Task<FontResult> LoadAsync(string spec, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(spec)) throw new ArgumentException("spec de fonte vazio");
        if (File.Exists(spec)) return new(RegisterFile(spec), "disk", spec, false);
        if (_mem.ContainsKey(spec)) return new(spec, "cache", null, true);

        var cached = Path.Combine(CacheDir, Sanitize(spec) + ".ttf");
        if (File.Exists(cached)) { RegisterFile(cached, spec); return new(spec, "cache", cached, true); }

        if (spec.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || spec.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var b = await DownloadAsync(spec, ct);
            RegisterData(b, spec);
            return new(spec, "url", Path.Combine(CacheDir, Sanitize(spec) + ".ttf"), false);
        }

        var url = await GoogleFontUrlAsync(spec, ct);
        var data = await DownloadAsync(url, ct);
        RegisterData(data, spec);
        return new(spec, "google", Path.Combine(CacheDir, Sanitize(spec) + ".ttf"), false);
    }

    private static async Task<string> GoogleFontUrlAsync(string name, CancellationToken ct)
    {
        var css = "https://fonts.googleapis.com/css2?family=" + Uri.EscapeDataString(name) + ":wght@400;700";
        using var req = new HttpRequestMessage(HttpMethod.Get, css);
        req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/4.0");   // UA legado → css2 serve TTF (não woff2)
        var res = await Http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        var m = Regex.Match(body, @"url\((https?://[^)]+?\.(?:ttf|otf))\)", RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;
        var slug = name.Replace(" ", "").ToLowerInvariant();
        foreach (var lic in new[] { "ofl", "apache", "ufl" })
        {
            var gh = $"https://raw.githubusercontent.com/google/fonts/main/{lic}/{slug}/{name.Replace(" ", "")}-Regular.ttf";
            try { using var h = new HttpRequestMessage(HttpMethod.Head, gh); if ((await Http.SendAsync(h, ct)).IsSuccessStatusCode) return gh; } catch { }
        }
        throw new InvalidOperationException($"fonte '{name}' não encontrada no Google Fonts (ou só existe em woff2). Usa um caminho .ttf ou URL.");
    }

    private static async Task<byte[]> DownloadAsync(string url, CancellationToken ct)
    {
        var bytes = await Http.GetByteArrayAsync(url, ct);
        if (bytes.Length > 15 * 1024 * 1024) throw new InvalidOperationException("fonte demasiado grande (>15MB)");
        if (!IsFont(bytes)) throw new InvalidOperationException("o download não é uma fonte TTF/OTF (talvez woff2 ou HTML)");
        return bytes;
    }

    private static bool IsFont(byte[] b) => IsFontBytes(b);

    /// <summary>Magic bytes de fonte (TTF/OTF/TTC/true). Partilhado com o AssetDownloader (Fase 8).</summary>
    internal static bool IsFontBytes(ReadOnlySpan<byte> b)
    {
        if (b.Length < 4) return false;
        uint m = (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);
        return m == 0x00010000 || m == 0x4F54544F /*OTTO*/ || m == 0x74746366 /*ttcf*/ || m == 0x74727565 /*true*/;
    }

    /// <summary>Caminho de cache determinístico p/ um alias de fonte (permite ao AssetDownloader delegar sem duplicar).</summary>
    public string CachePathFor(string alias) => Path.Combine(CacheDir, Sanitize(alias) + ".ttf");
}
