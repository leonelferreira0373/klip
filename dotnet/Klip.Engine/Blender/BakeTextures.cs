namespace Klip.Engine.Blender;

/// <summary>
/// COZER TEXTURAS ANTES DE EXPORTAR.
///
/// O glTF não transporta os nós do Blender. Um material procedural — ruído, gradiente, desgaste,
/// mistura de shaders — existe enquanto o Blender o avalia; no momento da exportação colapsa para
/// uma cor chapada e dois escalares. É exactamente por isso que um objecto "cheio de material" no
/// Blender chega ao KLIP a parecer plástico pintado.
///
/// A solução é a mesma que qualquer pipeline de jogo usa: BAKE. Assar o resultado dos nós para
/// imagens (cor base, rugosidade/metálico, normais), ligá-las a um Principled limpo, e só então
/// exportar. O que se perde em flexibilidade ganha-se em fidelidade — e o .blend original fica
/// intacto ao lado, com os nós todos, para continuar a editar.
///
/// Este script é acrescentado ao pipeline de exportação e só actua quando é PRECISO: um material
/// que já seja uma cor constante não tem nada para assar e passa ao lado.
/// </summary>
public static class BakeTextures
{
    /// <param name="res">lado da textura assada (1024 é o compromisso: chega para ver, não faz o
    /// bake demorar minutos).</param>
    public static string Script(int res = 1024) => """
# ================= KLIP: assar materiais procedurais para imagens =================
import bpy, os, tempfile

_RES = __RES__
_dir = os.path.join(tempfile.gettempdir(), 'klip_bake')
os.makedirs(_dir, exist_ok=True)

def _precisa_assar(mat):
    # Só vale a pena assar se houver nós a MEXER no aspecto. Uma cor constante não tem nada
    # para assar, e assá-la seria trocar um valor exacto por uma imagem aproximada.
    if not mat or not mat.use_nodes or not mat.node_tree:
        return False
    for n in mat.node_tree.nodes:
        if n.type in {'TEX_NOISE', 'TEX_MUSGRAVE', 'TEX_VORONOI', 'TEX_WAVE', 'TEX_MAGIC',
                      'TEX_CHECKER', 'TEX_BRICK', 'TEX_GRADIENT', 'TEX_IMAGE',
                      'BUMP', 'NORMAL_MAP', 'MIX_SHADER', 'MIX_RGB', 'MIX',
                      'VALTORGB', 'MAPPING', 'ATTRIBUTE', 'VERTEX_COLOR', 'COLOR_RAMP'}:
            return True
    return False

def _garante_uv(ob):
    # Sem UVs não há onde assar. O smart project é feio para trabalho fino mas é honesto:
    # mais vale uma UV automática com textura do que uma peça sem textura nenhuma.
    if ob.data.uv_layers:
        return True
    try:
        bpy.context.view_layer.objects.active = ob
        bpy.ops.object.mode_set(mode='EDIT')
        bpy.ops.mesh.select_all(action='SELECT')
        bpy.ops.uv.smart_project(angle_limit=1.15, island_margin=0.02)
        bpy.ops.object.mode_set(mode='OBJECT')
        return True
    except Exception as e:
        print('KLIP bake: UV falhou em', ob.name, e)
        try: bpy.ops.object.mode_set(mode='OBJECT')
        except Exception: pass
        return False

def _assar(ob, mat, slot_ix, tipo, nome, nao_cor):
    img = bpy.data.images.new(f'{ob.name}_{slot_ix}_{nome}', _RES, _RES, alpha=False,
                              float_buffer=False, is_data=nao_cor)
    nt = mat.node_tree
    alvo = nt.nodes.new('ShaderNodeTexImage')
    alvo.image = img
    alvo.select = True
    nt.nodes.active = alvo            # o Cycles assa para o nó de imagem ACTIVO
    # ARMADILHA DO BAKE: por omissão o DIFFUSE assa cor VEZES iluminação. Numa cena sem luzes
    # (que é o caso quando só se quer a textura) sai tudo PRETO. Tem de se desligar os passes
    # de luz e ficar só com a cor — a iluminação é trabalho do motor do KLIP, não da textura.
    try:
        bk = bpy.context.scene.render.bake
        bk.use_pass_direct = False
        bk.use_pass_indirect = False
        bk.use_pass_color = True
    except Exception:
        pass
    try:
        bpy.ops.object.bake(type=tipo, use_clear=True, margin=8)
    except Exception as e:
        print('KLIP bake falhou:', nome, e)
        nt.nodes.remove(alvo)
        return None
    caminho = os.path.join(_dir, f'{ob.name}_{slot_ix}_{nome}.png')
    img.filepath_raw = caminho
    img.file_format = 'PNG'
    img.save()
    nt.nodes.remove(alvo)
    return img

def klip_assar_tudo():
    cena = bpy.context.scene
    motor_antes = cena.render.engine
    cena.render.engine = 'CYCLES'
    try:
        cena.cycles.samples = 8           # é uma textura, não um render final
        cena.cycles.use_denoising = False
    except Exception:
        pass

    assados = 0
    for ob in [o for o in bpy.context.scene.objects if o.type == 'MESH']:
        alvo_mats = [(i, s.material) for i, s in enumerate(ob.material_slots)
                     if _precisa_assar(s.material)]
        if not alvo_mats:
            continue
        bpy.ops.object.select_all(action='DESELECT')
        ob.select_set(True)
        bpy.context.view_layer.objects.active = ob
        if not _garante_uv(ob):
            continue

        for ix, mat in alvo_mats:
            nt = mat.node_tree
            bsdf = next((n for n in nt.nodes if n.type == 'BSDF_PRINCIPLED'), None)
            if bsdf is None:
                continue

            cor = _assar(ob, mat, ix, 'DIFFUSE', 'cor', False)
            nrm = _assar(ob, mat, ix, 'NORMAL', 'normal', True)

            # Ligar as imagens assadas a um Principled LIMPO: as ligações antigas apontam para os
            # nós procedurais, que o glTF vai deitar fora. Sem isto o bake seria trabalho perdido.
            if cor is not None:
                for l in list(nt.links):
                    if l.to_node == bsdf and l.to_socket.name == 'Base Color':
                        nt.links.remove(l)
                n = nt.nodes.new('ShaderNodeTexImage'); n.image = cor
                nt.links.new(n.outputs['Color'], bsdf.inputs['Base Color'])
            if nrm is not None:
                for l in list(nt.links):
                    if l.to_node == bsdf and l.to_socket.name == 'Normal':
                        nt.links.remove(l)
                n = nt.nodes.new('ShaderNodeTexImage'); n.image = nrm
                n.image.colorspace_settings.name = 'Non-Color'
                nm = nt.nodes.new('ShaderNodeNormalMap')
                nt.links.new(n.outputs['Color'], nm.inputs['Color'])
                nt.links.new(nm.outputs['Normal'], bsdf.inputs['Normal'])
            assados += 1

    cena.render.engine = motor_antes
    print(f'KLIP BAKE -> {assados} material(is) assado(s)')

try:
    klip_assar_tudo()
except Exception as _e:
    print('KLIP bake: ignorado —', _e)
""".Replace("__RES__", res.ToString());
}
