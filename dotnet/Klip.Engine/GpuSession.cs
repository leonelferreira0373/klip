using System;
using Klip.Model;
using Silk.NET.Core.Contexts;
using Silk.NET.GLFW;
using Silk.NET.OpenGL;
using SkiaSharp;

namespace Klip.Engine;

/// <summary>
/// A real GPU render path: a hidden GLFW OpenGL context feeding a Skia <c>GRContext</c>.
/// Renders comps onto GPU-backed surfaces (same DrawComp code as CPU), then reads back for
/// export. This is the seed of realtime 60fps playback + fast export.
/// </summary>
public sealed unsafe class GpuSession : IDisposable
{
    private readonly Glfw _glfw;
    private readonly WindowHandle* _window;
    private readonly GRContext _gr;
    private readonly GL _gl;

    public GpuSession()
    {
        // single-file: o loader do Silk.NET não conhece o dir de extração dos nativos.
        // O resolver do RUNTIME conhece → TryLoad no contexto do assembly; depois o load
        // por-nome do Silk acerta no módulo já carregado no processo.
        if (System.Runtime.InteropServices.NativeLibrary.TryLoad(
                "glfw3.dll", typeof(GpuSession).Assembly, null, out _))
        {
            PreloadNote = "glfw3 preloaded (assembly resolver)";
        }
        else
        {
            // fallback: procurar no dir de extração do single-file (%TEMP%\.net\<app>\*)
            try
            {
                var exe = System.IO.Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "app");
                var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), ".net", exe);
                if (System.IO.Directory.Exists(root))
                    foreach (var dir in System.IO.Directory.EnumerateDirectories(root))
                    {
                        var cand = System.IO.Path.Combine(dir, "glfw3.dll");
                        if (System.IO.File.Exists(cand))
                        {
                            System.Runtime.InteropServices.NativeLibrary.Load(cand);
                            PreloadNote = "glfw3 preloaded: " + cand;
                            break;
                        }
                    }
            }
            catch (Exception ex) { PreloadNote = "preload falhou: " + ex.Message; }
        }

        _glfw = Glfw.GetApi();
        if (!_glfw.Init())
            throw new InvalidOperationException("GLFW init failed");

        _glfw.WindowHint(WindowHintBool.Visible, false);          // offscreen — we render to an FBO, not the window
        _glfw.WindowHint(WindowHintBool.DoubleBuffer, false);

        _window = _glfw.CreateWindow(1, 1, "klip-gpu", null, null);
        if (_window == null)
            throw new InvalidOperationException("GLFW context/window creation failed (no GL driver?)");
        _glfw.MakeContextCurrent(_window);

        // raw GL for the bespoke 3D pass — SAME context Skia will use
        _gl = GL.GetApi(new LamdaNativeContext(name => _glfw.GetProcAddress(name)));

        var glInterface = GRGlInterface.Create()
            ?? throw new InvalidOperationException("GRGlInterface.Create failed (GL not current?)");
        _gr = GRContext.CreateGl(glInterface)
            ?? throw new InvalidOperationException("GRContext.CreateGl failed");
    }

    public string Backend => "OpenGL / GRContext";

    /// <summary>Diagnóstico do preload do nativo GLFW (single-file).</summary>
    public static string? PreloadNote { get; private set; }

    /// <summary>Raw OpenGL API on the shared context (for the bespoke 3D mesh pass).</summary>
    public GL Gl => _gl;

    /// <summary>Skia's GPU context on the SAME GL context. Call ResetContext() after raw-GL draws.</summary>
    public GRContext GrContext => _gr;

    private SKSurface? _surface;
    private int _sw, _sh;

    private SKSurface Surface(Comp comp)
    {
        if (_surface is null || _sw != comp.Width || _sh != comp.Height)
        {
            _surface?.Dispose();
            var info = new SKImageInfo(comp.Width, comp.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            _surface = SKSurface.Create(_gr, budgeted: true, info)
                ?? throw new InvalidOperationException("GPU SKSurface.Create failed");
            _sw = comp.Width; _sh = comp.Height;
        }
        return _surface;
    }

    /// <summary>Playback path: render on the GPU, no read-back (what draws to the screen).</summary>
    public void RenderNoReadback(Comp comp, double t)
    {
        var surface = Surface(comp);
        Renderer.DrawComp(surface.Canvas, comp, t);
        surface.Canvas.Flush();
        _gr.Flush();
    }

    /// <summary>Export path: render on the GPU and read it back to a raster image (needed for the encoder).</summary>
    public SKImage RenderFrame(Comp comp, double t)
    {
        var surface = Surface(comp);
        Renderer.DrawComp(surface.Canvas, comp, t);
        surface.Canvas.Flush();
        _gr.Flush();
        using var gpuImg = surface.Snapshot();
        return gpuImg.ToRasterImage(ensurePixelData: true);   // GPU -> CPU copy
    }

    public void Dispose()
    {
        _surface?.Dispose();
        _gr?.Dispose();
        if (_window != null) _glfw.DestroyWindow(_window);
        _glfw.Terminate();
    }
}
