using System;
using System.Numerics;
using Silk.NET.OpenGL;
using SkiaSharp;

namespace Klip.Engine.ThreeD;

/// <summary>Composites one frame: raw-GL 3D mesh pass → texture, then Skia paints the 2D background
/// and draws the (supersampled) mesh texture over it, downsampled for AA. Proven coexistence path.</summary>
public static class Scene3D
{
    public static SKImage RenderFrame(GpuSession gpu, MeshPass mesh, int compW, int compH,
        float[] mvp, float[] model, Vector3 light, Vector3 color, Vector3 cam,
        Action<SKCanvas, int, int> drawBackground)
    {
        var gr = gpu.GrContext;

        // 1. 3D mesh into its FBO (raw GL)
        mesh.Render(mvp, model, light, color, cam);

        // 2. hand GL state back to Skia
        gr.ResetContext();

        // 3. Skia: background + the mesh texture on top
        var info = new SKImageInfo(compW, compH, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(gr, budgeted: true, info)
            ?? throw new InvalidOperationException("skia surface create failed");
        var canvas = surface.Canvas;
        drawBackground(canvas, compW, compH);

        var glInfo = new GRGlTextureInfo((uint)GLEnum.Texture2D, mesh.ColorTexture, (uint)GLEnum.Rgba8);
        using (var backend = new GRBackendTexture(mesh.Width, mesh.Height, false, glInfo))
        using (var img = SKImage.FromTexture(gr, backend, GRSurfaceOrigin.BottomLeft,
                                             SKColorType.Rgba8888, SKAlphaType.Premul))
        {
            if (img is not null)
            {
                canvas.Save();
                canvas.Scale((float)compW / mesh.Width, (float)compH / mesh.Height);
                canvas.DrawImage(img, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
                canvas.Restore();
            }
        }

        canvas.Flush();
        gr.Flush();
        using var snap = surface.Snapshot();
        var raster = snap.ToRasterImage(true);
        gr.ResetContext();
        return raster;
    }
}
