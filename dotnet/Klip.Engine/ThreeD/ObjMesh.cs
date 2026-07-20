using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;

namespace Klip.Engine.ThreeD;

/// <summary>
/// Leitor de OBJ → o MESMO formato de vértice que o MeshPass já come: [px,py,pz, nx,ny,nz, u,v].
///
/// É isto que faz o KLIP deixar de ser "extrude de um path 2D" e passar a segurar GEOMETRIA A SÉRIO:
/// o Blender (ou qualquer outra coisa) exporta um .obj, o KLIP carrega-o e passa a ser um OBJETO
/// na cena — rodável, iluminável, animável, com o mesmo PBR/IBL do resto.
///
/// Normaliza para caber no mundo do KLIP (dimensão maior = 1 unidade, centrado na origem):
/// sem isto um objeto exportado em metros aparecia gigante ou invisível.
/// </summary>
public static class ObjMesh
{
    private static readonly Dictionary<string, (float[] data, int count)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public static (float[] data, int count) Load(string path)
    {
        path = Path.GetFullPath(path);
        var key = path + "|" + (File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0);
        if (_cache.TryGetValue(key, out var hit)) return hit;

        var pos = new List<Vector3>();
        var nrm = new List<Vector3>();
        var uvs = new List<Vector2>();
        var tris = new List<float>(1 << 16);

        // índices do OBJ são 1-based e podem ser negativos (relativos ao fim)
        static int Ix(int i, int count) => i > 0 ? i - 1 : count + i;

        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var t = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (t.Length == 0) continue;

            switch (t[0])
            {
                case "v" when t.Length >= 4:
                    pos.Add(new Vector3(F(t[1]), F(t[2]), F(t[3])));
                    break;
                case "vn" when t.Length >= 4:
                    nrm.Add(new Vector3(F(t[1]), F(t[2]), F(t[3])));
                    break;
                case "vt" when t.Length >= 3:
                    uvs.Add(new Vector2(F(t[1]), F(t[2])));
                    break;
                case "f" when t.Length >= 4:
                    // fan-triangulate: aguenta quads e n-gons sem depender do exportador
                    for (int k = 2; k < t.Length - 1; k++)
                        Face(t[1], t[k], t[k + 1]);
                    break;
            }

            void Face(string a, string b, string c)
            {
                Span<int> vi = stackalloc int[3];
                Span<int> ti = stackalloc int[3];
                Span<int> ni = stackalloc int[3];
                string[] parts = { a, b, c };
                for (int i = 0; i < 3; i++)
                {
                    var s = parts[i].Split('/');
                    vi[i] = s.Length > 0 && s[0].Length > 0 ? Ix(int.Parse(s[0], CultureInfo.InvariantCulture), pos.Count) : -1;
                    ti[i] = s.Length > 1 && s[1].Length > 0 ? Ix(int.Parse(s[1], CultureInfo.InvariantCulture), uvs.Count) : -1;
                    ni[i] = s.Length > 2 && s[2].Length > 0 ? Ix(int.Parse(s[2], CultureInfo.InvariantCulture), nrm.Count) : -1;
                }
                if (vi[0] < 0 || vi[1] < 0 || vi[2] < 0) return;

                // sem normais no ficheiro? calcula a da face — melhor isso do que iluminar a preto
                Vector3 faceN = Vector3.Zero;
                if (ni[0] < 0 || ni[0] >= nrm.Count)
                {
                    var cross = Vector3.Cross(pos[vi[1]] - pos[vi[0]], pos[vi[2]] - pos[vi[0]]);
                    faceN = cross.LengthSquared() < 1e-12f ? Vector3.UnitZ : Vector3.Normalize(cross);
                }
                for (int i = 0; i < 3; i++)
                {
                    var p = pos[vi[i]];
                    var n = (ni[i] >= 0 && ni[i] < nrm.Count) ? nrm[ni[i]] : faceN;
                    var uv = (ti[i] >= 0 && ti[i] < uvs.Count) ? uvs[ti[i]] : Vector2.Zero;
                    tris.Add(p.X); tris.Add(p.Y); tris.Add(p.Z);
                    tris.Add(n.X); tris.Add(n.Y); tris.Add(n.Z);
                    tris.Add(uv.X); tris.Add(1f - uv.Y);      // OBJ tem V ao contrário do nosso
                }
            }
        }

        var arr = tris.ToArray();
        Normalize(arr);
        var res = (arr, arr.Length / 8);
        _cache[key] = res;
        return res;
    }

    /// <summary>Centra na origem e escala para a maior dimensão valer 1 — cabe sempre no mundo do KLIP.</summary>
    private static void Normalize(float[] v)
    {
        if (v.Length == 0) return;
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        for (int i = 0; i < v.Length; i += 8)
        {
            if (v[i] < minX) minX = v[i];       if (v[i] > maxX) maxX = v[i];
            if (v[i + 1] < minY) minY = v[i + 1]; if (v[i + 1] > maxY) maxY = v[i + 1];
            if (v[i + 2] < minZ) minZ = v[i + 2]; if (v[i + 2] > maxZ) maxZ = v[i + 2];
        }
        float cx = (minX + maxX) * 0.5f, cy = (minY + maxY) * 0.5f, cz = (minZ + maxZ) * 0.5f;
        float span = MathF.Max(maxX - minX, MathF.Max(maxY - minY, maxZ - minZ));
        float k = span > 1e-6f ? 1f / span : 1f;
        for (int i = 0; i < v.Length; i += 8)
        {
            v[i] = (v[i] - cx) * k;
            v[i + 1] = (v[i + 1] - cy) * k;
            v[i + 2] = (v[i + 2] - cz) * k;
        }
    }

    private static float F(string s) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 0f;
}
