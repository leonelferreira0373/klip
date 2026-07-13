using System;

namespace Klip.Engine;

/// <summary>Tipo real de um asset, deduzido por MAGIC BYTES (nunca pela extensão do URL).</summary>
public enum AssetKind { Image, Audio, Video, Font, Other }

/// <summary>
/// Deteção PURA e determinística do tipo de ficheiro pelos primeiros bytes (superset de
/// MainWindow.ExtOf + FontRegistry.IsFont). Sem I/O, sem rede. É a guarda de honestidade do
/// pipeline: HTML/JSON/erros → (Other,.bin), NUNCA fingem ser imagem/fonte.
/// </summary>
public static class AssetSniffer
{
    private static bool At(ReadOnlySpan<byte> b, int off, params byte[] sig)
    {
        if (b.Length < off + sig.Length) return false;
        for (int i = 0; i < sig.Length; i++) if (b[off + i] != sig[i]) return false;
        return true;
    }
    private static bool Ascii(ReadOnlySpan<byte> b, int off, string s)
    {
        if (b.Length < off + s.Length) return false;
        for (int i = 0; i < s.Length; i++) if (b[off + i] != (byte)s[i]) return false;
        return true;
    }

    /// <summary>Devolve o tipo + extensão canónica. Bytes desconhecidos → (Other, ".bin").</summary>
    public static AssetKind Detect(ReadOnlySpan<byte> b, out string ext)
    {
        // ---- imagens ----
        if (At(b, 0, 0x89, 0x50, 0x4E, 0x47)) { ext = ".png"; return AssetKind.Image; }   // PNG
        if (At(b, 0, 0xFF, 0xD8, 0xFF)) { ext = ".jpg"; return AssetKind.Image; }          // JPEG
        if (Ascii(b, 0, "GIF8")) { ext = ".gif"; return AssetKind.Image; }                 // GIF87a/89a
        if (At(b, 0, 0x42, 0x4D)) { ext = ".bmp"; return AssetKind.Image; }                // BMP "BM"

        // ---- containers RIFF (WEBP img / WAV audio / AVI video) — desambiguar pelos bytes 8..11 ----
        if (Ascii(b, 0, "RIFF"))
        {
            if (Ascii(b, 8, "WEBP")) { ext = ".webp"; return AssetKind.Image; }
            if (Ascii(b, 8, "WAVE")) { ext = ".wav"; return AssetKind.Audio; }
            if (Ascii(b, 8, "AVI ")) { ext = ".avi"; return AssetKind.Video; }
        }

        // ---- áudio ----
        if (Ascii(b, 0, "OggS")) { ext = ".ogg"; return AssetKind.Audio; }                 // Ogg
        if (Ascii(b, 0, "fLaC")) { ext = ".flac"; return AssetKind.Audio; }                // FLAC
        if (Ascii(b, 0, "ID3")) { ext = ".mp3"; return AssetKind.Audio; }                  // MP3 c/ tag ID3
        if (b.Length >= 2 && b[0] == 0xFF && (b[1] & 0xE0) == 0xE0) { ext = ".mp3"; return AssetKind.Audio; }  // MP3 frame-sync

        // ---- vídeo ----
        if (Ascii(b, 4, "ftyp")) { ext = ".mp4"; return AssetKind.Video; }                 // MP4/MOV (box ftyp)
        if (At(b, 0, 0x1A, 0x45, 0xDF, 0xA3)) { ext = ".webm"; return AssetKind.Video; }   // Matroska/WEBM

        // ---- fontes (delega a heurística ao FontRegistry, aqui só p/ classificar+extensão) ----
        if (At(b, 0, 0x00, 0x01, 0x00, 0x00) || Ascii(b, 0, "true") || Ascii(b, 0, "ttcf")) { ext = ".ttf"; return AssetKind.Font; }
        if (Ascii(b, 0, "OTTO")) { ext = ".otf"; return AssetKind.Font; }
        if (Ascii(b, 0, "wOFF")) { ext = ".woff"; return AssetKind.Font; }
        if (Ascii(b, 0, "wOF2")) { ext = ".woff2"; return AssetKind.Font; }

        ext = ".bin";
        return AssetKind.Other;   // HTML/JSON/erro/desconhecido → NUNCA finge ser um asset
    }
}
