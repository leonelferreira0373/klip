using System;
using Silk.NET.OpenGL;
using SkiaSharp;

namespace Klip.Engine.ThreeD;

/// <summary>
/// Coexistence spike: prove raw OpenGL and Skia share ONE GL context cleanly.
/// Renders a raw-GL triangle into our own FBO texture, hands state back to Skia
/// (GRContext.ResetContext), then composites: a Skia 2D shape + the GL texture on top,
/// with the correct Y-flip (BottomLeft origin). Read back → SKImage.
/// This de-risks the whole 3D pass before any mesh/bevel/camera code.
/// </summary>
public static class GlComposite
{
    public static unsafe SKImage RenderSpike(GpuSession gpu, int w, int h)
    {
        var gl = gpu.Gl;
        var gr = gpu.GrContext;

        // ---- 1. raw GL: render a triangle into our own FBO/texture ----
        uint tex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, tex);
        gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba8, (uint)w, (uint)h, 0,
                      PixelFormat.Rgba, PixelType.UnsignedByte, (void*)0);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);

        uint fbo = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                                TextureTarget.Texture2D, tex, 0);
        if (gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != GLEnum.FramebufferComplete)
            throw new InvalidOperationException("FBO incomplete");

        gl.Viewport(0, 0, (uint)w, (uint)h);
        gl.ClearColor(0f, 0f, 0f, 0f);
        gl.Clear((uint)ClearBufferMask.ColorBufferBit);

        uint prog = BuildProgram(gl);
        gl.UseProgram(prog);

        float[] verts = { 0f, 0.7f, -0.7f, -0.6f, 0.7f, -0.6f };
        uint vao = gl.GenVertexArray();
        gl.BindVertexArray(vao);
        uint vbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        fixed (float* v = verts)
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(verts.Length * sizeof(float)), v, BufferUsageARB.StaticDraw);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0);
        gl.DrawArrays(PrimitiveType.Triangles, 0, 3);

        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        gl.Flush();

        // ---- 2. hand GL state back to Skia ----
        gr.ResetContext();

        // ---- 3. Skia composite: 2D shape + the GL texture over it ----
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(gr, budgeted: true, info)
            ?? throw new InvalidOperationException("skia surface create failed");
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(0xFFF6F7FB));
        using (var p = new SKPaint { IsAntialias = true, Color = new SKColor(0xFFE0245E) })
            canvas.DrawCircle(w * 0.34f, h * 0.5f, w * 0.17f, p);

        var glInfo = new GRGlTextureInfo((uint)GLEnum.Texture2D, tex, (uint)GLEnum.Rgba8);
        using (var backend = new GRBackendTexture(w, h, false, glInfo))
        using (var glImage = SKImage.FromTexture(gr, backend, GRSurfaceOrigin.BottomLeft,
                                                 SKColorType.Rgba8888, SKAlphaType.Premul))
        {
            if (glImage is not null) canvas.DrawImage(glImage, 0, 0);
        }

        canvas.Flush();
        gr.Flush();
        using var snap = surface.Snapshot();
        var raster = snap.ToRasterImage(true);

        // ---- cleanup ----
        gr.ResetContext();
        gl.DeleteBuffer(vbo);
        gl.DeleteVertexArray(vao);
        gl.DeleteProgram(prog);
        gl.DeleteFramebuffer(fbo);
        gl.DeleteTexture(tex);
        return raster;
    }

    private static uint BuildProgram(GL gl)
    {
        const string vs = "#version 330 core\nlayout(location=0) in vec2 p;\nvoid main(){ gl_Position = vec4(p,0.0,1.0); }";
        const string fs = "#version 330 core\nout vec4 c;\nvoid main(){ c = vec4(0.17,0.55,1.0,1.0); }";
        uint v = Compile(gl, ShaderType.VertexShader, vs);
        uint f = Compile(gl, ShaderType.FragmentShader, fs);
        uint prog = gl.CreateProgram();
        gl.AttachShader(prog, v);
        gl.AttachShader(prog, f);
        gl.LinkProgram(prog);
        gl.GetProgram(prog, ProgramPropertyARB.LinkStatus, out int ok);
        if (ok == 0) throw new InvalidOperationException("link: " + gl.GetProgramInfoLog(prog));
        gl.DeleteShader(v);
        gl.DeleteShader(f);
        return prog;
    }

    private static uint Compile(GL gl, ShaderType type, string src)
    {
        uint s = gl.CreateShader(type);
        gl.ShaderSource(s, src);
        gl.CompileShader(s);
        gl.GetShader(s, ShaderParameterName.CompileStatus, out int ok);
        if (ok == 0) throw new InvalidOperationException($"{type}: " + gl.GetShaderInfoLog(s));
        return s;
    }
}
