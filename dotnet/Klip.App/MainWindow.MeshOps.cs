using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Avalonia.Controls;
using Klip.Engine.Blender;
using Klip.Engine.ThreeD;

namespace Klip.App;

/// <summary>
/// EDITAR A MALHA À MÃO, DENTRO DO KLIP.
///
/// O dono aponta para um sítio da peça na tela e diz o que quer ali. A janela do Blender nunca
/// aparece: o Blender é o MOTOR, as mãos ficam do lado de cá. Este ficheiro é a costura entre as
/// duas coisas — recebe pontos no espaço do OBJECTO do KLIP (o que o picking devolve), traduz-os
/// para as coordenadas do .blend e manda o Blender headless operar por proximidade.
///
/// Porque é por proximidade e não por índice: a exportação corre com export_apply=True, aplica os
/// modificadores e tritura a malha em triângulos. O vértice nº 412 que o KLIP tem em memória não é
/// o vértice nº 412 do .blend — não existe lá. A posição é a única identidade que sobrevive à
/// viagem, e é por isso que a operação anda toda à volta dela.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>
    /// mesh_op: aplica uma operação de modelação nos pontos tocados de uma camada 3D.
    /// </summary>
    /// <param name="id">camada (Id estável ou nome).</param>
    /// <param name="operacao">um dos verbos de <see cref="MeshOps.Catalogo"/>.</param>
    /// <param name="pontosJson">pontos em espaço de OBJECTO do KLIP: <c>[[x,y,z],…]</c>
    /// (aceita também um só ponto, <c>[x,y,z]</c>).</param>
    /// <param name="valor">o significado muda com a operação — distância, espessura ou força.
    /// Null deixa a operação escolher algo proporcional à peça, para o efeito ser sempre visível.</param>
    public object ApiMeshOp(string id, string operacao, string pontosJson, double? valor)
    {
        if (string.IsNullOrWhiteSpace(operacao)) throw new InvalidOperationException("operação vazia");
        operacao = operacao.Trim().ToLowerInvariant();
        if (!MeshOps.Suporta(operacao))
            throw new InvalidOperationException(
                $"não conheço a operação «{operacao}». As que existem: {MeshOps.Verbos}.");
        if (!BlenderBridge.IsAvailable)
            throw new InvalidOperationException("Blender não encontrado. Define KLIP_BLENDER com o caminho do blender.exe.");

        int ix = FindLayer(id);
        var layer = Sel(id);

        // Sem .blend não há o que editar: o .glb é o produto da exportação — vem triangulado, com
        // os modificadores já cozidos, e mexer nele seria mexer na fotografia em vez da peça.
        var blend = layer.ThreeD?.SourceBlend;
        if (string.IsNullOrWhiteSpace(blend) || !System.IO.File.Exists(blend))
            throw new InvalidOperationException(
                $"a camada «{layer.Name}» não tem fonte .blend guardada, portanto não há malha para editar à mão. " +
                "Só os objectos criados com blender_object trazem a fonte; para os que vieram de fora, remodela com blender_object.");

        // As partes servem para uma coisa só: desfazer a normalização (o leitor encolhe tudo para
        // caber em 1 unidade). Sem o centro e o factor, o ponto tocado não corresponde a nada lá dentro.
        // pela cache com tranca do modo malha (MainWindow.MeshEdit.cs) e não directo pelo leitor:
        // este método corre fora da thread da UI e o Dictionary do GltfMesh não é seguro a três
        var parts = MalhaPartes(layer);
        if (parts is null || parts.Count == 0)
            throw new InvalidOperationException(
                $"a camada «{layer.Name}» não tem malha carregada — não dá para saber onde tocaste.");
        var meshPath = layer.ThreeD!.MeshPath ?? "";
        var ext = System.IO.Path.GetExtension(meshPath).ToLowerInvariant();
        if (ext is not (".glb" or ".gltf"))
            throw new InvalidOperationException(
                "a edição à mão só funciona sobre malhas .glb/.gltf — são as únicas que trazem o centro e a " +
                "escala originais, e sem eles o ponto tocado não se converte em coordenadas do .blend.");

        var pontosKlip = LerPontos(pontosJson);
        if (pontosKlip.Count == 0)
            throw new InvalidOperationException("não veio nenhum ponto: não há onde aplicar a operação.");

        // Todas as partes partilham a mesma normalização (é global no leitor), portanto a primeira
        // serve para converter tudo.
        var p0 = parts[0];
        var pontosBlender = new List<Vector3>(pontosKlip.Count);
        foreach (var p in pontosKlip) pontosBlender.Add(p0.ParaBlender(p));

        // Malha NOVA a cada operação: sobrescrever a antiga com o Blender ainda a ler dela dá
        // ficheiros truncados, e o mesmo nome enganaria a cache do compositor.
        var dir = System.IO.Path.GetDirectoryName(blend)!;
        var obj = System.IO.Path.Combine(dir, "klip_" + Guid.NewGuid().ToString("N")[..10] + ".glb");

        // O preâmbulo é o MESMO do blender_edit — regravar a fonte, assar os materiais procedurais
        // e reexportar. Divergir daqui seria ter dois pipelines de exportação a envelhecer em separado.
        var full = MeshOps.Script(operacao, pontosBlender, valor) + @"

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
        UiChat("·", $"malha: {operacao} em «{layer.Name}»…");
        var sw = Stopwatch.StartNew();
        var r = BlenderBridge.RunScriptOnBlend(blend!, full, new[] { obj, blend! },
            TimeSpan.FromSeconds(180));
        sw.Stop();

        // O RECIBO manda mais do que o código de saída: com -P, uma excepção no Python ainda sai
        // com 0. Se o script disse que falhou, não se troca malha nenhuma — a camada fica como estava.
        var linhaRecibo = MeshOps.Recibo(r.StdOut);
        if (linhaRecibo is null)
        {
            try { System.IO.File.Delete(obj); } catch { }
            throw new InvalidOperationException("o Blender não devolveu recibo da operação.\n" + r.ErrorTail(600));
        }
        var info = Recibo.Ler(linhaRecibo);
        if (info.Erro is { Length: > 0 })
        {
            try { System.IO.File.Delete(obj); } catch { }
            throw new InvalidOperationException($"a operação «{operacao}» não se aplicou: {info.Erro}");
        }
        // recibo ilegível conta como falha: sem ele não se sabe SE mexeu, e trocar a malha às
        // cegas era dar por boa uma operação que pode ter passado ao lado da peça toda.
        if (info.Objecto.Length == 0 && info.VertsDepois == 0)
        {
            try { System.IO.File.Delete(obj); } catch { }
            throw new InvalidOperationException("o recibo do Blender veio ilegível: " + linhaRecibo);
        }

        if (!System.IO.File.Exists(obj) || new System.IO.FileInfo(obj).Length == 0)
            throw new InvalidOperationException("a operação não produziu malha nenhuma.\n" + r.ErrorTail(600));

        uint baseArgb = layer.FillArgb;
        double metal = layer.ThreeD?.Metal ?? 0.0, rough = layer.ThreeD?.Rough ?? 0.4;
        try
        {
            var g = GltfMesh.Load(obj);
            baseArgb = g.pbr.BaseArgb; metal = g.pbr.Metal; rough = Math.Clamp(g.pbr.Rough, 0.04, 1.0);
        }
        catch { /* sem material no ficheiro → fica o que a camada já tinha */ }

        var old = layer.ThreeD?.MeshPath;
        Avalonia.Threading.Dispatcher.UIThread.Invoke(() =>
        {
            // O ÍNDICE é reencontrado AGORA. O que veio do FindLayer lá em cima tem 7 segundos de
            // idade (o Blender demora isso) e este método corre fora da thread da UI — se entretanto
            // se apagou ou reordenou uma camada, escrevia-se por cima da errada.
            int jx = FindLayer(id);
            if (jx < 0) jx = FindLayer(layer.Name);
            if (jx < 0) throw new InvalidOperationException($"a camada «{layer.Name}» desapareceu durante a operação.");
            Mutate(() =>
            {
                var l = _layers[jx];
                _layers[jx] = l with
                {
                    FillArgb = baseArgb,
                    ThreeD = (l.ThreeD ?? new Klip.Model.Extrude3D()) with
                    {
                        MeshPath = obj, SourceBlend = blend, Rough = rough, Metal = metal,
                    },
                };
            });
            // A MALHA ANTIGA SÓ SE APAGA SE JÁ NINGUÉM A APONTAR. O histórico guarda listas de
            // camadas, e essas camadas guardam CAMINHOS de ficheiros — apagar o .glb anterior
            // deixava o undo a apontar para um ficheiro que já não existe, e o Hybrid3D, sem
            // ficheiro, deixa de desenhar a peça: desfazer fazia o objecto DESAPARECER.
            if (old is { Length: > 0 } && old != obj && !MalhaFicheiroEmUso(old))
            { try { System.IO.File.Delete(old); } catch { } }
        });

        UiChat("·", $"{operacao}: {info.Tocados} elemento(s) · {info.VertsAntes}→{info.VertsDepois} vértices, "
                    + $"{info.FacesAntes}→{info.FacesDepois} faces em {sw.Elapsed.TotalSeconds:0.0}s");

        // O motor escolhe SEMPRE o elemento mais próximo, mesmo que esteja longe — sem este aviso,
        // uma operação aplicada a meia peça de distância parece exactamente igual a uma boa.
        if (info.Tolerancia > 0 && info.Desvio > info.Tolerancia * 1.5)
            UiChat("·", $"⚠ o ponto tocado ficou a {info.Desvio:0.####} do elemento mais próximo "
                        + $"(tolerância {info.Tolerancia:0.####}) — provavelmente não foi aí que quiseste. "
                        + "Se a peça tem Subsurf, a superfície que vês está afastada da malha editável.");
        return new
        {
            ok = true,
            id = layer.Name,
            operacao,
            mesh = obj,
            source = blend,
            objecto = info.Objecto,
            pontos = pontosKlip.Count,
            tocados = info.Tocados,
            tolerancia = info.Tolerancia,
            desvio = info.Desvio,
            valor = info.Valor,
            antes = new { verts = info.VertsAntes, arestas = info.ArestasAntes, faces = info.FacesAntes },
            depois = new { verts = info.VertsDepois, arestas = info.ArestasDepois, faces = info.FacesDepois },
            mudou = info.Mudou,
            seconds = Math.Round(sw.Elapsed.TotalSeconds, 2),
        };
    }

    /// <summary>
    /// Alguém ainda aponta para este ficheiro de malha — a cena actual ou QUALQUER estado do
    /// histórico? Enquanto apontar, o ficheiro fica: é o que mantém o undo a funcionar.
    /// (Corre na thread da UI, que é a dona de <c>_layers</c> e <c>_hist</c>.)
    /// </summary>
    private bool MalhaFicheiroEmUso(string caminho)
    {
        static bool Igual(string? a, string b)
            => a is { Length: > 0 } && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        foreach (var l in _layers) if (Igual(l.ThreeD?.MeshPath, caminho)) return true;
        foreach (var estado in _hist)
            foreach (var l in estado) if (Igual(l.ThreeD?.MeshPath, caminho)) return true;
        return false;
    }

    /// <summary>Os verbos disponíveis, com o que cada «valor» significa. Existe para a IA (e o
    /// painel) não terem de adivinhar nem de manter uma cópia da lista.</summary>
    public object ApiMeshOps()
        => new
        {
            ok = true,
            operacoes = Array.ConvertAll(MeshOps.Catalogo,
                o => new { nome = o.Nome, faz = o.Descricao, valor = o.Unidade }),
        };

    /// <summary>
    /// Lê <c>[[x,y,z],…]</c> ou <c>[x,y,z]</c>. Aceitar o ponto solto sem embrulho é de propósito:
    /// é a forma que sai naturalmente de um clique único e obrigar a embrulhá-lo só criava erros.
    /// </summary>
    private static List<Vector3> LerPontos(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException("pontos vazios — esperava [[x,y,z],…] em espaço de objecto.");
        List<Vector3> saida = new();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("os pontos têm de vir num array.");

            bool planos = root.GetArrayLength() > 0 && root[0].ValueKind == JsonValueKind.Number;
            if (planos) { saida.Add(Ponto(root)); return saida; }

            foreach (var e in root.EnumerateArray()) saida.Add(Ponto(e));
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("os pontos não são JSON válido: " + ex.Message);
        }
        return saida;

        static Vector3 Ponto(JsonElement e)
        {
            if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() < 3)
                throw new InvalidOperationException("cada ponto tem de ser [x,y,z].");
            return new Vector3((float)e[0].GetDouble(), (float)e[1].GetDouble(), (float)e[2].GetDouble());
        }
    }

    /// <summary>
    /// O recibo que o script imprime, lido para números. É o que separa «correu» de «fez alguma
    /// coisa» — sem isto, uma operação que passou ao lado da peça toda dava exactamente o mesmo
    /// aspecto de sucesso que uma que funcionou.
    /// </summary>
    private readonly record struct Recibo(
        string Objecto, int Tocados, double Tolerancia, double Valor, bool Mudou, string? Erro,
        int VertsAntes, int ArestasAntes, int FacesAntes,
        int VertsDepois, int ArestasDepois, int FacesDepois, double Desvio)
    {
        public static Recibo Ler(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return default;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;
                var a = Sub(r, "antes"); var d = Sub(r, "depois");
                bool ok = !r.TryGetProperty("ok", out var okv) || okv.ValueKind != JsonValueKind.False;
                return new Recibo(
                    S(r, "objecto"), I(r, "tocados"), D(r, "tolerancia"), D(r, "valor"),
                    r.TryGetProperty("mudou", out var m) && m.ValueKind == JsonValueKind.True,
                    ok ? null : (S(r, "erro") is { Length: > 0 } e ? e : "o script não disse porquê"),
                    I(a, "verts"), I(a, "arestas"), I(a, "faces"),
                    I(d, "verts"), I(d, "arestas"), I(d, "faces"), D(r, "desvio"));
            }
            catch (JsonException) { return default; }
        }

        private static JsonElement Sub(JsonElement e, string k) => e.TryGetProperty(k, out var v) ? v : default;
        private static int I(JsonElement e, string k)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.TryGetInt32(out var i) ? i : 0;
        private static double D(JsonElement e, string k)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.TryGetDouble(out var d) ? d : 0;
        private static string S(JsonElement e, string k)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) ? (v.GetString() ?? "") : "";
    }
}
