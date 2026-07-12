using System;
using System.Numerics;
using Silk.NET.OpenGL;

namespace Klip.Engine.ThreeD;

/// <summary>
/// The GPU 3D pass: uploads a static mesh once, renders it lit (Blinn-Phong, two-sided) through a
/// camera into an offscreen color+depth FBO. The color texture is handed to Skia for compositing.
/// Supersampled (render at 2x) → Skia downsamples for anti-aliasing.
/// </summary>
public sealed unsafe class MeshPass : IDisposable
{
    private readonly GL _gl;
    private readonly uint _prog, _vao, _vbo, _fbo, _colorTex, _depthRb;
    private int _vertCount;
    private readonly int _w, _h;
    private readonly int _locMVP, _locModel, _locLight, _locColor, _locCam;

    public uint ColorTexture => _colorTex;
    public uint Fbo => _fbo;
    public int Width => _w;
    public int Height => _h;

    public MeshPass(GL gl, float[] interleaved, int vertCount, int w, int h)
    {
        _gl = gl; _vertCount = vertCount; _w = w; _h = h;
        _prog = GlUtil.Program(gl, Vs, Fs);
        _locMVP = gl.GetUniformLocation(_prog, "uMVP");
        _locModel = gl.GetUniformLocation(_prog, "uModel");
        _locLight = gl.GetUniformLocation(_prog, "uLightDir");
        _locColor = gl.GetUniformLocation(_prog, "uColor");
        _locCam = gl.GetUniformLocation(_prog, "uCamPos");

        _vao = gl.GenVertexArray(); gl.BindVertexArray(_vao);
        _vbo = gl.GenBuffer(); gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* p = interleaved)
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(interleaved.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), (void*)0);
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), (void*)(3 * sizeof(float)));

        _colorTex = gl.GenTexture(); gl.BindTexture(TextureTarget.Texture2D, _colorTex);
        gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba8, (uint)w, (uint)h, 0,
                      PixelFormat.Rgba, PixelType.UnsignedByte, (void*)0);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);

        _depthRb = gl.GenRenderbuffer(); gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depthRb);
        gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24, (uint)w, (uint)h);

        _fbo = gl.GenFramebuffer(); gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _colorTex, 0);
        gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, _depthRb);
        if (gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != GLEnum.FramebufferComplete)
            throw new InvalidOperationException("3D FBO incomplete");
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    /// <summary>Substitui a mesh carregada (hybrid compositor: uma pass, N camadas 3D).</summary>
    public unsafe void SetMesh(float[] interleaved, int vertCount)
    {
        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* p = interleaved)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(interleaved.Length * sizeof(float)), p, BufferUsageARB.DynamicDraw);
        _vertCount = vertCount;
    }

    public void Render(float[] mvp, float[] model, Vector3 lightDir, Vector3 color, Vector3 camPos)
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        _gl.Viewport(0, 0, (uint)_w, (uint)_h);
        _gl.Enable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.CullFace);
        _gl.ClearColor(0f, 0f, 0f, 0f);
        _gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));

        _gl.UseProgram(_prog);
        _gl.BindVertexArray(_vao);
        fixed (float* m = mvp) _gl.UniformMatrix4(_locMVP, 1, false, m);
        fixed (float* m = model) _gl.UniformMatrix4(_locModel, 1, false, m);
        _gl.Uniform3(_locLight, lightDir.X, lightDir.Y, lightDir.Z);
        _gl.Uniform3(_locColor, color.X, color.Y, color.Z);
        _gl.Uniform3(_locCam, camPos.X, camPos.Y, camPos.Z);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)_vertCount);

        _gl.Disable(EnableCap.DepthTest);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.Flush();
    }

    private const string Vs = @"#version 330 core
layout(location=0) in vec3 aPos;
layout(location=1) in vec3 aNormal;
uniform mat4 uMVP; uniform mat4 uModel;
out vec3 vN; out vec3 vW;
void main(){ gl_Position = uMVP * vec4(aPos,1.0); vW = (uModel*vec4(aPos,1.0)).xyz; vN = mat3(uModel)*aNormal; }";

    private const string Fs = @"#version 330 core
in vec3 vN; in vec3 vW; out vec4 o;
uniform vec3 uLightDir; uniform vec3 uColor; uniform vec3 uCamPos;
void main(){
  vec3 N = normalize(vN);
  vec3 V = normalize(uCamPos - vW);
  if (dot(N,V) < 0.0) N = -N;            // two-sided
  vec3 L = normalize(-uLightDir);
  float diff = max(dot(N,L), 0.0);
  vec3 H = normalize(L+V);
  float spec = pow(max(dot(N,H),0.0), 48.0);
  vec3 col = uColor*(0.38 + 0.72*diff) + vec3(1.0)*spec*0.7;
  o = vec4(col, 1.0);
}";

    public void Dispose()
    {
        _gl.DeleteFramebuffer(_fbo);
        _gl.DeleteRenderbuffer(_depthRb);
        _gl.DeleteTexture(_colorTex);
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteVertexArray(_vao);
        _gl.DeleteProgram(_prog);
    }
}
