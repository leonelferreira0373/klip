using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;

namespace Klip.Engine;

/// <summary>Resultado de baixar um asset (payload p/ o bus/IA). FontFamily preenchido só p/ fontes.</summary>
public sealed record AssetResult(string Path, AssetKind Kind, string Ext, int Bytes, bool FromCache, string? FontFamily = null);

/// <summary>
/// Downloader GENÉRICO de assets (imagem/áudio/vídeo/fonte/ficheiro) — espelha o FontRegistry:
/// valida pelo tipo REAL (magic bytes, via <see cref="AssetSniffer"/>), roteia p/ a pasta certa,
/// cacheia por content-hash (idempotente), escrita atómica. Fontes são DELEGADAS ao FontRegistry
/// (zero duplicação). O <paramref name="assetsRoot"/> é INJETADO pela App (o Engine não conhece %APPDATA%).
/// </summary>
public sealed class AssetDownloader
{
    private const long MaxBytes = 64L * 1024 * 1024;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(45) };

    // NB: declarado DEPOIS de Http — a init estática é textual e o ctor usa Http.
    public static AssetDownloader Shared { get; } = new();

    private AssetDownloader()
    {
        // UA realista (Wikimedia & CDNs rejeitam UA vazio/genérico) — igual em espírito ao _dl da App.
        Http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
    }

    /// <summary>Baixa qualquer url (http(s) ou data:) → valida → roteia → cacheia. blob: não é descarregável.</summary>
    public async Task<AssetResult> DownloadAsync(string url, string assetsRoot, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("url vazio");
        byte[] bytes = await FetchBytesAsync(url, ct);
        if (bytes.Length == 0) throw new InvalidOperationException("download vazio");
        if (bytes.Length > MaxBytes) throw new InvalidOperationException("asset demasiado grande (>64MB)");

        var kind = AssetSniffer.Detect(bytes, out string ext);

        // imagens: PROBE real (o magic byte diz PNG mas o conteúdo pode estar corrompido)
        if (kind == AssetKind.Image)
        {
            using var probe = SKBitmap.Decode(bytes);
            if (probe is null) { kind = AssetKind.Other; ext = ".bin"; }   // não finge imagem
        }

        string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()[..16];

        // FONTES → delega ao FontRegistry (mesma cache %APPDATA%\Klip\fonts, mesma validação)
        if (kind == AssetKind.Font && FontRegistry.IsFontBytes(bytes))
        {
            string alias = "web_font_" + hash;
            string fp = FontRegistry.Shared.CachePathFor(alias);
            bool cached = File.Exists(fp);
            if (!cached) FontRegistry.Shared.RegisterData(bytes, alias);   // valida + cacheia atómico
            return new AssetResult(fp, AssetKind.Font, ".ttf", bytes.Length, cached, alias);
        }

        string sub = kind switch
        {
            AssetKind.Image => "images",
            AssetKind.Audio => "audio",
            AssetKind.Video => "videos",
            _ => "downloads",
        };
        string dir = Path.Combine(assetsRoot, sub);
        Directory.CreateDirectory(dir);
        string dst = Path.Combine(dir, "web_" + hash + ext);
        if (File.Exists(dst)) return new AssetResult(dst, kind, ext, bytes.Length, true);   // idempotente (content-hash)

        string tmp = dst + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, dst, true);   // escrita atómica
        return new AssetResult(dst, kind, ext, bytes.Length, false);
    }

    private static async Task<byte[]> FetchBytesAsync(string url, CancellationToken ct)
    {
        if (url.StartsWith("blob:", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("blob: não é descarregável (stream do browser) — usa browser_capture ou o URL real");

        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            int comma = url.IndexOf(',');
            if (comma < 0) throw new InvalidOperationException("data: URL malformado");
            string meta = url[5..comma];
            string data = url[(comma + 1)..];
            return meta.Contains(";base64", StringComparison.OrdinalIgnoreCase)
                ? Convert.FromBase64String(data)
                : Encoding.UTF8.GetBytes(Uri.UnescapeDataString(data));
        }

        return await Http.GetByteArrayAsync(url, ct);
    }
}
