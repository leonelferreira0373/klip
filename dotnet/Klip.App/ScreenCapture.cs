using System;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace Klip.App;

/// <summary>
/// Captura GDI de uma região do ECRÃ (pixels físicos) → PNG. Lê do DC do ecrã, por isso apanha
/// conteúdo composto nativamente (WebView2/Chromium) que o Skia não consegue "render". É a base do
/// "a IA vê o que estás a ver".
/// </summary>
internal static class ScreenCapture
{
    private const int SRCCOPY = 0x00CC0020;
    private const int CAPTUREBLT = 0x40000000;   // inclui camadas (layered/GPU) na cópia

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr hdc, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint start, uint lines, byte[] bits, ref BITMAPINFO bi, uint usage);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO { public BITMAPINFOHEADER h; public int colors; }

    /// <summary>Captura o rectângulo (x,y,w,h) em pixels físicos do ecrã e devolve PNG.</summary>
    public static byte[] CaptureRectPng(int x, int y, int w, int h)
    {
        if (w <= 0 || h <= 0) throw new InvalidOperationException("região de captura inválida");
        var scr = GetDC(IntPtr.Zero);
        var mem = CreateCompatibleDC(scr);
        var bmp = CreateCompatibleBitmap(scr, w, h);
        var old = SelectObject(mem, bmp);
        var buf = new byte[w * h * 4];
        try
        {
            BitBlt(mem, 0, 0, w, h, scr, x, y, SRCCOPY | CAPTUREBLT);
            SelectObject(mem, old);   // desliga o bitmap do DC antes do GetDIBits
            var bi = new BITMAPINFO
            {
                h = new BITMAPINFOHEADER
                {
                    biSize = 40, biWidth = w, biHeight = -h,   // -h = top-down
                    biPlanes = 1, biBitCount = 32, biCompression = 0,
                }
            };
            GetDIBits(mem, bmp, 0, (uint)h, buf, ref bi, 0);
        }
        finally
        {
            DeleteObject(bmp); DeleteDC(mem); ReleaseDC(IntPtr.Zero, scr);
        }

        for (int i = 3; i < buf.Length; i += 4) buf[i] = 255;   // ecrã não tem alfa → força opaco

        using var bitmap = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque);
        Marshal.Copy(buf, 0, bitmap.GetPixels(), buf.Length);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 92);
        return data.ToArray();
    }
}
