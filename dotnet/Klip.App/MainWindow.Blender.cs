using System;
using System.Diagnostics;
using System.Linq;
using Avalonia.Controls;
using Klip.Engine.Blender;

namespace Klip.App;

/// <summary>
/// PONTE BLENDER (verbo blender_render do bus de IA). O KLIP é 2D/2.5D em Skia; quando o pedido
/// exige path-tracing a sério (produto fotorreal, vidro, GI), a IA escreve um script Python e
/// manda-o ao Blender headless — o PNG que volta entra na tela como qualquer outro asset.
/// Fica em ficheiro próprio para não engordar o MainWindow.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>
    /// blender_object: o Blender MODELA e o KLIP fica com a MALHA — não com uma fotografia dela.
    /// A camada resultante é um objeto 3D a sério na cena: rodas, iluminas, animas e keyframas
    /// com o mesmo motor PBR/IBL das outras camadas. É a diferença entre ter o render e ter a peça.
    /// </summary>
    public object ApiBlenderObject(string script, string? name, double? timeoutSec)
    {
        if (string.IsNullOrWhiteSpace(script)) throw new InvalidOperationException("script vazio");
        if (!BlenderBridge.IsAvailable)
            throw new InvalidOperationException("Blender não encontrado. Define KLIP_BLENDER com o caminho do blender.exe.");

        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "klip_meshes");
        System.IO.Directory.CreateDirectory(dir);
        var stem = "klip_" + Guid.NewGuid().ToString("N")[..10];
        var obj = System.IO.Path.Combine(dir, stem + ".glb");
        var blend = System.IO.Path.Combine(dir, stem + ".blend");

        // O script do utilizador só CONSTRÓI. A exportação é nossa — a IA não precisa de saber o
        // nome do operador (muda entre versões) e nunca se esquece dela.
        //
        // GLB, NÃO OBJ: o OBJ só traz triângulos e UVs — perde materiais, hierarquia e cores de
        // vértice, e por isso a malha chegava cá cinzenta. O glTF atravessa com o PBR intacto.
        // E gravamos o .blend ao lado: é a FONTE, o único ficheiro que não perde nada, e é o que
        // permite voltar a mexer no objeto em vez de o refazer do zero.
        var full = script + @"

# ---- KLIP: guardar a fonte e exportar a malha para o editor ----
import bpy as _bpy, sys as _sys
_args = _sys.argv[_sys.argv.index('--') + 1:]
_out, _blend = _args[0], _args[1]
try:
    _bpy.ops.wm.save_as_mainfile(filepath=_blend)     # a FONTE, sem perdas
except Exception as _e:
    print('KLIP: .blend nao gravou:', _e)
try:
    _bpy.ops.object.select_all(action='SELECT')
except Exception:
    pass
" + BakeTextures.Script() + @"
try:
    _bpy.ops.object.select_all(action='SELECT')
except Exception:
    pass
# export_yup=True e NAO False: o Blender e Z-up, o motor do KLIP (e o proprio glTF) sao Y-up.
# Com False, tudo o que se modelava chegava DEITADO de costas para a camara.
_bpy.ops.export_scene.gltf(filepath=_out, export_format='GLB',
                           export_apply=True, export_materials='EXPORT',
                           export_normals=True, export_yup=True)
print('KLIP GLB ->', _out)
";
        UiChat("·", $"Blender {BlenderBridge.Version} a modelar…");
        var sw = Stopwatch.StartNew();
        var r = BlenderBridge.RunScript(full, new[] { obj, blend },
            TimeSpan.FromSeconds(Math.Clamp(timeoutSec ?? 600, 5, 7200)),
            line => { if (line.Contains("KLIP GLB")) UiChat("·", "malha exportada"); });
        sw.Stop();

        if (!System.IO.File.Exists(obj) || new System.IO.FileInfo(obj).Length == 0)
            throw new InvalidOperationException("o Blender não exportou malha nenhuma.\n" + r.ErrorTail(600));

        // o material vem no .glb — lemo-lo aqui para a camada nascer com o aspeto que tinha lá dentro
        uint baseArgb = 0xFFBDBDC6; double metal = 0.0, rough = 0.4;
        try
        {
            var g = Klip.Engine.ThreeD.GltfMesh.Load(obj);
            baseArgb = g.pbr.BaseArgb; metal = g.pbr.Metal; rough = Math.Clamp(g.pbr.Rough, 0.04, 1.0);
        }
        catch { /* sem material no ficheiro → fica o cinzento neutro */ }

        var id = Avalonia.Threading.Dispatcher.UIThread.Invoke(
            () => AddMeshLayer(obj, blend, name, baseArgb, rough, metal));
        long kb = new System.IO.FileInfo(obj).Length / 1024;
        UiChat("·", $"objeto 3D na cena: {id} ({kb} KB) em {sw.Elapsed.TotalSeconds:0.0}s");
        return new
        {
            ok = true, id, mesh = obj,
            source = System.IO.File.Exists(blend) ? blend : null,   // a FONTE, para voltar a mexer
            kb, seconds = Math.Round(sw.Elapsed.TotalSeconds, 2),
        };
    }

    /// <summary>
    /// blender_edit: EDITAR o objeto que já está na cena, em vez de o refazer.
    ///
    /// Abre o .blend guardado (a fonte, com modificadores, nós de material e hierarquia intactos),
    /// corre o script de alteração lá dentro, volta a gravar a fonte e reexporta a malha. Reconstruir
    /// a partir do glTF perderia tudo o que não é triângulo — por isso é o .blend que manda.
    ///
    /// A camada mantém-se: só o ficheiro da malha muda. Como a chave da cache do compositor inclui a
    /// data de escrita do ficheiro, o objeto na tela actualiza-se sozinho.
    /// </summary>
    public object ApiBlenderEdit(string id, string script, double? timeoutSec)
    {
        if (string.IsNullOrWhiteSpace(script)) throw new InvalidOperationException("script vazio");
        if (!BlenderBridge.IsAvailable)
            throw new InvalidOperationException("Blender não encontrado. Define KLIP_BLENDER com o caminho do blender.exe.");

        int ix = FindLayer(id);
        var layer = Sel(id);
        var blend = layer.ThreeD?.SourceBlend;
        if (string.IsNullOrWhiteSpace(blend) || !System.IO.File.Exists(blend))
            throw new InvalidOperationException(
                $"a camada «{layer.Name}» não tem fonte .blend guardada, portanto não há o que editar. " +
                "Só objetos criados com blender_object trazem a fonte; para os importados de fora, remodela com blender_object.");

        // malha NOVA a cada edição: sobrescrever a antiga com o Blender ainda a ler dela dá ficheiros
        // truncados, e uma malha com o mesmo nome enganava a cache do compositor.
        var dir = System.IO.Path.GetDirectoryName(blend)!;
        var obj = System.IO.Path.Combine(dir, "klip_" + Guid.NewGuid().ToString("N")[..10] + ".glb");

        var full = script + @"

# ---- KLIP: regravar a fonte e reexportar a malha editada ----
import bpy as _bpy, sys as _sys
_args = _sys.argv[_sys.argv.index('--') + 1:]
_out, _blend = _args[0], _args[1]
try:
    _bpy.ops.wm.save_as_mainfile(filepath=_blend)
except Exception as _e:
    print('KLIP: .blend nao regravou:', _e)
try:
    _bpy.ops.object.select_all(action='SELECT')
except Exception:
    pass
" + BakeTextures.Script() + @"
try:
    _bpy.ops.object.select_all(action='SELECT')
except Exception:
    pass
_bpy.ops.export_scene.gltf(filepath=_out, export_format='GLB',
                           export_apply=True, export_materials='EXPORT',
                           export_normals=True, export_yup=True)
print('KLIP GLB ->', _out)
";
        UiChat("·", $"Blender a editar «{layer.Name}»…");
        var sw = Stopwatch.StartNew();
        var r = BlenderBridge.RunScriptOnBlend(blend!, full, new[] { obj, blend! },
            TimeSpan.FromSeconds(Math.Clamp(timeoutSec ?? 600, 5, 7200)),
            line => { if (line.Contains("KLIP GLB")) UiChat("·", "malha reexportada"); });
        sw.Stop();

        if (!System.IO.File.Exists(obj) || new System.IO.FileInfo(obj).Length == 0)
            throw new InvalidOperationException("a edição não produziu malha nenhuma.\n" + r.ErrorTail(600));

        uint baseArgb = layer.FillArgb;
        double metal = layer.ThreeD?.Metal ?? 0.0, rough = layer.ThreeD?.Rough ?? 0.4;
        try
        {
            var g = Klip.Engine.ThreeD.GltfMesh.Load(obj);
            baseArgb = g.pbr.BaseArgb; metal = g.pbr.Metal; rough = Math.Clamp(g.pbr.Rough, 0.04, 1.0);
        }
        catch { /* sem material no ficheiro → fica o que a camada já tinha */ }

        var old = layer.ThreeD?.MeshPath;
        Avalonia.Threading.Dispatcher.UIThread.Invoke(() => Mutate(() =>
        {
            var l = _layers[ix];
            _layers[ix] = l with
            {
                FillArgb = baseArgb,
                ThreeD = (l.ThreeD ?? new Klip.Model.Extrude3D()) with
                {
                    MeshPath = obj, SourceBlend = blend, Rough = rough, Metal = metal,
                },
            };
        }));
        // a malha antiga já não é referida por ninguém
        if (old is { Length: > 0 } && old != obj) { try { System.IO.File.Delete(old); } catch { } }

        long kb = new System.IO.FileInfo(obj).Length / 1024;
        UiChat("·", $"«{layer.Name}» editado ({kb} KB) em {sw.Elapsed.TotalSeconds:0.0}s");
        return new
        {
            ok = true, id = layer.Name, mesh = obj, source = blend,
            kb, seconds = Math.Round(sw.Elapsed.TotalSeconds, 2),
        };
    }

    /// <summary>
    /// inspect_mesh: mede a topologia da malha de uma camada e devolve NÚMEROS.
    /// Existe para tirar a modelação do campo da opinião — ou a peça tem quads, sem ngons e sem
    /// pólos, ou não tem, e nesse caso diz-se o que corrigir.
    /// </summary>
    public object ApiInspectMesh(string id)
    {
        var layer = Sel(id);
        var blend = layer.ThreeD?.SourceBlend;
        if (string.IsNullOrWhiteSpace(blend) || !System.IO.File.Exists(blend))
            throw new InvalidOperationException(
                $"a camada «{layer.Name}» não tem fonte .blend, e um .glb não serve para medir topologia: " +
                "vem triangulado e com os modificadores já aplicados, portanto mediria a malha de exportação " +
                "e não a que se pode editar.");

        var r = Klip.Engine.Blender.MeshInspect.Inspecionar(blend!);
        return new
        {
            ok = true,
            id = layer.Name,
            objetos = r.Select(o => new
            {
                o.Objeto, o.Faces, quads = o.Quads, tris = o.Tris, ngons = o.Ngons,
                quad_pct = o.QuadPct, polos_6plus = o.PolosGrandes,
                duplicados = o.Duplicados, area_nula = o.AreaNula,
                subsurf = o.TemSubsurf ? $"{o.SubsurfViewport}/{o.SubsurfRender}" : "nenhum",
                suave = o.Suave, veredicto = o.Veredicto, reparos = o.Reparos,
            }).ToArray(),
        };
    }

    /// <summary>Cria a camada que segura a malha, já com o material que veio do .glb.</summary>
    private string AddMeshLayer(string meshPath, string blendPath, string? name,
                                uint argb, double rough, double metal)
    {
        string id = (string.IsNullOrWhiteSpace(name) ? "objeto" : name!.Trim()) + "-" + _nameSeq++;
        Mutate(() =>
        {
            _layers.Add(new Klip.Model.Layer(
                id,
                Klip.Model.MorphTrack.Static(Klip.Engine.Shapes.Rect(220, 220)),
                argb,
                Scale: Klip.Model.Track.Const(1.0),
                ThreeD: new Klip.Model.Extrude3D(Rough: rough, Metal: metal, MeshPath: meshPath,
                                                 SourceBlend: System.IO.File.Exists(blendPath) ? blendPath : null)));
            _selected = _layers.Count - 1;
        });
        return id;
    }

    /// <summary>
    /// blender_render: script Python → PNG. Corre em background (Background: true no bus) porque
    /// um render de Cycles em CPU leva minutos e travaria a UI thread inteira.
    /// Devolve "_image" para a IA VER o resultado sem ter de o inserir na tela primeiro.
    /// </summary>
    public object ApiBlenderRender(string script, string path, double? timeoutSec)
    {
        if (string.IsNullOrWhiteSpace(script)) throw new InvalidOperationException("script vazio");
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("path de saída vazio");
        if (!BlenderBridge.IsAvailable)
            throw new InvalidOperationException(
                "Blender não encontrado nesta máquina. Instala o Blender ou define KLIP_BLENDER com o caminho do blender.exe.");

        // A ponte entrega o destino ao script em sys.argv (depois de '--'), por isso não o inventamos aqui.
        path = System.IO.Path.GetFullPath(path);
        UiChat("·", $"Blender {BlenderBridge.Version} a renderizar… (CPU pode demorar)");

        var sw = Stopwatch.StartNew();

        // PROGRESSO AO VIVO: o Cycles imprime "Sample 12/96" no stdout. Sem isto o utilizador
        // fica minutos a olhar para nada, sem saber se está a trabalhar ou pendurado.
        int lastPct = -1;
        void OnLine(string line)
        {
            var m = System.Text.RegularExpressions.Regex.Match(line, @"Sample (\d+)/(\d+)");
            if (!m.Success) return;
            if (!int.TryParse(m.Groups[1].Value, out int cur) || !int.TryParse(m.Groups[2].Value, out int tot) || tot <= 0) return;
            int pct = (int)(100.0 * cur / tot);
            if (pct / 10 == lastPct / 10) return;      // só de 10 em 10% — senão inunda o chat
            lastPct = pct;
            var rem = System.Text.RegularExpressions.Regex.Match(line, @"Remaining:([\d:.]+)");
            string tail = rem.Success ? $" · faltam {rem.Groups[1].Value}" : "";
            UiChat("·", $"Blender a renderizar… {pct}%{tail}");
        }

        var outp = BlenderBridge.RenderStill(script, path,
            null, TimeSpan.FromSeconds(Math.Clamp(timeoutSec ?? 900, 5, 7200)), OnLine);
        sw.Stop();

        long bytes = new System.IO.FileInfo(outp).Length;

        // A IMAGEM ENTRA SOZINHA NA TELA. Corremos em background, por isso o documento
        // só pode ser mexido na UI thread — daí o Invoke.
        string? layerId = null;
        try
        {
            layerId = Avalonia.Threading.Dispatcher.UIThread.Invoke(() => ApiInsertImage(outp));
        }
        catch (Exception ex)
        {
            UiChat("·", "render pronto, mas não entrou na tela: " + ex.Message);
        }

        UiChat("·", $"Blender: {System.IO.Path.GetFileName(outp)} ({bytes / 1024} KB) em {sw.Elapsed.TotalSeconds:0.0}s"
                    + (layerId is null ? "" : " → na tela"));
        return new
        {
            ok = true,
            _image = outp,               // a IA recebe a imagem e pode criticá-la
            path = outp,
            id = layerId,                // camada criada na tela (null se falhou a inserção)
            bytes,
            seconds = Math.Round(sw.Elapsed.TotalSeconds, 2),
            blender = BlenderBridge.Version,
        };
    }
}
