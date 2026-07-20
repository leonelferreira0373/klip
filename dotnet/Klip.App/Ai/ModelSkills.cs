namespace Klip.App.Ai;

/// <summary>
/// RECEITAS DE MODELAÇÃO injectadas nas instruções da IA.
///
/// Tudo o que está aqui foi MEDIDO no Blender 5.2 headless, não copiado de tutoriais: contagens de
/// polígonos, perdas de volume e renders lado a lado. Os números estão no texto de propósito — a IA
/// segue melhor uma regra que traz a prova atrás.
/// </summary>
public static class ModelSkills
{
    public const string Modelacao = """
════ MODELAR COMO GENTE (medido no Blender 5.2, não é teoria) ════

A LEI DAS ARESTAS DE SUPORTE. Um cubo 2×2×2 (volume 8.0) com Subsurf nível 2 e mais nada
DERRETE: fica com volume 2.45 — perde 69% — e renderiza como uma ESFERA. O mesmo cubo com um
bevel de suporte antes do Subsurf fica com volume 7.84 (perde 2%) e lê-se como um cubo de
cantos macios. Num cilindro: 45% de perda contra 1.7%. As arestas de suporte não são
acabamento, são o que impede o Catmull-Clark de derreter a peça. NUNCA metas Subsurf sem elas.

    import bmesh
    bm = bmesh.new(); bm.from_mesh(ob.data)
    duras = [e for e in bm.edges if e.calc_face_angle(0) > 0.5]   # só as arestas vivas
    bmesh.ops.bevel(bm, geom=duras, offset=0.06, segments=2, affect='EDGES')
    bm.to_mesh(ob.data); bm.free()

O BOTÃO QUE DECIDE O ASPECTO é a LARGURA desse bevel — ela É o raio do arredondamento.
Medido no mesmo cubo, com o MESMO custo de 864 faces: 0.02 lê-se como caixa de CAD dura,
0.08 lê-se como produto desenhado, 0.15 lê-se como brinquedo mole. Portanto "parecer feito por
uma pessoa" é uma escolha, não uma questão de polígonos. Usa 2 a 5% da MENOR dimensão da peça,
segments=2 (3 só em grandes planos).

ARESTAS DE SUPORTE ou CREASE? Não são a mesma coisa e a diferença vê-se. Crease=1.0 mantém o
volume EXACTO (8.000002) porque desliga o Subsurf naquela aresta: dá um canto infinitamente
afiado, que não apanha brilho nenhum. No mundo real não existem arestas de raio zero — é
precisamente por isso que modelos só com crease continuam a parecer de computador. Regra:
arestas de suporte na silhueta e no que se vê de perto; crease no resto (custa 0 polígonos).
Em 5.2 o crease mudou de sítio:  bm.edges.layers.float.new('crease_edge')

A PILHA QUE FUNCIONA, por esta ordem (verificada: 100% quads, 0 tris, 0 ngons, 0 não-manifold,
estanque, 0.1% de encolhimento):
    1. bevel de suporte por bmesh (fica gravado na malha)
    2. me.shade_smooth() ; me.set_sharp_from_angle(angle=radians(30))
    3. SUBSURF  levels=2, render_levels=3, use_creases=True, use_limit_surface=True
    4. BEVEL modificador  width≈0.008, segments=2, limit_method='ANGLE'
    5. WELD  merge_threshold=1e-4
Define SEMPRE levels E render_levels — no mesmo objecto é uma diferença de 3× nos polígonos.

════ O QUE REBENTA EM HEADLESS (aprendido à martelada) ════

· bpy.ops.mesh.loopcut_slide MATA O PROCESSO. Não é uma excepção de Python, é um
  EXCEPTION_ACCESS_VIOLATION — o try/except NÃO apanha. NUNCA o escrevas.
  Corte de anel a sério: caminha o anel em bmesh e usa
  bmesh.ops.subdivide_edges(bm, edges=anel, cuts=3, use_grid_fill=True)   (neutro em volume)
· bpy.ops.mesh.knife_project precisa de uma janela 3D — não existe em -b.
· bmesh.ops.offset_edgeloops é uma armadilha: insere os anéis com deslocamento ZERO e deixa
  vértices duplicados coincidentes. Usa bmesh.ops.bevel, que insere E afasta numa só chamada.
· mesh.use_auto_smooth e mesh.auto_smooth_angle FORAM REMOVIDOS no 5.2. São as duas linhas mais
  comuns em código bpy gerado por IA e agora dão AttributeError. Em 5.2:
      me.shade_smooth(); me.set_sharp_from_angle(angle=radians(30))     # destrutivo, barato
      bpy.ops.object.shade_auto_smooth(angle=radians(30))               # não-destrutivo (nós)
  (for p in me.polygons: p.use_smooth = True  continua válido.)
· A maioria dos operadores de modo edição FUNCIONA em headless sem override de contexto
  (subdivide, bevel, inset, poke, tris_convert_to_quads, solidify, spin, symmetrize, unwrap…).
  Ainda assim prefere bmesh: sem estados de modo, sem selecção, sem superfície de crash.

════ MODIFICADORES NÃO-DESTRUTIVOS (todos verificados em headless) ════
MIRROR (use_clip + use_mirror_merge) — modela METADE e espelha; simetria perfeita e metade do
trabalho.  ARRAY + CURVE — repetição que segue uma curva, detalhe instantâneo.  SCREW — um perfil
de 5 vértices vira um vaso torneado de 128 faces, 100% quads.  SOLIDIFY (use_even_offset).
WELD para limpar depois de mirror/array.

════ TAPAS DE CILINDRO: O DELATOR ════
Todo o cilindro por omissão traz 2 NGONS de N lados nas tampas — é a assinatura mais visível de
"primitiva". Corrige com  end_fill_type='NOTHING'  e depois bmesh.ops.grid_fill.
GOTCHA: passar as DUAS bordas numa só chamada de grid_fill dá resultado partido. Uma chamada
por borda, separadamente.

════ COMO SE MEDE SE ESTÁ BOM ════
Corri a inspecção na Suzanne, que é modelada à mão por gente: 93.6% quads, 0 ngons, 0 pólos de
valência 6+. Mas também 42 não-manifold e não estanque — arte a sério falha essas. Logo, o que
separa feito-à-mão de gerado é isto, e só isto:
    quads >= 90% · ngons == 0 · pólos de valência 6+ == 0 · vértices duplicados == 0 · faces de área nula == 0
Usa o verbo inspect_mesh DEPOIS de modelar e corrige o que estiver vermelho. Uma icosfera dá 0%
quads e 150 pólos; um cilindro por omissão dá 2 ngons. Se vires isso, não entregues.

════ O CICLO, QUE NÃO É OPCIONAL ════
modelar → inspect_mesh (números) → render_frame (OLHAR) → criticar → blender_edit para corrigir.
Nunca entregues o primeiro resultado. Pergunta-te ao ver: a silhueta lê-se? os chanfros apanham
luz? as proporções entre as partes fazem sentido? há variação, ou está tudo igual ao milímetro?

════ NÃO FAÇAS ════
· Subsurf sem arestas de suporte (derrete — 69% de volume).
· harden_normals "porque sim": medido, deixou a peça MAIS CHATA que um bevel simples. Achata o
  degradê ao longo do chanfro. É só para mecânica dura, e com chanfros largos (>=5% da peça).
· Chanfros todos da mesma largura. Numa peça real a aresta principal é mais generosa que as
  secundárias — é aí que mora o "desenhado por alguém".
· Primitiva + bevel e entregar. Bloqueia a forma primeiro, silhueta antes do detalhe.
· Material procedural sem pensar: o glTF NÃO leva os nós. O KLIP assa as texturas na exportação,
  mas o que ele assa é o que tu ligaste ao Principled — se deixaste tudo em cor chapada, é cor
  chapada que sai. Dá ruído, riscos, sujidade nas cavidades: é isso que faz parecer real.
""";
}
