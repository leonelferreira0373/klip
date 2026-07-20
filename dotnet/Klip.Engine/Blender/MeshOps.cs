using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Klip.Engine.Blender;

/// <summary>
/// OPERAÇÕES DE MALHA POR PONTO TOCADO.
///
/// O dono edita a malha DENTRO do KLIP: aponta com o rato para um sítio da peça e diz o que quer
/// ali. Do lado de cá isso é um punhado de PONTOS 3D; do lado do Blender tem de virar «estes
/// vértices, estas arestas, estas faces».
///
/// Não há índices para transportar. O exportador corre com export_apply=True — aplica os
/// modificadores e tritura a malha em triângulos —, portanto o vértice nº 412 do .glb não é o
/// vértice nº 412 do .blend nem existe lá. A ÚNICA ponte fiável entre os dois ficheiros é a
/// POSIÇÃO. Daí que tudo aqui funcione por proximidade: para cada ponto tocado procura-se o
/// elemento mais próximo, com uma tolerância proporcional ao tamanho da peça (não em milímetros
/// absolutos, senão a mesma tolerância seria enorme num parafuso e ridícula num edifício).
///
/// Tudo é feito com <c>bmesh.ops</c> e não com operadores de modo-edição: sem estados de modo,
/// sem seleção global, sem contexto para forjar — que é onde o Blender headless rebenta.
/// (<c>mesh.loopcut_slide</c> e <c>mesh.knife_project</c> matam mesmo o processo em -b, por isso
/// não aparecem em lado nenhum deste ficheiro.)
/// </summary>
public static class MeshOps
{
    /// <param name="Nome">verbo aceite pela API.</param>
    /// <param name="Unidade">o que o parâmetro «valor» significa NESTA operação — não é o mesmo
    /// em todas, e chamar-lhe sempre «valor» sem dizer isto seria uma armadilha.</param>
    public sealed record Op(string Nome, string Descricao, string Unidade);

    /// <summary>
    /// As operações que EXISTEM E FORAM PROVADAS em headless. Deliberadamente pequeno: mais vale
    /// oito verbos que funcionam sempre do que quinze em que três rebentam o processo.
    /// </summary>
    public static readonly Op[] Catalogo =
    {
        new("mover",      "empurra/puxa os vértices tocados ao longo da normal deles",  "distância (unidades do Blender); omisso = 4% da diagonal da peça"),
        new("mover_x",    "desloca os vértices tocados no eixo X do Blender",            "distância com sinal"),
        new("mover_y",    "desloca os vértices tocados no eixo Y do Blender",            "distância com sinal"),
        new("mover_z",    "desloca os vértices tocados no eixo Z do Blender (a altura)", "distância com sinal"),
        new("extrudir",   "puxa as faces tocadas para fora, criando paredes",            "altura da extrusão"),
        new("bisel",      "chanfra as arestas tocadas (bevel)",                          "largura do chanfro"),
        new("inset",      "cria uma face mais pequena dentro das faces tocadas",         "espessura da moldura"),
        new("subdividir", "corta as faces tocadas em mais faces",                        "ignorado (usa o nº de cortes)"),
        new("suavizar",   "relaxa a posição dos vértices tocados (alisa o relevo)",      "força 0..1; omisso = 0.5"),
        new("apagar",     "elimina as faces tocadas (abre um buraco)",                   "ignorado"),
        new("fundir",     "funde os vértices tocados num só ponto, no centro deles",     "ignorado"),
    };

    /// <summary>True se o verbo existe. O chamador deve validar ANTES de arrancar o Blender —
    /// arrancar um processo de vários segundos para depois dizer «não conheço» é desrespeito.</summary>
    public static bool Suporta(string operacao)
        => Array.Exists(Catalogo, o => string.Equals(o.Nome, operacao, StringComparison.OrdinalIgnoreCase));

    /// <summary>Lista dos verbos, para a mensagem de erro dizer o que ESTÁ disponível.</summary>
    public static string Verbos => string.Join(", ", Array.ConvertAll(Catalogo, o => o.Nome));

    /// <summary>
    /// Gera o Python. Os pontos vão EMBUTIDOS no script (e não em sys.argv) de propósito: o
    /// preâmbulo de exportação do KLIP já ocupa argv[0] e argv[1], e misturar as duas convenções
    /// era garantir que um dia alguém trocava a ordem.
    /// </summary>
    /// <param name="pontosBlender">pontos JÁ em coordenadas do .blend (Part.ParaBlender).</param>
    /// <param name="valor">ver <see cref="Op.Unidade"/>; null = a operação escolhe um valor
    /// proporcional à peça, para o resultado ser sempre visível.</param>
    /// <param name="segmentos">segmentos do bisel / cortes da subdivisão / passagens do suavizar.</param>
    public static string Script(string operacao, IReadOnlyList<Vector3> pontosBlender,
                               double? valor, int segmentos = 2)
    {
        if (pontosBlender is null || pontosBlender.Count == 0)
            throw new ArgumentException("sem pontos: não há onde aplicar a operação", nameof(pontosBlender));
        if (!Suporta(operacao))
            throw new ArgumentException($"operação «{operacao}» desconhecida. Existem: {Verbos}", nameof(operacao));
        // O valor entra no script como LITERAL Python. "R" de um NaN escreve «NaN», que em Python é
        // um nome que não existe: o script morria no cabeçalho, ANTES do try, e o Blender saía com
        // código 0 e sem recibo — a falha mais difícil de ler que há. Mesmo para os pontos.
        if (valor is { } v && (double.IsNaN(v) || double.IsInfinity(v)))
            throw new ArgumentException("o valor da operação tem de ser um número finito", nameof(valor));
        for (int i = 0; i < pontosBlender.Count; i++)
        {
            var q = pontosBlender[i];
            if (!float.IsFinite(q.X) || !float.IsFinite(q.Y) || !float.IsFinite(q.Z))
                throw new ArgumentException($"o ponto {i} não é finito ({q}) — o picking devolveu lixo",
                                            nameof(pontosBlender));
        }

        var pts = new StringBuilder("[");
        for (int i = 0; i < pontosBlender.Count; i++)
        {
            var p = pontosBlender[i];
            if (i > 0) pts.Append(", ");
            pts.Append('(').Append(F(p.X)).Append(", ").Append(F(p.Y)).Append(", ").Append(F(p.Z)).Append(')');
        }
        pts.Append(']');

        return Modelo
            .Replace("__PONTOS__", pts.ToString())
            .Replace("__OP__", operacao.ToLowerInvariant())
            .Replace("__VALOR__", valor is null ? "None" : F(valor.Value))
            .Replace("__SEG__", Math.Clamp(segmentos, 1, 12).ToString(CultureInfo.InvariantCulture));
    }

    // "R" e cultura invariante: numa máquina com vírgula decimal, 0,04 seria dois argumentos em Python.
    private static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);
    private static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>
    /// Pesca a linha «KLIP MESHOP -&gt; {json}» do stdout do Blender. É o recibo da operação:
    /// sem ele não sabemos se mexeu em alguma coisa ou se passou ao lado da peça toda.
    /// </summary>
    public static string? Recibo(string stdout)
    {
        if (string.IsNullOrEmpty(stdout)) return null;
        const string marca = "KLIP MESHOP ->";
        int i = stdout.LastIndexOf(marca, StringComparison.Ordinal);
        if (i < 0) return null;
        int fim = stdout.IndexOf('\n', i);
        var linha = fim < 0 ? stdout[(i + marca.Length)..] : stdout[(i + marca.Length)..fim];
        return linha.Trim();
    }

    // =====================================================================================
    // O script. Escrito para NUNCA rebentar: tudo dentro de um try, e o que correr mal sai
    // pelo mesmo canal (o json do recibo) em vez de morrer com um traceback que ninguém lê.
    // =====================================================================================
    private const string Modelo = """
# ============ KLIP: operacao de malha por proximidade aos pontos tocados ============
import bpy, bmesh, json
from mathutils import Vector

_PONTOS = __PONTOS__
_OP     = '__OP__'
_VALOR  = __VALOR__      # None = a operacao escolhe algo proporcional a peca
_SEG    = __SEG__

_res = {'op': _OP, 'pontos': len(_PONTOS)}

def _diagonal(cos):
    if not cos: return 1.0
    xs = [c.x for c in cos]; ys = [c.y for c in cos]; zs = [c.z for c in cos]
    d = Vector((max(xs)-min(xs), max(ys)-min(ys), max(zs)-min(zs))).length
    return d if d > 1e-9 else 1.0

try:
    alvos = [Vector(p) for p in _PONTOS]

    # ---- QUAL objecto? O que passa mais perto dos pontos tocados. -------------------
    # O .glb funde a cena toda num so ficheiro, portanto o ponto que veio do KLIP nao traz
    # consigo o nome do objecto. Deduz-se: soma-se, por objecto, a distancia de cada ponto ao
    # vertice mais proximo, e ganha o menor total.
    cands = []
    for _ob in bpy.context.scene.objects:
        if _ob.type != 'MESH' or len(_ob.data.vertices) == 0: continue
        _mw = _ob.matrix_world
        _wco = [_mw @ v.co for v in _ob.data.vertices]
        custo = 0.0
        for p in alvos:
            custo += min((c - p).length_squared for c in _wco)
        cands.append((custo, _ob.name, _ob))
    if not cands:
        raise RuntimeError('nao ha nenhuma malha nesta cena')
    cands.sort(key=lambda t: (t[0], t[1]))
    ob = cands[0][2]
    _res['objecto'] = ob.name

    me = ob.data
    bm = bmesh.new(); bm.from_mesh(me)
    bm.verts.ensure_lookup_table(); bm.edges.ensure_lookup_table(); bm.faces.ensure_lookup_table()

    mw  = ob.matrix_world
    mwi = mw.inverted()
    # Trabalha-se em espaco LOCAL do objecto (e onde os vertices vivem); os pontos e que vem
    # ao encontro deles, e nao ao contrario.
    alvos_loc = [mwi @ p for p in alvos]

    antes = {'verts': len(bm.verts), 'arestas': len(bm.edges), 'faces': len(bm.faces)}
    _res['antes'] = antes

    diag = _diagonal([v.co for v in bm.verts])
    # TOLERANCIA PROPORCIONAL, POR DUAS MEDIDAS. Em absoluto seria sempre errado — a mesma
    # medida e um continente num parafuso e um grao de po num predio. Mas so a diagonal
    # tambem chega mal: MEDIDO, 6% da diagonal apanhava 66 dos 232 vertices de um relogio
    # com um unico toque, e editar a mao nao e isso. Por isso manda o menor dos dois: 2.5%
    # da peca, com um piso de metade da aresta tipica para que numa malha densa o toque
    # ainda apanhe a vizinhanca imediata em vez de um vertice solitario.
    _comps = sorted(e.calc_length() for e in bm.edges if e.calc_length() > 1e-9)
    _mediana = _comps[len(_comps)//2] if _comps else diag * 0.05
    tol = max(diag * 0.025, _mediana * 0.5)
    _res['tolerancia'] = round(tol, 6)

    # o utilizador pensa em unidades de mundo; a malha vive em local. Se o objecto tem escala,
    # 0.1 de mundo nao sao 0.1 de local.
    _sc = sum(abs(s) for s in mw.to_scale()) / 3.0
    if _sc < 1e-9: _sc = 1.0
    valor_loc = (_VALOR / _sc) if _VALOR is not None else diag * 0.04
    _res['valor'] = round(valor_loc, 6)

    # A que distancia ficou o ponto tocado do elemento que acabou escolhido. E a UNICA forma de
    # distinguir «acertou» de «passou ao lado»: como o mais proximo entra SEMPRE, uma operacao
    # aplicada a meio metro do sitio certo tem exactamente o mesmo aspecto de sucesso que uma
    # boa. Sai no recibo para o lado C# poder avisar em vez de fingir que correu bem.
    _desvio = [0.0]

    def perto(elems, co):
        # Dentro da tolerancia, MAIS o mais proximo de cada ponto. O segundo termo e o que
        # impede o silencio: se o dedo caiu um pouco ao lado, a operacao ainda acerta em algo
        # em vez de nao fazer nada e parecer que o programa esta avariado.
        sel = set()
        for p in alvos_loc:
            melhor = None; md = 1e30
            for e in elems:
                dd = (co(e) - p).length_squared
                if dd < md: md = dd; melhor = e
                if dd <= tol * tol: sel.add(e)
            if melhor is not None:
                sel.add(melhor)
                if md ** 0.5 > _desvio[0]: _desvio[0] = md ** 0.5
        return list(sel)

    _cv = lambda v: v.co
    _ce = lambda e: (e.verts[0].co + e.verts[1].co) * 0.5
    _cf = lambda f: f.calc_center_median()

    if _OP == 'mover' or _OP.startswith('mover_'):
        vs = perto(bm.verts, _cv)
        if _OP == 'mover':
            # sem eixo pedido, a direccao natural e a normal: empurrar/puxar a superficie
            n = Vector((0.0, 0.0, 0.0))
            for v in vs: n += v.normal
            if n.length < 1e-9: n = Vector((0.0, 0.0, 1.0))
        else:
            eixo = {'mover_x': (1.0,0.0,0.0), 'mover_y': (0.0,1.0,0.0), 'mover_z': (0.0,0.0,1.0)}[_OP]
            n = mwi.to_3x3() @ Vector(eixo)
            if n.length < 1e-9: n = Vector(eixo)
        n.normalize()
        bmesh.ops.translate(bm, verts=vs, vec=n * valor_loc)
        _res['tocados'] = len(vs)

    elif _OP == 'extrudir':
        fs = perto(bm.faces, _cf)
        n = Vector((0.0, 0.0, 0.0))
        for f in fs: n += f.normal
        if n.length < 1e-9: n = Vector((0.0, 0.0, 1.0))
        n.normalize()
        r = bmesh.ops.extrude_face_region(bm, geom=fs)
        novos = [g for g in r['geom'] if isinstance(g, bmesh.types.BMVert)]
        bmesh.ops.translate(bm, verts=novos, vec=n * valor_loc)
        # as faces de origem ficam PRESAS por dentro da caixa nova — apagadas, senao a peca
        # fica com uma parede interior invisivel que estraga as sombras e o volume
        bmesh.ops.delete(bm, geom=fs, context='FACES')
        bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
        _res['tocados'] = len(fs)

    elif _OP == 'bisel':
        es = perto(bm.edges, _ce)
        bmesh.ops.bevel(bm, geom=es, offset=valor_loc, offset_type='OFFSET',
                        segments=_SEG, profile=0.5, affect='EDGES')
        _res['tocados'] = len(es)

    elif _OP == 'inset':
        fs = perto(bm.faces, _cf)
        bmesh.ops.inset_region(bm, faces=fs, thickness=valor_loc, depth=0.0,
                               use_even_offset=True, use_boundary=True)
        bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
        _res['tocados'] = len(fs)

    elif _OP == 'subdividir':
        # subdivide-se pelas FACES tocadas e nao pelas arestas soltas: cortar tres arestas ao
        # calhas deixa triangulos por todo o lado; cortar as arestas de uma face inteira com
        # use_grid_fill mantem quads.
        fs = perto(bm.faces, _cf)
        es = set()
        for f in fs:
            for e in f.edges: es.add(e)
        bmesh.ops.subdivide_edges(bm, edges=list(es), cuts=_SEG, use_grid_fill=True)
        _res['tocados'] = len(fs)

    elif _OP == 'suavizar':
        vs = perto(bm.verts, _cv)
        f = 0.5 if _VALOR is None else max(0.0, min(1.0, _VALOR))
        for _ in range(_SEG):
            bmesh.ops.smooth_vert(bm, verts=vs, factor=f,
                                  use_axis_x=True, use_axis_y=True, use_axis_z=True)
        _res['tocados'] = len(vs); _res['forca'] = f

    elif _OP == 'apagar':
        fs = perto(bm.faces, _cf)
        bmesh.ops.delete(bm, geom=fs, context='FACES')
        _res['tocados'] = len(fs)

    elif _OP == 'fundir':
        vs = perto(bm.verts, _cv)
        if len(vs) < 2:
            raise RuntimeError('fundir precisa de pelo menos dois vertices; toca em dois sitios')
        c = Vector((0.0, 0.0, 0.0))
        for v in vs: c += v.co
        c /= len(vs)
        bmesh.ops.pointmerge(bm, verts=vs, merge_co=c)
        _res['tocados'] = len(vs)

    else:
        raise RuntimeError('operacao desconhecida: ' + _OP)

    depois = {'verts': len(bm.verts), 'arestas': len(bm.edges), 'faces': len(bm.faces)}
    _res['depois'] = depois
    _res['desvio'] = round(_desvio[0], 6)   # pior distancia ponto tocado -> elemento escolhido
    # MUDOU ALGUMA COISA? Contagens iguais nao provam que nao (mover nao cria geometria), mas
    # contagens diferentes provam que sim. O lado C# usa isto mais o 'tocados'.
    _res['mudou'] = (antes != depois) or _OP in ('mover','mover_x','mover_y','mover_z','suavizar')
    _res['ok'] = True

    bm.to_mesh(me)
    me.update()
    bm.free()

except Exception as _e:
    _res['ok'] = False
    _res['erro'] = str(_e)

print('KLIP MESHOP ->', json.dumps(_res))
""";
}
