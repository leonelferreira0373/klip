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
    // Uma camada pode ter VÁRIAS partes (uma por material do objeto original).
    private static readonly Dictionary<string, (string key, IReadOnlyList<GltfMesh.Part> parts)> _meshCache = new();
    private static readonly Dictionary<string, uint> _texCache = new();   // path → GL texture (face do produto)

    /// <summary>Carrega uma imagem (png/jpg) para textura GL sRGB c/ mipmaps (cache por caminho). Corre na thread klip-3d.</summary>
    private static unsafe uint LoadTexture(GL gl, string path)
    {
        if (_texCache.TryGetValue(path, out var t)) return t;
        using var bmp0 = SKBitmap.Decode(path);
        if (bmp0 is null) { _texCache[path] = 0; return 0; }
        // garantir RGBA8888 (ordem de canais previsível p/ o upload GL)
        SKBitmap bmp = bmp0;
        if (bmp0.ColorType != SKColorType.Rgba8888)
        {
            bmp = new SKBitmap(new SKImageInfo(bmp0.Width, bmp0.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
            bmp0.CopyTo(bmp, SKColorType.Rgba8888);
        }
        uint tex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, tex);
        fixed (byte* p = bmp.GetPixelSpan())
            gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba8, (uint)bmp.Width, (uint)bmp.Height, 0,
                          PixelFormat.Rgba, PixelType.UnsignedByte, p);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.LinearMipmapLinear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        gl.GenerateMipmap(TextureTarget.Texture2D);
        if (!ReferenceEquals(bmp, bmp0)) bmp.Dispose();
        _texCache[path] = tex;
        return tex;
    }

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

    /// <param name="outScale">escala da SAÍDA (1 no preview, ~2 em 2K, ~4 em 4K). O 3D passa a ser
    /// renderizado nessa resolução em vez de ser esticado a partir da do comp.</param>
    public static SKImage? Render(Comp comp, Layer layer, double t, float outScale = 1f)
    {
        if (_gpuFailed || layer.ThreeD is null) return null;
        try
        {
            return RunOn3DThread(() => RenderCore(comp, layer, t, outScale));
        }
        catch (Exception ex)
        {
            _gpuFailed = true;
            GpuError = (GpuSession.PreloadNote ?? "sem preload") + " | " + ex;
            return null;
        }
    }

    private static SKImage? RenderCore(Comp comp, Layer layer, double t, float outScale)
    {
        if (layer.ThreeD is not { } spec) return null;
        _gpu ??= new GpuSession();
        var gpu = _gpu;

        // RESOLUÇÃO DO 3D SEGUE A DA SAÍDA. Antes era sempre comp*SS: ao exportar em 4K, o 3D
        // vinha de 2K e era ESTICADO — era exatamente daí que vinham os dentes/o aspeto "choppy".
        // Multiplicador limitado a 4x o comp para não criar FBOs absurdos (memória de vídeo).
        float mult = Math.Clamp(outScale * SS, SS, 4f);
        int w = (int)MathF.Ceiling(comp.Width * mult), h = (int)MathF.Ceiling(comp.Height * mult);
        if (_pass is null || _pw != w || _ph != h)
        {
            _pass?.Dispose();
            _pass = new MeshPass(gpu.Gl, Array.Empty<float>(), 0, w, h);
            _pw = w; _ph = h;
        }

        // mesh (cache por camada: shape+spec)
        string d = layer.Shape.Keys.Count > 0 ? layer.Shape.Keys[0].PathD : "";
        bool useMesh = spec.MeshPath is { Length: > 0 } && System.IO.File.Exists(spec.MeshPath);
        string key = useMesh
            ? "obj|" + spec.MeshPath + "|" + System.IO.File.GetLastWriteTimeUtc(spec.MeshPath!).Ticks
            : $"{d.GetHashCode()}|{spec.Depth}|{spec.Bevel}";
        if (!_meshCache.TryGetValue(layer.Name, out var m) || m.key != key)
        {
            if (useMesh)
            {
                // OBJETO REAL importado — nada de extrusão. A malha já vem normalizada p/ 1 unidade.
                // .glb traz OS MATERIAIS junto, um por parte (sola vs tecido vs metal), com as
                // texturas extraídas; .obj é o caminho pobre, fica como reserva.
                var ext = System.IO.Path.GetExtension(spec.MeshPath!).ToLowerInvariant();
                IReadOnlyList<GltfMesh.Part> parts0;
                if (ext is ".glb" or ".gltf") parts0 = GltfMesh.LoadParts(spec.MeshPath!);
                else
                {
                    var o = ObjMesh.Load(spec.MeshPath!);
                    parts0 = new[] { new GltfMesh.Part(o.data, o.count, new GltfMesh.Pbr(0xFFBDBDC6, 0f, 0.5f), null) };
                }
                if (parts0.Count == 0 || parts0[0].Count == 0) return null;
                m = (key, parts0);
            }
            else
            {
                using var path = SKPath.ParseSvgPathData(d);
                if (path is null || path.IsEmpty) return null;
                float scale = PxToWorld;                    // px do canvas → unidades de mundo
                var data = Extruder.Build(path, scale, (float)spec.Depth, (float)spec.Bevel, out int count);
                m = (key, new[] { new GltfMesh.Part(data, count, new GltfMesh.Pbr(layer.FillArgb, 0f, 0.5f), null) });
            }
            _meshCache[layer.Name] = m;
        }

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
        const double Deg = Math.PI / 180.0;
        float rotY = (float)((layer.Rotation?.Eval(t) ?? 0) * Deg);   // turn (yaw)
        float rotX = (float)((layer.RotationX?.Eval(t) ?? 0) * Deg);  // pitch (inclinar)
        float rotZ = (float)((layer.RotationZ?.Eval(t) ?? 0) * Deg);  // roll (leque)
        float s = (float)(layer.Scale?.Eval(t) ?? 1.0);
        float px = (float)((layer.PosX?.Eval(t) ?? 0) * PxToWorld);
        float py = (float)(-(layer.PosY?.Eval(t) ?? 0) * PxToWorld);
        float pz = (float)((layer.PosZ?.Eval(t) ?? 0) * PxToWorld);
        var rot = Mat4.Multiply(Mat4.RotationZ(rotZ), Mat4.Multiply(Mat4.RotationY(rotY), Mat4.RotationX(rotX)));
        float[] model = Mat4.Multiply(Mat4.Translation(px, py, pz), Mat4.Multiply(rot, Mat4.Scale(s)));
        float[] mvp = Mat4.Multiply(proj, Mat4.Multiply(view, model));

        var light = Vector3.Normalize(new Vector3(-0.45f, -0.5f, -0.74f));
        uint f = layer.FillArgb;
        var color = new Vector3(((f >> 16) & 0xFF) / 255f, ((f >> 8) & 0xFF) / 255f, (f & 0xFF) / 255f);

        uint frontTex = 0, backTex = 0; bool useTex = false; Vector3? edge = null;
        if (spec.FrontTex is { Length: > 0 } fp)
        {
            frontTex = LoadTexture(gpu.Gl, fp);
            if (frontTex != 0)
            {
                useTex = true;
                if (spec.BackTex is { Length: > 0 } bp) backTex = LoadTexture(gpu.Gl, bp);
                uint ec = spec.EdgeArgb;
                edge = new Vector3(((ec >> 16) & 0xFF) / 255f, ((ec >> 8) & 0xFF) / 255f, (ec & 0xFF) / 255f);
            }
        }

        // UMA PASSAGEM POR MATERIAL. Só a primeira limpa o buffer; as seguintes desenham por cima e
        // o teste de profundidade resolve a oclusão. Com uma parte só, os sliders do painel continuam
        // a mandar (é o comportamento de sempre); com várias, cada parte usa o SEU material — é o que
        // faz um objeto importado deixar de chegar todo da mesma cor.
        var parts = m.parts;
        bool perPart = parts.Count > 1 || (parts.Count == 1 && parts[0].BaseTex is { Length: > 0 });
        for (int i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            if (part.Count == 0) continue;
            _pass.SetMesh(part.Data, part.Count);

            var pc = color; float pr = (float)spec.Rough, pm = (float)spec.Metal;
            uint pTex = frontTex, pBack = backTex; bool pUse = useTex, pTexAll = false;
            if (perPart)
            {
                uint b = part.Material.BaseArgb;
                pc = new Vector3(((b >> 16) & 0xFF) / 255f, ((b >> 8) & 0xFF) / 255f, (b & 0xFF) / 255f);
                pr = part.Material.Rough; pm = part.Material.Metal;
                if (part.BaseTex is { Length: > 0 } bt)
                {
                    uint tx = LoadTexture(gpu.Gl, bt);
                    if (tx != 0) { pTex = tx; pBack = tx; pUse = true; pTexAll = true; }
                    else pUse = false;
                }
                else pUse = false;
            }
            _pass.Render(mvp, model, light, pc, eye, pr, pm, pTex, pBack, pUse, edge,
                         clear: i == 0, texAll: pTexAll);
        }

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
