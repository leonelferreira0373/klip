using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace Klip.Engine.ThreeD;

/// <summary>
/// Leitor de glTF binário (.glb) → o formato de vértice do MeshPass [px,py,pz, nx,ny,nz, u,v]
/// MAIS o material PBR (cor base, metal, aspereza).
///
/// PORQUÊ NÃO OBJ: o OBJ é o formato mais pobre que existe — só triângulos e UVs. Perde materiais,
/// hierarquia, cores de vértice e UVs múltiplos, e obriga a achatar tudo antes de sair. O glTF
/// atravessa a fronteira Blender→KLIP com o material intacto, que é o que faz o objeto chegar
/// com o aspeto que tinha lá dentro em vez de cinzento.
/// </summary>
public static class GltfMesh
{
    public readonly record struct Pbr(uint BaseArgb, float Metal, float Rough);

    /// <summary>Uma PARTE da malha: tudo o que partilha o mesmo material. Um ténis tem sola,
    /// tecido e ilhós — sem isto chegava tudo com uma cor só.</summary>
    /// <param name="NormalTex">mapa de normais (relevo). Sem ele, uma superfície trabalhada chega lisa.</param>
    /// <param name="MrTex">mapa metálico-rugosidade do glTF: G = rugosidade, B = metálico, no MESMO ficheiro.</param>
    /// <param name="EmisTex">mapa de emissão.</param>
    /// <param name="EmisR">cor emissiva já multiplicada pela força (KHR_materials_emissive_strength).</param>
    /// <param name="Alpha">alfa do baseColorFactor; &lt; 1 = material translúcido.</param>
    /// <param name="Blend">alphaMode BLEND — o material pede mistura, não é só uma cor com alfa.</param>
    /// <param name="Centro">centro da caixa ORIGINAL, antes de normalizar.</param>
    /// <param name="Escala">factor aplicado (1/maior dimensão).</param>
    public sealed record Part(float[] Data, int Count, Pbr Material, string? BaseTex,
                              string? NormalTex = null, string? MrTex = null, string? EmisTex = null,
                              float EmisR = 0, float EmisG = 0, float EmisB = 0,
                              float Alpha = 1f, bool Blend = false,
                              Vector3 Centro = default, float Escala = 1f)
    {
        /// <summary>
        /// Desfaz a normalização: ponto do espaço-objeto do KLIP → coordenadas do ficheiro glTF.
        /// SEM ISTO NÃO HÁ EDIÇÃO DE MALHA. O leitor encolhe tudo para caber em 1 unidade e, até
        /// aqui, deitava fora o centro e o factor — o que tornava impossível dizer ao Blender
        /// QUAL vértice o utilizador tocou, porque as coordenadas não correspondiam a nada.
        /// </summary>
        public Vector3 ParaGltf(Vector3 pKlip)
            => new(pKlip.X / Escala + Centro.X, pKlip.Y / Escala + Centro.Y, pKlip.Z / Escala + Centro.Z);

        /// <summary>
        /// glTF → Blender. O exportador corre com export_yup=True, portanto o eixo vertical trocou:
        /// desfazer isso é obrigatório, senão a peça é editada deitada.
        /// </summary>
        public static Vector3 GltfParaBlender(Vector3 g) => new(g.X, -g.Z, g.Y);

        /// <summary>Atalho: ponto tocado na tela → coordenadas do .blend.</summary>
        public Vector3 ParaBlender(Vector3 pKlip) => GltfParaBlender(ParaGltf(pKlip));
    }

    private static readonly Dictionary<string, (float[] data, int count, Pbr pbr)> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, IReadOnlyList<Part>> _partsCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Carrega a malha SEPARADA POR MATERIAL, com a textura de cor base extraída para disco.
    /// A normalização é feita com a caixa COMUM a todas as partes — normalizar cada uma à sua
    /// escala faria a sola e o tecido do mesmo sapato saírem com tamanhos diferentes.
    /// </summary>
    private static readonly object _cacheGate = new();

    public static IReadOnlyList<Part> LoadParts(string path)
    {
        path = Path.GetFullPath(path);
        var ck = "parts|" + path + "|" + (File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0);
        // TRANCA: até ao modo malha só a thread "klip-3d" entrava aqui. Agora a thread da UI lê a
        // malha a CADA movimento do rato para fazer o picking, e o worker da operação também —
        // três threads num Dictionary simples é corrupção à espera de acontecer.
        lock (_cacheGate) { if (_partsCache.TryGetValue(ck, out var got)) return got; }

        var bytes = File.ReadAllBytes(path);
        var (json, bin) = SplitGlb(bytes);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var accessors = Arr(root, "accessors");
        var views = Arr(root, "bufferViews");
        var meshes = Arr(root, "meshes");
        var nodes = Arr(root, "nodes");
        var materials = Arr(root, "materials");

        var buckets = new Dictionary<int, List<float>>();          // material → vértices

        void Walk(int nodeIx, Matrix4x4 parent)
        {
            if (nodeIx < 0 || nodeIx >= nodes.Count) return;
            var n = nodes[nodeIx];
            var world = NodeMatrix(n) * parent;

            if (n.TryGetProperty("mesh", out var mi) && mi.TryGetInt32(out int meshIx)
                && meshIx >= 0 && meshIx < meshes.Count)
            {
                foreach (var prim in EnumArr(meshes[meshIx], "primitives"))
                {
                    if (prim.TryGetProperty("mode", out var md) && md.TryGetInt32(out int mode) && mode != 4) continue;
                    if (!prim.TryGetProperty("attributes", out var at)) continue;
                    var pos = ReadVec3(at, "POSITION", accessors, views, bin);
                    if (pos.Count == 0) continue;
                    var nor = ReadVec3(at, "NORMAL", accessors, views, bin);
                    var uv = ReadVec2(at, "TEXCOORD_0", accessors, views, bin);
                    var idx = prim.TryGetProperty("indices", out var ii) && ii.TryGetInt32(out int ia)
                        ? ReadIndices(ia, accessors, views, bin) : SeqIndices(pos.Count);

                    int matIx = prim.TryGetProperty("material", out var pm) && pm.TryGetInt32(out int mx) ? mx : -1;
                    if (!buckets.TryGetValue(matIx, out var sink)) buckets[matIx] = sink = new List<float>(4096);

                    var nrmMat = Normal3x3(world);
                    for (int t = 0; t + 2 < idx.Count; t += 3)
                    {
                        Vector3 fa = Vector3.Transform(pos[idx[t]], world);
                        Vector3 fb = Vector3.Transform(pos[idx[t + 1]], world);
                        Vector3 fc = Vector3.Transform(pos[idx[t + 2]], world);
                        var cr = Vector3.Cross(fb - fa, fc - fa);
                        var fn = cr.LengthSquared() < 1e-14f ? Vector3.UnitZ : Vector3.Normalize(cr);
                        for (int k = 0; k < 3; k++)
                        {
                            int vi = idx[t + k];
                            var p = Vector3.Transform(pos[vi], world);
                            var nv = vi < nor.Count ? Vector3.TransformNormal(nor[vi], nrmMat) : fn;
                            nv = nv.LengthSquared() > 1e-12f ? Vector3.Normalize(nv) : fn;
                            var t2 = vi < uv.Count ? uv[vi] : Vector2.Zero;
                            sink.Add(p.X); sink.Add(p.Y); sink.Add(p.Z);
                            sink.Add(nv.X); sink.Add(nv.Y); sink.Add(nv.Z);
                            sink.Add(t2.X); sink.Add(1f - t2.Y);
                        }
                    }
                }
            }
            foreach (var c in EnumArr(n, "children"))
                if (c.TryGetInt32(out int ci)) Walk(ci, world);
        }

        bool walked = false;
        if (root.TryGetProperty("scenes", out var scenes) && scenes.ValueKind == JsonValueKind.Array)
        {
            int sIx = root.TryGetProperty("scene", out var sc) && sc.TryGetInt32(out int si) ? si : 0;
            var list = Arr(root, "scenes");
            if (sIx >= 0 && sIx < list.Count)
                foreach (var r in EnumArr(list[sIx], "nodes"))
                    if (r.TryGetInt32(out int ri)) { Walk(ri, Matrix4x4.Identity); walked = true; }
        }
        if (!walked) for (int i = 0; i < nodes.Count; i++) Walk(i, Matrix4x4.Identity);

        // caixa COMUM a todas as partes
        float mnX = float.MaxValue, mnY = float.MaxValue, mnZ = float.MaxValue;
        float mxX = float.MinValue, mxY = float.MinValue, mxZ = float.MinValue;
        foreach (var b in buckets.Values)
            for (int i = 0; i < b.Count; i += 8)
            {
                if (b[i] < mnX) mnX = b[i];         if (b[i] > mxX) mxX = b[i];
                if (b[i + 1] < mnY) mnY = b[i + 1]; if (b[i + 1] > mxY) mxY = b[i + 1];
                if (b[i + 2] < mnZ) mnZ = b[i + 2]; if (b[i + 2] > mxZ) mxZ = b[i + 2];
            }
        float cx = (mnX + mxX) * .5f, cy = (mnY + mxY) * .5f, cz = (mnZ + mxZ) * .5f;
        float span = MathF.Max(mxX - mnX, MathF.Max(mxY - mnY, mxZ - mnZ));
        float k = span > 1e-6f && !float.IsInfinity(span) ? 1f / span : 1f;

        var parts = new List<Part>();
        foreach (var (matIx, b) in buckets)
        {
            if (b.Count == 0) continue;
            var arr = b.ToArray();
            for (int i = 0; i < arr.Length; i += 8)
            { arr[i] = (arr[i] - cx) * k; arr[i + 1] = (arr[i + 1] - cy) * k; arr[i + 2] = (arr[i + 2] - cz) * k; }
            var pbr = matIx >= 0 && matIx < materials.Count ? ReadPbr(materials[matIx]) : new Pbr(0xFFBDBDC6, 0f, 0.5f);
            if (matIx < 0 || matIx >= materials.Count) { parts.Add(new Part(arr, arr.Length / 8, pbr, null, Centro: new Vector3(cx, cy, cz), Escala: k)); continue; }

            var mat = materials[matIx];
            var pmr = mat.TryGetProperty("pbrMetallicRoughness", out var pv) ? pv : default;

            string? baseTex = ExtractTexture(root, views, bin, path, matIx, "base", TexIndex(pmr, "baseColorTexture"));
            string? normTex = ExtractTexture(root, views, bin, path, matIx, "norm", TexIndex(mat, "normalTexture"));
            string? mrTex = ExtractTexture(root, views, bin, path, matIx, "mr", TexIndex(pmr, "metallicRoughnessTexture"));
            string? emiTex = ExtractTexture(root, views, bin, path, matIx, "emi", TexIndex(mat, "emissiveTexture"));

            // A força da emissão vive numa EXTENSÃO à parte; sem a ler, um néon exportado com
            // strength 5 chegava com a intensidade de uma tinta baça.
            float ef = 1f;
            if (mat.TryGetProperty("extensions", out var exts)
                && exts.TryGetProperty("KHR_materials_emissive_strength", out var es)
                && es.TryGetProperty("emissiveStrength", out var esv))
                ef = (float)esv.GetDouble();

            float er = 0, eg = 0, eb = 0;
            if (mat.TryGetProperty("emissiveFactor", out var emf) && emf.ValueKind == JsonValueKind.Array && emf.GetArrayLength() >= 3)
            {
                var e = emf.EnumerateArray().ToArray();
                er = (float)e[0].GetDouble() * ef; eg = (float)e[1].GetDouble() * ef; eb = (float)e[2].GetDouble() * ef;
            }

            float alpha = 1f;
            if (pmr.ValueKind == JsonValueKind.Object
                && pmr.TryGetProperty("baseColorFactor", out var bcf)
                && bcf.ValueKind == JsonValueKind.Array && bcf.GetArrayLength() >= 4)
                alpha = (float)bcf.EnumerateArray().ElementAt(3).GetDouble();

            bool blend = mat.TryGetProperty("alphaMode", out var am)
                         && (am.GetString() ?? "OPAQUE") is "BLEND" or "MASK";

            parts.Add(new Part(arr, arr.Length / 8, pbr, baseTex, normTex, mrTex, emiTex, er, eg, eb, alpha, blend,
                               Centro: new Vector3(cx, cy, cz), Escala: k));
        }
        lock (_cacheGate) _partsCache[ck] = parts;
        return parts;
    }

    /// <summary>Índice da textura dentro de um slot ("baseColorTexture", "normalTexture"…), ou -1.</summary>
    private static int TexIndex(JsonElement owner, string slot)
        => owner.ValueKind == JsonValueKind.Object
           && owner.TryGetProperty(slot, out var s)
           && s.TryGetProperty("index", out var i)
           && i.TryGetInt32(out int ix) ? ix : -1;

    /// <summary>
    /// Escreve uma textura embutida no .glb num ficheiro que o KLIP possa carregar.
    /// O sufixo distingue os mapas do mesmo material — sem ele, o mapa de normais sobrescrevia
    /// a cor base em disco e o objeto chegava pintado com o próprio relevo.
    /// </summary>
    private static string? ExtractTexture(JsonElement root, List<JsonElement> views, byte[] bin,
                                          string glbPath, int matIx, string sufixo, int tix)
    {
        try
        {
            if (tix < 0) return null;
            var textures = Arr(root, "textures");
            if (tix >= textures.Count) return null;
            if (!textures[tix].TryGetProperty("source", out var srcE) || !srcE.TryGetInt32(out int srcIx)) return null;
            var images = Arr(root, "images");
            if (srcIx < 0 || srcIx >= images.Count) return null;
            var img = images[srcIx];

            // imagem por caminho (glTF separado) → usa-se tal como está
            if (img.TryGetProperty("uri", out var uriE) && uriE.GetString() is { } uri && !uri.StartsWith("data:"))
            {
                var side = Path.Combine(Path.GetDirectoryName(glbPath) ?? ".", uri);
                return File.Exists(side) ? side : null;
            }
            if (!img.TryGetProperty("bufferView", out var bvE) || !bvE.TryGetInt32(out int bvIx)) return null;
            if (bvIx < 0 || bvIx >= views.Count) return null;
            var v = views[bvIx];
            int off = v.TryGetProperty("byteOffset", out var o) ? o.GetInt32() : 0;
            int len = v.TryGetProperty("byteLength", out var bl) ? bl.GetInt32() : 0;
            if (len <= 0 || off + len > bin.Length) return null;

            string mime = img.TryGetProperty("mimeType", out var mt) ? (mt.GetString() ?? "image/png") : "image/png";
            string ext = mime.Contains("jpeg") ? ".jpg" : ".png";
            var dir = Path.Combine(Path.GetTempPath(), "klip_gltf_tex");
            Directory.CreateDirectory(dir);
            var outp = Path.Combine(dir,
                Path.GetFileNameWithoutExtension(glbPath) + "_m" + matIx + "_" + sufixo + ext);
            if (!File.Exists(outp) || new FileInfo(outp).Length != len)
                File.WriteAllBytes(outp, bin[off..(off + len)]);
            return outp;
        }
        catch { return null; }
    }

    public static (float[] data, int count, Pbr pbr) Load(string path)
    {
        path = Path.GetFullPath(path);
        var key = path + "|" + (File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0);
        if (_cache.TryGetValue(key, out var hit)) return hit;

        var bytes = File.ReadAllBytes(path);
        var (json, bin) = SplitGlb(bytes);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var verts = new List<float>(1 << 16);
        var pbr = new Pbr(0xFFBDBDC6, 0f, 0.4f);
        bool gotPbr = false;

        var accessors = Arr(root, "accessors");
        var views = Arr(root, "bufferViews");
        var meshes = Arr(root, "meshes");
        var nodes = Arr(root, "nodes");
        var materials = Arr(root, "materials");

        // percorrer a árvore de nós para APLICAR as transformações — sem isto, partes
        // posicionadas por hierarquia (parafusos num array, por ex.) aterram todas na origem.
        void Walk(int nodeIx, Matrix4x4 parent)
        {
            if (nodeIx < 0 || nodeIx >= nodes.Count) return;
            var n = nodes[nodeIx];
            var local = NodeMatrix(n);
            var world = local * parent;

            if (n.TryGetProperty("mesh", out var mi) && mi.TryGetInt32(out int meshIx)
                && meshIx >= 0 && meshIx < meshes.Count)
            {
                foreach (var prim in EnumArr(meshes[meshIx], "primitives"))
                {
                    if (prim.TryGetProperty("mode", out var md) && md.TryGetInt32(out int mode) && mode != 4)
                        continue;    // só triângulos
                    if (!prim.TryGetProperty("attributes", out var at)) continue;

                    var pos = ReadVec3(at, "POSITION", accessors, views, bin);
                    if (pos.Count == 0) continue;
                    var nor = ReadVec3(at, "NORMAL", accessors, views, bin);
                    var uv = ReadVec2(at, "TEXCOORD_0", accessors, views, bin);
                    var idx = prim.TryGetProperty("indices", out var ii) && ii.TryGetInt32(out int ia)
                        ? ReadIndices(ia, accessors, views, bin)
                        : SeqIndices(pos.Count);

                    if (!gotPbr && prim.TryGetProperty("material", out var pm) && pm.TryGetInt32(out int mIx)
                        && mIx >= 0 && mIx < materials.Count)
                    { pbr = ReadPbr(materials[mIx]); gotPbr = true; }

                    var nrmMat = Normal3x3(world);
                    for (int t = 0; t + 2 < idx.Count; t += 3)
                    {
                        // normal de face para quando o ficheiro não traz normais
                        Vector3 fa = Vector3.Transform(pos[idx[t]], world);
                        Vector3 fb = Vector3.Transform(pos[idx[t + 1]], world);
                        Vector3 fc = Vector3.Transform(pos[idx[t + 2]], world);
                        var cr = Vector3.Cross(fb - fa, fc - fa);
                        var fn = cr.LengthSquared() < 1e-14f ? Vector3.UnitZ : Vector3.Normalize(cr);

                        for (int k = 0; k < 3; k++)
                        {
                            int vi = idx[t + k];
                            var p = Vector3.Transform(pos[vi], world);
                            var nv = vi < nor.Count ? Vector3.TransformNormal(nor[vi], nrmMat) : fn;
                            if (nv.LengthSquared() > 1e-12f) nv = Vector3.Normalize(nv); else nv = fn;
                            var t2 = vi < uv.Count ? uv[vi] : Vector2.Zero;
                            verts.Add(p.X); verts.Add(p.Y); verts.Add(p.Z);
                            verts.Add(nv.X); verts.Add(nv.Y); verts.Add(nv.Z);
                            verts.Add(t2.X); verts.Add(1f - t2.Y);
                        }
                    }
                }
            }
            foreach (var c in EnumArr(n, "children"))
                if (c.TryGetInt32(out int ci)) Walk(ci, world);
        }

        // arrancar pelas raízes da cena; se não houver cena declarada, por todos os nós
        bool walked = false;
        if (root.TryGetProperty("scenes", out var scenes) && scenes.ValueKind == JsonValueKind.Array)
        {
            int sIx = root.TryGetProperty("scene", out var sc) && sc.TryGetInt32(out int si) ? si : 0;
            var list = Arr(root, "scenes");
            if (sIx >= 0 && sIx < list.Count)
                foreach (var r in EnumArr(list[sIx], "nodes"))
                    if (r.TryGetInt32(out int ri)) { Walk(ri, Matrix4x4.Identity); walked = true; }
        }
        if (!walked) for (int i = 0; i < nodes.Count; i++) Walk(i, Matrix4x4.Identity);

        var arr = verts.ToArray();
        Normalize(arr);
        var res = (arr, arr.Length / 8, pbr);
        _cache[key] = res;
        return res;
    }

    // ---------------- glb ----------------
    private static (byte[] json, byte[] bin) SplitGlb(byte[] b)
    {
        if (b.Length < 12 || BitConverter.ToUInt32(b, 0) != 0x46546C67u)
            throw new InvalidOperationException("não é um .glb (magic errado)");
        int off = 12; byte[] json = Array.Empty<byte>(), bin = Array.Empty<byte>();
        while (off + 8 <= b.Length)
        {
            int len = BitConverter.ToInt32(b, off);
            uint type = BitConverter.ToUInt32(b, off + 4);
            int start = off + 8;
            if (len < 0 || start + len > b.Length) break;
            if (type == 0x4E4F534A) json = b[start..(start + len)];        // JSON
            else if (type == 0x004E4942) bin = b[start..(start + len)];    // BIN
            off = start + len + ((4 - len % 4) % 4);
        }
        if (json.Length == 0) throw new InvalidOperationException("glb sem bloco JSON");
        return (json, bin);
    }

    private static List<JsonElement> Arr(JsonElement e, string name)
    {
        var l = new List<JsonElement>();
        if (e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array)
            foreach (var x in a.EnumerateArray()) l.Add(x);
        return l;
    }
    private static IEnumerable<JsonElement> EnumArr(JsonElement e, string name)
    {
        if (e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array)
            foreach (var x in a.EnumerateArray()) yield return x;
    }

    private static Matrix4x4 NodeMatrix(JsonElement n)
    {
        if (n.TryGetProperty("matrix", out var m) && m.ValueKind == JsonValueKind.Array)
        {
            var v = new float[16]; int i = 0;
            foreach (var x in m.EnumerateArray()) { if (i < 16) v[i++] = x.GetSingle(); }
            return new Matrix4x4(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7],
                                 v[8], v[9], v[10], v[11], v[12], v[13], v[14], v[15]);
        }
        var s = Vector3.One; var t = Vector3.Zero; var q = Quaternion.Identity;
        if (n.TryGetProperty("scale", out var se)) s = V3(se, Vector3.One);
        if (n.TryGetProperty("translation", out var te)) t = V3(te, Vector3.Zero);
        if (n.TryGetProperty("rotation", out var re) && re.ValueKind == JsonValueKind.Array)
        {
            var v = new float[4]; int i = 0;
            foreach (var x in re.EnumerateArray()) { if (i < 4) v[i++] = x.GetSingle(); }
            q = new Quaternion(v[0], v[1], v[2], v[3]);
        }
        return Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(q) * Matrix4x4.CreateTranslation(t);
    }

    private static Vector3 V3(JsonElement e, Vector3 def)
    {
        if (e.ValueKind != JsonValueKind.Array) return def;
        var v = new float[3]; int i = 0;
        foreach (var x in e.EnumerateArray()) { if (i < 3) v[i++] = x.GetSingle(); }
        return new Vector3(v[0], v[1], v[2]);
    }

    private static Matrix4x4 Normal3x3(Matrix4x4 m)
        => Matrix4x4.Invert(m, out var inv) ? Matrix4x4.Transpose(inv) : m;

    // ---------------- accessors ----------------
    private static (int off, int stride, int count, int comp) Info(
        int accIx, List<JsonElement> accessors, List<JsonElement> views, out int compType)
    {
        compType = 5126;
        if (accIx < 0 || accIx >= accessors.Count) return (0, 0, 0, 0);
        var a = accessors[accIx];
        int count = a.TryGetProperty("count", out var c) ? c.GetInt32() : 0;
        compType = a.TryGetProperty("componentType", out var ct) ? ct.GetInt32() : 5126;
        string type = a.TryGetProperty("type", out var ty) ? (ty.GetString() ?? "SCALAR") : "SCALAR";
        int comp = type switch { "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, "MAT4" => 16, _ => 1 };
        int accOff = a.TryGetProperty("byteOffset", out var ao) ? ao.GetInt32() : 0;
        int vIx = a.TryGetProperty("bufferView", out var bv) ? bv.GetInt32() : -1;
        int vOff = 0, stride = 0;
        if (vIx >= 0 && vIx < views.Count)
        {
            var v = views[vIx];
            vOff = v.TryGetProperty("byteOffset", out var vo) ? vo.GetInt32() : 0;
            stride = v.TryGetProperty("byteStride", out var bs) ? bs.GetInt32() : 0;
        }
        int elemSize = comp * (compType switch { 5120 or 5121 => 1, 5122 or 5123 => 2, _ => 4 });
        if (stride == 0) stride = elemSize;
        return (vOff + accOff, stride, count, comp);
    }

    private static List<Vector3> ReadVec3(JsonElement attrs, string name,
        List<JsonElement> acc, List<JsonElement> views, byte[] bin)
    {
        var r = new List<Vector3>();
        if (!attrs.TryGetProperty(name, out var e) || !e.TryGetInt32(out int ix)) return r;
        var (off, stride, count, _) = Info(ix, acc, views, out _);
        for (int i = 0; i < count; i++)
        {
            int p = off + i * stride;
            if (p + 12 > bin.Length) break;
            r.Add(new Vector3(BitConverter.ToSingle(bin, p), BitConverter.ToSingle(bin, p + 4),
                              BitConverter.ToSingle(bin, p + 8)));
        }
        return r;
    }

    private static List<Vector2> ReadVec2(JsonElement attrs, string name,
        List<JsonElement> acc, List<JsonElement> views, byte[] bin)
    {
        var r = new List<Vector2>();
        if (!attrs.TryGetProperty(name, out var e) || !e.TryGetInt32(out int ix)) return r;
        var (off, stride, count, _) = Info(ix, acc, views, out int ct);
        for (int i = 0; i < count; i++)
        {
            int p = off + i * stride;
            if (p + 8 > bin.Length) break;
            r.Add(ct == 5126
                ? new Vector2(BitConverter.ToSingle(bin, p), BitConverter.ToSingle(bin, p + 4))
                : new Vector2(BitConverter.ToUInt16(bin, p) / 65535f, BitConverter.ToUInt16(bin, p + 2) / 65535f));
        }
        return r;
    }

    private static List<int> ReadIndices(int ix, List<JsonElement> acc, List<JsonElement> views, byte[] bin)
    {
        var r = new List<int>();
        var (off, stride, count, _) = Info(ix, acc, views, out int ct);
        for (int i = 0; i < count; i++)
        {
            int p = off + i * stride;
            if (p + 1 > bin.Length) break;
            r.Add(ct switch
            {
                5121 => bin[p],
                5123 => p + 2 <= bin.Length ? BitConverter.ToUInt16(bin, p) : 0,
                _ => p + 4 <= bin.Length ? BitConverter.ToInt32(bin, p) : 0,
            });
        }
        return r;
    }

    private static List<int> SeqIndices(int n)
    { var r = new List<int>(n); for (int i = 0; i < n; i++) r.Add(i); return r; }

    private static Pbr ReadPbr(JsonElement mat)
    {
        // ARMADILHA DO glTF: os valores por omissão da spec são metallic=1 e roughness=1, e os
        // exportadores OMITEM o campo quando ele é igual ao default. Começar a zero fazia com que
        // todo o metal chegasse cá como plástico — foi exatamente o que aconteceu ao anel dourado.
        uint argb = 0xFFBDBDC6; float metal = 1f, rough = 1f;
        if (mat.TryGetProperty("pbrMetallicRoughness", out var p))
        {
            if (p.TryGetProperty("baseColorFactor", out var bc) && bc.ValueKind == JsonValueKind.Array)
            {
                var v = new float[4] { 1, 1, 1, 1 }; int i = 0;
                foreach (var x in bc.EnumerateArray()) { if (i < 4) v[i++] = x.GetSingle(); }
                // glTF entrega linear; o KLIP pinta em sRGB
                static byte S(float lin)
                {
                    float c = lin <= 0.0031308f ? lin * 12.92f : 1.055f * MathF.Pow(MathF.Max(lin, 0f), 1f / 2.4f) - 0.055f;
                    return (byte)Math.Clamp(c * 255f + 0.5f, 0, 255);
                }
                argb = 0xFF000000u | ((uint)S(v[0]) << 16) | ((uint)S(v[1]) << 8) | S(v[2]);
            }
            if (p.TryGetProperty("metallicFactor", out var mf)) metal = mf.GetSingle();
            if (p.TryGetProperty("roughnessFactor", out var rf)) rough = rf.GetSingle();
        }
        return new Pbr(argb, metal, rough);
    }

    /// <summary>Centra e escala para a maior dimensão valer 1 — cabe sempre no mundo do KLIP.</summary>
    private static void Normalize(float[] v)
    {
        if (v.Length == 0) return;
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        for (int i = 0; i < v.Length; i += 8)
        {
            if (v[i] < minX) minX = v[i];         if (v[i] > maxX) maxX = v[i];
            if (v[i + 1] < minY) minY = v[i + 1]; if (v[i + 1] > maxY) maxY = v[i + 1];
            if (v[i + 2] < minZ) minZ = v[i + 2]; if (v[i + 2] > maxZ) maxZ = v[i + 2];
        }
        float cx = (minX + maxX) * .5f, cy = (minY + maxY) * .5f, cz = (minZ + maxZ) * .5f;
        float span = MathF.Max(maxX - minX, MathF.Max(maxY - minY, maxZ - minZ));
        float k = span > 1e-6f ? 1f / span : 1f;
        for (int i = 0; i < v.Length; i += 8)
        { v[i] = (v[i] - cx) * k; v[i + 1] = (v[i + 1] - cy) * k; v[i + 2] = (v[i + 2] - cz) * k; }
    }
}
