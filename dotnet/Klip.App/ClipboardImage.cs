using System;
using System.Runtime.InteropServices;

namespace Klip.App;

/// <summary>Lê uma imagem do clipboard do Windows (CF_DIB) e devolve PNG. Ideal p/ colar screenshots.
/// Constrói um .bmp em memória (prepende BITMAPFILEHEADER ao DIB) e decodifica via SkiaSharp.</summary>
public static class ClipboardImage
{
    private const uint CF_DIB = 8, CF_DIBV5 = 17;

    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint uFormat);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr hMem);

    public static byte[]? TryGetPng()
    {
        if (!OpenClipboard(IntPtr.Zero)) return null;
        try
        {
            uint fmt = IsClipboardFormatAvailable(CF_DIB) ? CF_DIB
                     : IsClipboardFormatAvailable(CF_DIBV5) ? CF_DIBV5 : 0;
            if (fmt == 0) return null;
            IntPtr h = GetClipboardData(fmt);
            if (h == IntPtr.Zero) return null;
            IntPtr ptr = GlobalLock(h);
            if (ptr == IntPtr.Zero) return null;
            try
            {
                int size = (int)GlobalSize(h);
                if (size < 40) return null;
                var dib = new byte[size];
                Marshal.Copy(ptr, dib, 0, size);

                int biSize = BitConverter.ToInt32(dib, 0);
                short bpp = BitConverter.ToInt16(dib, 14);
                int clrUsed = BitConverter.ToInt32(dib, 32);
                int paletteEntries = bpp <= 8 ? (clrUsed != 0 ? clrUsed : (1 << bpp)) : 0;
                // bitfields (BI_BITFIELDS=3) adiciona 3 máscaras (12 bytes) para 16/32bpp
                int compression = BitConverter.ToInt32(dib, 16);
                int masks = (compression == 3 && biSize == 40) ? 12 : 0;
                int pixelOffset = 14 + biSize + masks + paletteEntries * 4;

                var bmp = new byte[14 + size];
                bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
                BitConverter.GetBytes(14 + size).CopyTo(bmp, 2);   // file size
                BitConverter.GetBytes(pixelOffset).CopyTo(bmp, 10); // offset to pixels
                Array.Copy(dib, 0, bmp, 14, size);

                using var sk = SkiaSharp.SKBitmap.Decode(bmp);
                if (sk is null) return null;
                using var img = SkiaSharp.SKImage.FromBitmap(sk);
                using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                return data.ToArray();
            }
            finally { GlobalUnlock(h); }
        }
        catch { return null; }
        finally { CloseClipboard(); }
    }
}
