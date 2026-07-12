using System;
using System.Collections.Concurrent;
using System.IO;
using SkiaSharp;

namespace Klip.Engine.Rive;

/// <summary>Cached facade: load a .riv once, render an animated frame at time t on demand.</summary>
public static class RiveClip
{
    private static readonly ConcurrentDictionary<string, RiveDocument?> _cache = new();

    private static RiveDocument? Doc(string path) => _cache.GetOrAdd(path, p =>
    {
        try { return RiveLoader.Load(File.ReadAllBytes(p)); }
        catch { return null; }
    });

    /// <summary>Draw the .riv's animation at time t (seconds) into dst. Returns false if unloadable.</summary>
    public static bool Draw(SKCanvas canvas, SKRect dst, string path, string? animName, double t)
    {
        var doc = Doc(path);
        var ab = doc?.First;
        if (ab is null) return false;
        var player = new RivePlayer(ab);
        var anim = player.Find(animName);
        if (anim is not null) player.Apply(anim, t);
        new RiveRenderer(ab).Render(canvas, dst);
        return true;
    }

    public static (double w, double h, string[] anims)? Info(string path)
    {
        var ab = Doc(path)?.First;
        if (ab is null) return null;
        var names = new string[ab.Animations.Count];
        for (int i = 0; i < names.Length; i++) names[i] = ab.Animations[i].Name;
        return (ab.Width, ab.Height, names);
    }
}
