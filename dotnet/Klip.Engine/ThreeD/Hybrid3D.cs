using System;
using System.Collections.Generic;
using System.Numerics;
using Klip.Model;
using Silk.NET.OpenGL;
using SkiaSharp;

namespace Klip.Engine.ThreeD;

/// <summary>
/// Compositor híbrido: camadas com <see cref="Extrude3D"/> renderizam AQUI (GL offscreen próprio,
/// extrude+bevel+Blinn-Phong) através da CÂMARA ANIMÁVEL do comp, e voltam como SKImage para o
/// pipeline 2D compor na ordem das camadas. Sem GPU (driver ausente) → devolve null e a camada
/// cai no desenho 2D normal.
/// </summary>
public static class Hybrid3D
{
    private const float PxToWorld = 1f / 220f;
    private const int SS = 2;                     // supersample p/ AA

    private static GpuSession? _gpu;
    private static bool _gpuFailed;
    private static MeshPass? _pass;
    private static int _pw, _ph;
    private static readonly Dictionary<string, (string key, float[] data, int count)> _meshCache = new();

    /// <summary>Motivo da falha GPU (diagnóstico via get_state). Null = ok.</summary>
    public static string? GpuError { get; private set; }

    // O contexto GL tem afinidade de thread — TODOS os renders 3D correm numa thread dedicada
    // (o preview vem da UI thread, o export de workers; sem isto o contexto corrompe).
    private static System.Collections.Concurrent.BlockingCollection<Action>? _queue;
    private static void EnsureThread()
    {
        if (_queue is not null) return;
        _queue = new();
        var th = new System.Threading.Thread(() =>
        {
            foreach (var job in _queue.GetConsumingEnumerable()) job();
        })
        { IsBackground = true, Name = "klip-3d" };
        th.Start();
    }

    private static SKImage? RunOn3DThread(Func<SKImage?> f)
    {
        EnsureThread();
        SKImage? result = null;
        Exception? error = null;
        using var done = new System.Threading.ManualResetEventSlim();
        _queue!.Add(() =>
        {
            try { result = f(); }
            catch (Exception e) { error = e; }
            finally { done.Set(); }
        });
        done.Wait();
        if (error is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        return result;
    }

    public static SKImage? Render(Comp comp, Layer layer, double t)
    {
        if (_gpuFailed || layer.ThreeD is null) return null;
        try
        {
            return RunOn3DThread(() => RenderCore(comp, layer, t));
        }
        catch (Exception ex)
        {
            _gpuFailed = true;
            GpuError = (GpuSession.PreloadNote ?? "sem preload") + " | " + ex;
            return null;
        }
    }

    private static SKImage? RenderCore(Comp comp, Layer layer, double t)
    {
        if (layer.ThreeD is not { } spec) return null;
        _gpu ??= new GpuSession();
        var gpu = _gpu;

        int w = comp.Width * SS, h = comp.Height * SS;
        if (_pass is null || _pw != w || _ph != h)
        {
            _pass?.Dispose();
            _pass = new MeshPass(gpu.Gl, Array.Empty<float>(), 0, w, h);
            _pw = w; _ph = h;
        }

        // mesh (cache por camada: shape+spec)
        string d = layer.Shape.Keys.Count > 0 ? layer.Shape.Keys[0].PathD : "";
        string key = $"{d.GetHashCode()}|{spec.Depth}|{spec.Bevel}";
        if (!_meshCache.TryGetValue(layer.Name, out var m) || m.key != key)
        {
            using var path = SKPath.ParseSvgPathData(d);
            if (path is null || path.IsEmpty) return null;
            var b = path.Bounds;
            float scale = PxToWorld;                    // px do canvas → unidades de mundo
            var data = Extruder.Build(path, scale, (float)spec.Depth, (float)spec.Bevel, out int count);
            m = (key, data, count);
            _meshCache[layer.Name] = m;
        }
        _pass.SetMesh(m.data, m.count);

        // câmara animável (defaults AE-like)
        var cam = comp.Camera;
        var eye = new Vector3(
            (float)(cam?.X?.Eval(t) ?? 0), (float)(cam?.Y?.Eval(t) ?? 0), (float)(cam?.Z?.Eval(t) ?? 5.2));
        var target = new Vector3(
            (float)(cam?.Tx?.Eval(t) ?? 0), (float)(cam?.Ty?.Eval(t) ?? 0), (float)(cam?.Tz?.Eval(t) ?? 0));
        float fov = (float)((cam?.Fov?.Eval(t) ?? 34.0) * Math.PI / 180.0);
        float[] view = Mat4.LookAt(eye, target, Vector3.UnitY);
        float[] proj = Mat4.Perspective(fov, (float)comp.Width / comp.Height, 0.1f, 100f);

        // modelo: escala da camada → rotação Y (track Rotation) → posição (px→mundo, Y invertido)
        float rotY = (float)((layer.Rotation?.Eval(t) ?? 0) * Math.PI / 180.0);
        float s = (float)(layer.Scale?.Eval(t) ?? 1.0);
        float px = (float)((layer.PosX?.Eval(t) ?? 0) * PxToWorld);
        float py = (float)(-(layer.PosY?.Eval(t) ?? 0) * PxToWorld);
        float[] model = Mat4.Multiply(Mat4.Translation(px, py, 0),
                        Mat4.Multiply(Mat4.RotationY(rotY), Mat4.Scale(s)));
        float[] mvp = Mat4.Multiply(proj, Mat4.Multiply(view, model));

        var light = Vector3.Normalize(new Vector3(-0.45f, -0.5f, -0.74f));
        uint f = layer.FillArgb;
        var color = new Vector3(((f >> 16) & 0xFF) / 255f, ((f >> 8) & 0xFF) / 255f, (f & 0xFF) / 255f);

        _pass.Render(mvp, model, light, color, eye);

        // readback direto (glReadPixels) — sem interop GRBackendTexture, robusto em single-file
        var gl = gpu.Gl;
        var pixels = new byte[w * h * 4];
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, _pass.Fbo);
        unsafe
        {
            fixed (byte* p = pixels)
                gl.ReadPixels(0, 0, (uint)w, (uint)h, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        }
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

        // GL lê bottom-up → inverter linhas
        var flipped = new byte[pixels.Length];
        int stride = w * 4;
        for (int row = 0; row < h; row++)
            System.Buffer.BlockCopy(pixels, (h - 1 - row) * stride, flipped, row * stride, stride);

        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        unsafe
        {
            fixed (byte* p = flipped)
                return SKImage.FromPixelCopy(info, (IntPtr)p, stride);
        }
    }
}
