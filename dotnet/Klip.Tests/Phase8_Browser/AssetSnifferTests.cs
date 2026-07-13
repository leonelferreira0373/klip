using System.Text;
using Klip.Engine;
using Klip.Tests.Framework;

namespace Klip.Tests.Phase8_Browser;

/// <summary>Fase 8 — deteção de tipo por MAGIC BYTES (roteamento determinístico + honestidade:
/// HTML/JSON/erros NUNCA fingem ser imagem/fonte).</summary>
public static class AssetSnifferTests
{
    private static byte[] Riff(string sub)
    {
        var b = new byte[12];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(b, 0);
        Encoding.ASCII.GetBytes(sub).CopyTo(b, 8);
        return b;
    }

    private static void Chk(byte[] b, AssetKind k, string ext, string label)
    {
        var got = AssetSniffer.Detect(b, out var e);
        Assert.True(got == k, $"{label}: kind esperado {k}, obtive {got}");
        Assert.True(e == ext, $"{label}: ext esperada {ext}, obtive {e}");
    }

    [KlipTest(8, "asset-sniffer: magic bytes → tipo+extensão corretos (imagem/áudio/vídeo/fonte)",
        Criterion = "cada header conhecido classifica certo; roteamento 100% determinístico")]
    public static void MagicBytesRouting()
    {
        Chk(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 0 }, AssetKind.Image, ".png", "PNG");
        Chk(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, AssetKind.Image, ".jpg", "JPEG");
        Chk(Encoding.ASCII.GetBytes("GIF89a"), AssetKind.Image, ".gif", "GIF");
        Chk(Riff("WEBP"), AssetKind.Image, ".webp", "WEBP");
        Chk(new byte[] { 0x42, 0x4D, 0, 0 }, AssetKind.Image, ".bmp", "BMP");
        Chk(Riff("WAVE"), AssetKind.Audio, ".wav", "WAV");
        Chk(Riff("AVI "), AssetKind.Video, ".avi", "AVI");
        Chk(Encoding.ASCII.GetBytes("OggS----"), AssetKind.Audio, ".ogg", "OGG");
        Chk(Encoding.ASCII.GetBytes("fLaC----"), AssetKind.Audio, ".flac", "FLAC");
        Chk(Encoding.ASCII.GetBytes("ID3-----"), AssetKind.Audio, ".mp3", "MP3-ID3");
        Chk(new byte[] { 0xFF, 0xFB, 0, 0 }, AssetKind.Audio, ".mp3", "MP3-framesync");
        Chk(new byte[] { 0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p' }, AssetKind.Video, ".mp4", "MP4-ftyp");
        Chk(new byte[] { 0x1A, 0x45, 0xDF, 0xA3 }, AssetKind.Video, ".webm", "WEBM");
        Chk(new byte[] { 0x00, 0x01, 0x00, 0x00 }, AssetKind.Font, ".ttf", "TTF");
        Chk(Encoding.ASCII.GetBytes("OTTO----"), AssetKind.Font, ".otf", "OTF");
        Chk(Encoding.ASCII.GetBytes("wOF2----"), AssetKind.Font, ".woff2", "woff2");

        // HONESTIDADE: HTML e JSON NUNCA são classificados como asset
        Chk(Encoding.UTF8.GetBytes("<!DOCTYPE html><html>404 Not Found</html>"), AssetKind.Other, ".bin", "HTML→Other");
        Chk(Encoding.UTF8.GetBytes("{\"error\":\"forbidden\"}"), AssetKind.Other, ".bin", "JSON→Other");
    }
}
