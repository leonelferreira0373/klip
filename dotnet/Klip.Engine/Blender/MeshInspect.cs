using System;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Klip.Engine.Blender;

/// <summary>
/// MEDIR A TOPOLOGIA de uma malha, para deixar de ser opinião.
///
/// Os limiares vêm de uma calibração contra a Suzanne — o modelo do Blender feito à mão por
/// pessoas: 93.6% quads, zero ngons, zero pólos de valência 6+. Mas a Suzanne também é
/// não-manifold e não estanque, porque arte a sério falha essas coisas. Por isso NÃO se avalia
/// por estanqueidade nem por planaridade: avalia-se pelos cinco números que separam mesmo o
/// feito-à-mão do gerado por máquina.
/// </summary>
public static class MeshInspect
{
    public sealed record Resultado(
        string Objeto, int Faces, int Quads, int Tris, int Ngons, int Vertices,
        double QuadPct, int PolosGrandes, int Duplicados, int AreaNula,
        bool TemSubsurf, int SubsurfViewport, int SubsurfRender, bool Suave,
        string Veredicto, string[] Reparos);

    /// <summary>
    /// Corre o Blender headless sobre um .blend e devolve a topologia de cada objecto.
    /// Um .glb não serve: vem triangulado e com os modificadores já aplicados, portanto mediria
    /// a malha de exportação e não a que se pode editar.
    /// </summary>
    public static Resultado[] Inspecionar(string blendPath, TimeSpan? timeout = null)
    {
        if (!File.Exists(blendPath)) throw new FileNotFoundException("ficheiro .blend não encontrado", blendPath);

        var saida = Path.Combine(Path.GetTempPath(), "klip_inspect_" + Guid.NewGuid().ToString("N")[..8] + ".json");
        var r = BlenderBridge.RunScriptOnBlend(blendPath, Script, new[] { saida },
                                               timeout ?? TimeSpan.FromMinutes(5));
        if (!File.Exists(saida))
            throw new InvalidOperationException("a inspecção não produziu resultado.\n" + r.ErrorTail(500));

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(saida));
            var lista = new System.Collections.Generic.List<Resultado>();
            foreach (var o in doc.RootElement.EnumerateArray())
            {
                int faces = I(o, "faces"), quads = I(o, "quads"), tris = I(o, "tris"), ngons = I(o, "ngons");
                int nVerts = I(o, "verts");
                double pct = faces > 0 ? 100.0 * quads / faces : 0;
                int polos = I(o, "polos6"), dup = I(o, "duplicados"), nula = I(o, "area_nula");

                // Os cinco que separam feito-à-mão de gerado. Estanqueidade e planaridade ficam de
                // fora de propósito: a Suzanne falha ambas e é o padrão de referência.
                var reparos = new System.Collections.Generic.List<string>();
                if (pct < 90) reparos.Add($"só {pct:0.0}% de quads (mínimo 90) — converte com tris_convert_to_quads");
                if (ngons > 0) reparos.Add($"{ngons} ngon(s) — tapas de cilindro? usa end_fill_type='NOTHING' + grid_fill por borda");
                // PÓLOS: informação, não veredicto. A própria Suzanne — o modelo à mão de referência —
                // tem 9 pólos de valência 6+, e um cubo com bevel tem 8 nos cantos, que são legítimos.
                // Só é sinal de máquina quando SÃO a malha: uma icosfera é feita quase só de pólos.
                if (nVerts > 0 && polos > nVerts * 0.25)
                    reparos.Add($"{polos} pólos de valência 6+ em {nVerts} vértices — é a assinatura de uma icosfera/primitiva subdividida à bruta; refaz o fluxo de arestas");
                if (dup > 0) reparos.Add($"{dup} vértice(s) duplicado(s) — mete um WELD (1e-4)");
                if (nula > 0) reparos.Add($"{nula} face(s) de área nula — geometria degenerada");
                if (!B(o, "subsurf")) reparos.Add("sem Subsurf — a peça não tem subdivisão nenhuma");
                else if (!B(o, "loops_suporte"))
                    reparos.Add("Subsurf SEM arestas de suporte — a forma está a derreter (mede-se em perda de volume)");
                if (!B(o, "suave")) reparos.Add("sem shade smooth — as facetas vêem-se");

                lista.Add(new Resultado(
                    S(o, "nome"), faces, quads, tris, ngons, I(o, "verts"),
                    Math.Round(pct, 1), polos, dup, nula,
                    B(o, "subsurf"), I(o, "sub_view"), I(o, "sub_render"), B(o, "suave"),
                    reparos.Count == 0 ? "limpo" : (reparos.Count <= 2 ? "aceitável" : "de refazer"),
                    reparos.ToArray()));
            }
            return lista.ToArray();
        }
        finally { try { File.Delete(saida); } catch { } }
    }

    private static int I(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.TryGetInt32(out var i) ? i : 0;
    private static bool B(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
    private static string S(JsonElement e, string k) => e.TryGetProperty(k, out var v) ? (v.GetString() ?? "") : "";

    private const string Script = @"
import bpy, bmesh, json, sys
from mathutils import Vector

_out = sys.argv[sys.argv.index('--') + 1]
res = []

for ob in [o for o in bpy.context.scene.objects if o.type == 'MESH']:
    me = ob.data
    bm = bmesh.new(); bm.from_mesh(me)
    bm.verts.ensure_lookup_table(); bm.faces.ensure_lookup_table()

    quads = tris = ngons = nula = 0
    for f in bm.faces:
        n = len(f.verts)
        if n == 3: tris += 1
        elif n == 4: quads += 1
        else: ngons += 1
        if f.calc_area() < 1e-9: nula += 1

    # Polo = vertice com muitas arestas. Valencia 6+ e a assinatura da icosfera e das
    # primitivas subdivididas a bruta; malha desenhada por gente quase nao os tem.
    polos6 = sum(1 for v in bm.verts if len(v.link_edges) >= 6)

    # duplicados: vertices coincidentes dentro de 1e-4 (o WELD limpa-os)
    dup = 0
    vistos = {}
    for v in bm.verts:
        k = (round(v.co.x, 4), round(v.co.y, 4), round(v.co.z, 4))
        if k in vistos: dup += 1
        else: vistos[k] = 1

    sub = None
    for m in ob.modifiers:
        if m.type == 'SUBSURF': sub = m; break

    # ARESTAS DE SUPORTE: procura-se um par de arestas curtas junto a cada aresta viva.
    # Aproximacao honesta — mede-se a proporcao de arestas MUITO curtas, que e o que um
    # bevel de suporte cria. Sem elas, o Subsurf derrete a forma.
    comps = [e.calc_length() for e in bm.edges if e.calc_length() > 1e-9]
    loops_suporte = False
    if comps:
        comps.sort()
        mediana = comps[len(comps)//2]
        curtas = sum(1 for c in comps if c < mediana * 0.45)
        loops_suporte = curtas >= max(4, len(comps) * 0.08)

    suave = any(p.use_smooth for p in me.polygons) if len(me.polygons) else False

    res.append({
        'nome': ob.name,
        'faces': len(bm.faces), 'quads': quads, 'tris': tris, 'ngons': ngons,
        'verts': len(bm.verts), 'polos6': polos6, 'duplicados': dup, 'area_nula': nula,
        'subsurf': sub is not None,
        'sub_view': (sub.levels if sub else 0),
        'sub_render': (sub.render_levels if sub else 0),
        'loops_suporte': loops_suporte,
        'suave': suave,
    })
    bm.free()

with open(_out, 'w', encoding='utf-8') as f:
    json.dump(res, f)
print('KLIP INSPECT ->', len(res), 'objecto(s)')
";
}
