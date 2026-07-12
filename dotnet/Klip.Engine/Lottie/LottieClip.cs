using System;
using System.Collections.Concurrent;
using System.IO;
using SkiaSharp;

namespace Klip.Engine.Lottie;

/// <summary>Cached facade: load a bodymovin .json once, render an animated frame at time t (seconds).</summary>
public static class LottieClip
{
    private static readonly ConcurrentDictionary<string, LottieDoc?> _cache = new();

    private static LottieDoc? Doc(string path) => _cache.GetOrAdd(path, p =>
    {
        try { return LottieLoader.Load(File.ReadAllBytes(p)); }
        catch { return null; }
    });

    public static bool Draw(SKCanvas canvas, SKRect dst, string path, double seconds)
    {
        var doc = Doc(path);
        if (doc is null) return false;
        double span = doc.Op - doc.Ip;
        double frame = doc.Ip + (span > 0 ? (seconds * doc.Fr) % span : 0);
        new LottieRenderer(doc).Render(canvas, dst, frame);
        return true;
    }

    public static (double w, double h, double seconds, double fps)? Info(string path)
    {
        var doc = Doc(path);
        if (doc is null) return null;
        return (doc.W, doc.H, doc.DurationSeconds, doc.Fr);
    }
}
