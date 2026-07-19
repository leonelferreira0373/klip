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
    private readonly uint _prog, _vao, _vbo, _fbo, _colorTex, _depthRb, _env;
    private int _vertCount;
    private readonly int _w, _h;
    private readonly float _envMaxLod;
    private readonly int _locMVP, _locModel, _locLight, _locColor, _locCam, _locRough, _locMetal, _locEnv, _locEnvLod;
    private readonly int _locFront, _locBack, _locUseTex, _locEdge;

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
        _locRough = gl.GetUniformLocation(_prog, "uRough");
        _locMetal = gl.GetUniformLocation(_prog, "uMetal");
        _locEnv = gl.GetUniformLocation(_prog, "uEnv");
        _locEnvLod = gl.GetUniformLocation(_prog, "uEnvMaxLod");
        _locFront = gl.GetUniformLocation(_prog, "uFront");
        _locBack = gl.GetUniformLocation(_prog, "uBack");
        _locUseTex = gl.GetUniformLocation(_prog, "uUseTex");
        _locEdge = gl.GetUniformLocation(_prog, "uEdge");
        (_env, _envMaxLod) = IblEnv.Create(gl);   // estúdio procedural p/ IBL

        _vao = gl.GenVertexArray(); gl.BindVertexArray(_vao);
        _vbo = gl.GenBuffer(); gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* p = interleaved)
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(interleaved.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 8 * sizeof(float), (void*)0);
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 8 * sizeof(float), (void*)(3 * sizeof(float)));
        gl.EnableVertexAttribArray(2);
        gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, 8 * sizeof(float), (void*)(6 * sizeof(float)));

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

    public void Render(float[] mvp, float[] model, Vector3 lightDir, Vector3 color, Vector3 camPos,
                       float rough = 0.25f, float metal = 0.85f,
                       uint frontTex = 0, uint backTex = 0, bool useTex = false, Vector3? edge = null)
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
        _gl.Uniform1(_locRough, rough);
        _gl.Uniform1(_locMetal, metal);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _env);
        _gl.Uniform1(_locEnv, 0);
        _gl.Uniform1(_locEnvLod, _envMaxLod);
        // texturas de face (frente/verso) — só quando a camada as define
        _gl.Uniform1(_locUseTex, useTex ? 1 : 0);
        if (useTex)
        {
            var e = edge ?? new Vector3(0.93f, 0.93f, 0.93f);
            _gl.Uniform3(_locEdge, e.X, e.Y, e.Z);
            _gl.ActiveTexture(TextureUnit.Texture1);
            _gl.BindTexture(TextureTarget.Texture2D, frontTex);
            _gl.Uniform1(_locFront, 1);
            _gl.ActiveTexture(TextureUnit.Texture2);
            _gl.BindTexture(TextureTarget.Texture2D, backTex != 0 ? backTex : frontTex);
            _gl.Uniform1(_locBack, 2);
        }
        _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)_vertCount);

        _gl.Disable(EnableCap.DepthTest);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.Flush();
    }

    private const string Vs = @"#version 330 core
layout(location=0) in vec3 aPos;
layout(location=1) in vec3 aNormal;
layout(location=2) in vec2 aUV;
uniform mat4 uMVP; uniform mat4 uModel;
out vec3 vN; out vec3 vW; out vec2 vUV; out float vFaceZ;
void main(){
  gl_Position = uMVP * vec4(aPos,1.0);
  vW = (uModel*vec4(aPos,1.0)).xyz;
  vN = mat3(uModel)*aNormal;
  vUV = aUV;
  vFaceZ = aNormal.z;                 // normal em ESPAÇO-OBJETO → classifica frente/verso/borda
}";

    // PBR (Cook-Torrance/GGX, metallic-roughness) + IBL REAL (estúdio procedural
    // equiretangular: reflexo especular via reflect()+mip por roughness, difusa via
    // irradiância) + 1 key light analítica p/ glint nítido + ACES + gama sRGB.
    private const string Fs = @"#version 330 core
in vec3 vN; in vec3 vW; in vec2 vUV; in float vFaceZ; out vec4 o;
uniform vec3 uLightDir; uniform vec3 uColor; uniform vec3 uCamPos;
uniform float uRough; uniform float uMetal;
uniform sampler2D uEnv; uniform float uEnvMaxLod;
uniform sampler2D uFront; uniform sampler2D uBack; uniform int uUseTex; uniform vec3 uEdge;
const float PI = 3.14159265359;

float D_GGX(float NoH, float a){ float a2=a*a; float d=(NoH*NoH)*(a2-1.0)+1.0; return a2/(PI*d*d); }
float G1(float NoX, float k){ return NoX/(NoX*(1.0-k)+k); }
float G_Smith(float NoV, float NoL, float r){ float k=(r+1.0); k=k*k/8.0; return G1(NoV,k)*G1(NoL,k); }
vec3  F_Schlick(float VoH, vec3 F0){ return F0 + (1.0-F0)*pow(clamp(1.0-VoH,0.0,1.0),5.0); }

vec3 lobe(vec3 N, vec3 V, vec3 L, vec3 albedo, float rough, float metal, vec3 F0, vec3 radiance){
  vec3 H=normalize(V+L);
  float NoL=max(dot(N,L),0.0), NoV=max(dot(N,V),1e-4), NoH=max(dot(N,H),0.0), VoH=max(dot(V,H),0.0);
  if(NoL<=0.0) return vec3(0.0);
  float D=D_GGX(NoH, max(rough*rough,1e-3));
  float G=G_Smith(NoV,NoL,rough);
  vec3  F=F_Schlick(VoH,F0);
  vec3 spec=(D*G)*F/max(4.0*NoV*NoL,1e-4);
  vec3 kd=(1.0-F)*(1.0-metal);
  return (kd*albedo/PI + spec)*radiance*NoL;
}

vec3 aces(vec3 x){ return clamp((x*(2.51*x+0.03))/(x*(2.43*x+0.59)+0.14),0.0,1.0); }

// direção -> UV equiretangular (u=azimute, v=elevação)
const vec2 INV_ATAN = vec2(0.1591, 0.3183);
vec2 dirToEquirect(vec3 d){
  vec2 uv = vec2(atan(d.z, d.x), asin(clamp(d.y,-1.0,1.0)));
  uv *= INV_ATAN; uv += 0.5; return uv;
}
vec3 sampleEnv(vec3 d, float lod){ return textureLod(uEnv, dirToEquirect(d), lod).rgb; }

// BRDF de ambiente analítica (Karis, aprox. mobile) — evita a LUT pré-integrada
vec3 envBRDFApprox(vec3 F0, float rough, float NoV){
  const vec4 c0 = vec4(-1.0,-0.0275,-0.572,0.022);
  const vec4 c1 = vec4( 1.0, 0.0425, 1.04,-0.04);
  vec4 r = rough*c0 + c1;
  float a004 = min(r.x*r.x, exp2(-9.28*NoV))*r.x + r.y;
  vec2 ab = vec2(-1.04,1.04)*a004 + r.zw;
  return F0*ab.x + ab.y;
}

void main(){
  vec3 N = normalize(vN);
  vec3 V = normalize(uCamPos - vW);
  if (dot(N,V) < 0.0) N = -N;                       // two-sided
  vec3 srgb = uColor;                               // cor sólida por defeito
  if (uUseTex == 1) {                               // textura de face por normal-z de objeto
    if (vFaceZ > 0.5)       srgb = texture(uFront, vUV).rgb;   // frente = arte
    else if (vFaceZ < -0.5) srgb = texture(uBack,  vUV).rgb;   // verso = arte
    else                    srgb = uEdge;                      // borda = núcleo de papel
  }
  vec3 albedo = pow(max(srgb,0.0), vec3(2.2));      // sRGB -> linear
  float rough = clamp(uRough,0.04,1.0);
  float metal = clamp(uMetal,0.0,1.0);
  vec3 F0 = mix(vec3(0.04), albedo, metal);
  float NoV = max(dot(N,V),1e-4);

  vec3 col = vec3(0.0);

  // key light analítica: um glint direto e nítido por cima do IBL
  vec3 keyL = normalize(-uLightDir);
  col += lobe(N,V,keyL, albedo,rough,metal,F0, vec3(2.0));

  // ---- IBL (estúdio procedural) ----
  // especular: amostra o ambiente ao longo do vetor de reflexão; mip = roughness (reflexo mais desfocado)
  vec3 R = reflect(-V, N);
  vec3 prefiltered = sampleEnv(R, rough*uEnvMaxLod);
  vec3 iblSpec = prefiltered * envBRDFApprox(F0, rough, NoV);
  // difusa: irradiância ~ ambiente muito desfocado amostrado na normal
  vec3 irradiance = sampleEnv(N, uEnvMaxLod - 1.0);
  vec3 iblDiff = irradiance * albedo * (1.0 - metal);
  col += iblSpec + iblDiff;

  col = aces(col);
  col = pow(col, vec3(1.0/2.2));                    // linear -> sRGB
  o = vec4(col, 1.0);
}";

    public void Dispose()
    {
        _gl.DeleteFramebuffer(_fbo);
        _gl.DeleteRenderbuffer(_depthRb);
        _gl.DeleteTexture(_colorTex);
        _gl.DeleteTexture(_env);
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteVertexArray(_vao);
        _gl.DeleteProgram(_prog);
    }
}
