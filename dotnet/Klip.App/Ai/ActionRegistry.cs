using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace Klip.App.Ai;

/// <summary>
/// The command bus of the .NET KLIP — one registry of actions that BOTH the UI and the AI drive.
/// Calls arrive on the HTTP thread and are marshaled to the Avalonia UI thread.
/// Manifest shape matches the legacy bridge: {name, description, params:{prop:{type,description}}, required:[]}.
/// </summary>
public sealed class ActionRegistry
{
    private readonly MainWindow _w;

    public ActionRegistry(MainWindow w) => _w = w;

    private sealed record Act(string Description,
        Dictionary<string, object> Params, string[] Required,
        Func<JsonElement, object?> Run, bool Background = false,
        Func<JsonElement, Task<object?>>? RunAsync = null);   // Fase 8: verbos async (WebView2 InvokeScript)

    private Dictionary<string, Act>? _acts;

    private static Dictionary<string, object> P(params (string n, string t, string d)[] ps)
        => ps.ToDictionary(p => p.n, p => (object)new { type = p.t, description = p.d });

    private Dictionary<string, Act> Acts => _acts ??= new()
    {
        ["get_state"] = new("Estado do documento: canvas, nº de camadas, seleção.",
            P(), Array.Empty<string>(), _ => _w.ApiState()),

        ["export_view"] = new("DIAGNÓSTICO: dump do estado da vista + PNG exato do que o canvas mostra.",
            P(("path", "string", ".png absoluto")), new[] { "path" },
            a => _w.ApiExportView(Str(a, "path") ?? "")),

        ["list_items"] = new("Lista as camadas (id=nome, transform, fill, clip).",
            P(), Array.Empty<string>(), _ => _w.ApiListItems()),

        ["insert_shape"] = new("Insere forma: star|circle|rect|squircle. Opcional size, fill (#RRGGBB), x, y.",
            P(("shape", "string", "star|circle|rect|squircle"), ("size", "number", "raio/meia-largura (default 120)"),
              ("fill", "string", "#RRGGBB"), ("x", "number", "offset do centro"), ("y", "number", "offset do centro")),
            new[] { "shape" },
            a => _w.ApiInsertShape(Str(a, "shape") ?? "circle", Num(a, "size") ?? 120,
                                   Str(a, "fill"), Num(a, "x") ?? 0, Num(a, "y") ?? 0)),

        ["insert_text"] = new("Insere texto como contornos vetoriais editáveis. Acentos PT (ç/ã/õ/é) OK. family=fonte de sistema ou carregada por load_font.",
            P(("text", "string", "o texto"), ("size", "number", "tamanho da fonte (default 120)"),
              ("fill", "string", "#RRGGBB"), ("x", "number", ""), ("y", "number", ""),
              ("family", "string", "fonte de sistema ou carregada por load_font (default Segoe UI)")),
            new[] { "text" },
            a => _w.ApiInsertText(Str(a, "text") ?? "", Num(a, "size") ?? 120,
                                  Str(a, "fill"), Num(a, "x") ?? 0, Num(a, "y") ?? 0, Str(a, "family"))),

        ["load_font"] = new("Carrega/baixa uma fonte e regista-a: name (ex. 'Bebas Neue' → baixa do Google Fonts), caminho .ttf/.otf/.ttc do disco, ou URL. Devolve a family a usar em insert_text/set_font. Cacheada em %APPDATA%\\Klip\\fonts.",
            P(("name", "string", "nome, caminho ou URL")),
            new[] { "name" },
            a => _w.ApiLoadFont(Str(a, "name") ?? ""), Background: true),

        ["set_font"] = new("Muda a fonte de uma camada de texto (re-bake) OU, sem id, define a fonte por omissão dos próximos insert_text.",
            P(("id", "string", "camada de texto (opcional)"), ("family", "string", "fonte de sistema ou carregada")),
            new[] { "family" },
            a => _w.ApiSetFont(Str(a, "id"), Str(a, "family") ?? "Segoe UI")),

        ["reorder"] = new("Reordena a camada no z-index: mode=front (topo) | back (fundo) | forward | backward.",
            P(("id", "string", ""), ("mode", "string", "front|back|forward|backward")),
            new[] { "id", "mode" },
            a => _w.ApiReorder(Str(a, "id") ?? "", Str(a, "mode") ?? "front")),

        ["duplicate"] = new("Duplica uma camada COM todos os keyframes/animação. Devolve o id da cópia (fica por cima do original).",
            P(("id", "string", "")),
            new[] { "id" },
            a => _w.ApiDuplicate(Str(a, "id") ?? "")),

        ["stagger"] = new("A MESMA animação a várias camadas com desfasamento (o clássico stagger). ids=lista por vírgulas; path=propriedade (opacity|position.y|scale|color.fill…); from/to=valor OU #hex; duration=s de cada animação; offset=atraso entre camadas (s).",
            P(("ids", "string", "ids separados por vírgula"), ("path", "string", ""), ("from", "string", "valor ou #hex"),
              ("to", "string", "valor ou #hex"), ("duration", "number", "s"), ("offset", "number", "atraso entre camadas (s)"), ("ease", "string", "")),
            new[] { "ids", "path", "from", "to", "duration", "offset" },
            a => _w.ApiStagger((Str(a, "ids") ?? "").Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries),
                               Str(a, "path") ?? "opacity", Str(a, "from") ?? "0", Str(a, "to") ?? "1",
                               Num(a, "duration") ?? 0.5, Num(a, "offset") ?? 0.1, Str(a, "ease") ?? "linear")),

        ["insert_path"] = new("Insere um caminho SVG 'd' (coords centradas no canvas).",
            P(("d", "string", "SVG path data"), ("fill", "string", "#RRGGBB")),
            new[] { "d" },
            a => _w.ApiInsertPath(Str(a, "d") ?? "", Str(a, "fill"))),

        ["set_transform"] = new("Move/escala/roda uma camada por id.",
            P(("id", "string", "nome da camada"), ("x", "number", ""), ("y", "number", ""),
              ("scale", "number", ""), ("rotation", "number", "graus")),
            new[] { "id" },
            a => _w.ApiSetTransform(Str(a, "id") ?? "", Num(a, "x"), Num(a, "y"),
                                    Num(a, "scale"), Num(a, "rotation"))),

        ["set_fill"] = new("Cor/gradiente PROFUNDO: fill+fill2 → gradiente; radial bool; angle graus (direção); mid 0-1 (ponto médio); spread 0.02-1 (velocidade da transição — menor = mais rápida).",
            P(("id", "string", ""), ("fill", "string", "#RRGGBB"), ("fill2", "string", "#RRGGBB ou vazio"),
              ("radial", "boolean", ""), ("angle", "number", "graus, 90=vertical"),
              ("mid", "number", "0-1"), ("spread", "number", "0.02-1")),
            new[] { "id", "fill" },
            a => _w.ApiSetFill(Str(a, "id") ?? "", Str(a, "fill") ?? "#000000",
                               Str(a, "fill2"), Bool(a, "radial") ?? false,
                               Num(a, "angle"), Num(a, "mid"), Num(a, "spread"))),

        ["remove_background"] = new("Remove o fundo de uma camada de imagem (u2netp leve, on-device). Devolve ms.",
            P(("id", "string", "camada de imagem")), new[] { "id" },
            a => _w.ApiRemoveBackground(Str(a, "id") ?? "")),

        ["insert_image"] = new("Insere uma imagem (png/jpg/webp) do disco como camada.",
            P(("path", "string", "caminho absoluto da imagem")), new[] { "path" },
            a => new { id = _w.ApiInsertImage(Str(a, "path") ?? "") ?? throw new InvalidOperationException("imagem ilegível") }),

        ["set_gradient"] = new("Gradiente MULTI-STOP (2 a 8 paragens) no preenchimento. stops=\"#RRGGBB@0, #RRGGBB@0.35, #RRGGBB@1\" (o @pos é opcional — sem ele distribui igualmente). kind=linear|radial|conic. angle=graus (90=topo→fundo). cx/cy=0-1 centro (radial/cónico). radius=0-1 fracção do maior lado. tile=clamp|repeat|mirror. SUBSTITUI o gradiente inteiro; para mexer numa só paragem usa set_stop.",
            P(("id", "string", "camada"), ("stops", "string", "\"#RRGGBB@pos, …\""), ("kind", "string", "linear|radial|conic"),
              ("angle", "number", "graus"), ("cx", "number", "0-1"), ("cy", "number", "0-1"),
              ("radius", "number", "0-1"), ("tile", "string", "clamp|repeat|mirror")),
            new[] { "id", "stops" },
            a => _w.ApiSetGradient(Str(a, "id") ?? "", Str(a, "stops") ?? "", Str(a, "kind"),
                                   Num(a, "angle"), Num(a, "cx"), Num(a, "cy"), Num(a, "radius"), Str(a, "tile"))),

        ["get_gradient"] = new("Lê o gradiente COMPLETO de uma camada no tempo t: kind, angle, cx, cy, radius, tile e todas as paragens com cor+posição. Usa ANTES e DEPOIS de ajustar, para trabalhares por deltas em vez de reescrever tudo.",
            P(("id", "string", "camada"), ("t", "number", "tempo em s (default 0)")), new[] { "id" },
            a => _w.ApiGetGradient(Str(a, "id") ?? "", Num(a, "t") ?? 0)),

        ["set_stop"] = new("Ajuste MILIMÉTRICO de UMA paragem: index 0-based, color #RRGGBB(AA) e/ou pos 0-1. Não toca nas outras paragens nem na geometria.",
            P(("id", "string", "camada"), ("index", "number", "0-based"), ("color", "string", "#RRGGBB"), ("pos", "number", "0-1")),
            new[] { "id", "index" },
            a => _w.ApiSetStop(Str(a, "id") ?? "", (int)(Num(a, "index") ?? 0), Str(a, "color"), Num(a, "pos"))),

        ["add_stop"] = new("Insere uma paragem nova na posição pos (0-1) com a cor dada. Máximo 8.",
            P(("id", "string", "camada"), ("color", "string", "#RRGGBB"), ("pos", "number", "0-1")),
            new[] { "id", "color", "pos" },
            a => _w.ApiAddStop(Str(a, "id") ?? "", Str(a, "color") ?? "#000000", Num(a, "pos") ?? 0.5)),

        ["remove_stop"] = new("Remove a paragem index (0-based). Mínimo 2 paragens.",
            P(("id", "string", "camada"), ("index", "number", "0-based")), new[] { "id", "index" },
            a => _w.ApiRemoveStop(Str(a, "id") ?? "", (int)(Num(a, "index") ?? 0))),

        ["set_spot"] = new("Aplica uma cor SPOT pelo código (ex.: \"PANTONE 185 C\", \"HKS 43 K\", \"TOYO 0044\") no preenchimento, ou no contorno se target=stroke. A cor traz a chapa CMYK do livro, portanto atravessa o export_cmyk sem ser reinventada.",
            P(("id", "string", "camada"), ("code", "string", "ex.: PANTONE 185 C"), ("target", "string", "fill|stroke")),
            new[] { "id", "code" },
            a => _w.ApiSetSpot(Str(a, "id") ?? "", Str(a, "code") ?? "", Str(a, "target"))),

        ["find_spot"] = new("Dado um hex, devolve as N cores spot mais próximas por ΔE. Serve para traduzir uma paleta de ecrã para impressão.",
            P(("hex", "string", "#RRGGBB"), ("n", "number", "quantas (default 5)"), ("library", "string", "filtrar por livro, ex.: Solid Coated")),
            new[] { "hex" },
            a => _w.ApiFindSpot(Str(a, "hex") ?? "", (int)(Num(a, "n") ?? 5), Str(a, "library"))),

        ["list_spot"] = new("Procura cores spot cujo código/nome contenha o filtro (ex.: \"185\", \"Cool Gray\", \"Reflex\"). Devolve código + hex + CMYK.",
            P(("filter", "string", "texto a procurar"), ("limit", "number", "default 40"), ("library", "string", "filtrar por livro")),
            new[] { "filter" },
            a => _w.ApiListSpot(Str(a, "filter"), (int)(Num(a, "limit") ?? 40), Str(a, "library"))),

        ["list_palettes"] = new("Que livros de cor existem nesta máquina (PANTONE, HKS, TOYO, DIC, FOCOLTONE, TRUMATCH…) e quantas cores tem cada um.",
            P(), new string[0], a => _w.ApiListPalettes()),

        ["import_mesh"] = new("Traz um objeto 3D do disco (.glb/.gltf/.obj) para a cena, COM os materiais e texturas do ficheiro (um material por parte). Usa isto depois de modelares com blender_object, ou para ficheiros que o utilizador já tenha.",
            P(("path", "string", "caminho absoluto .glb/.gltf/.obj"), ("x", "number", "posição x (opcional)"),
              ("y", "number", "posição y (opcional)"), ("scale", "number", "escala (opcional)")),
            new[] { "path" },
            a => new { id = _w.ApiImportMesh(Str(a, "path") ?? "", Num(a, "x"), Num(a, "y"), Num(a, "scale")) }),

        ["insert_rive"] = new("Insere uma animação Rive (.riv) como camada — runtime C# nativo, toca na timeline e exporta em MP4/GIF. anim opcional (nome da animação).",
            P(("path", "string", "caminho .riv absoluto"), ("anim", "string", "nome da animação (opcional)")),
            new[] { "path" },
            a => _w.ApiInsertRive(Str(a, "path") ?? "", Str(a, "anim"))),

        ["insert_lottie"] = new("Insere uma animação Lottie (.json bodymovin) como camada — runtime C# nativo, toca na timeline e exporta em MP4/GIF.",
            P(("path", "string", "caminho .json absoluto")), new[] { "path" },
            a => _w.ApiInsertLottie(Str(a, "path") ?? "")),

        ["set_anchor"] = new("Ponto-âncora (pivô de rotação/escala) em coords locais. (0,0)=centro da forma.",
            P(("id", "string", ""), ("x", "number", ""), ("y", "number", "")), new[] { "id" },
            a => _w.ApiSetAnchor(Str(a, "id") ?? "", Num(a, "x") ?? 0, Num(a, "y") ?? 0)),

        ["set_parent"] = new("PARENTING (rigs): liga a camada 'id' a uma mãe 'parent' — o transform passa a ser relativo à mãe (move/roda a mãe → o filho segue, mas mantém a sua própria animação). parent vazio desliga.",
            P(("id", "string", "filho"), ("parent", "string", "mãe (vazio=desligar)")), new[] { "id" },
            a => _w.ApiSetParent(Str(a, "id") ?? "", Str(a, "parent"))),

        ["insert_null"] = new("NULL object / controlador — camada invisível só-transform. Anima o null e os filhos (parenteados a ele) seguem. O truque de 'costurar' rigs do estilo Apple.",
            P(("x", "number", "offset centro"), ("y", "number", "")), Array.Empty<string>(),
            a => _w.ApiInsertNull(Num(a, "x") ?? 0, Num(a, "y") ?? 0)),

        ["set_expression"] = new("MOTOR DE EXPRESSÕES. kind=code → JavaScript estilo AFTER EFFECTS no parâmetro 'code' (o valor final da expressão é o resultado). Scope: value, time, wiggle(freq,amp), valueAtTime(t), loopOut(), loopIn(), linear(t,tMin,tMax,v1,v2), ease/easeIn/easeOut(...), clamp(v,min,max), random(), degreesToRadians/radiansToDegrees, Math.*. Ex.: rotation code=\"time*90\"; y code=\"value + wiggle(2,30)\"; opacity code=\"linear(time,0,1,0,100)\". kind=spring/wiggle = presets rápidos. kind=none remove. Aplica a x|y|scale|scale_x|scale_y|rotation|opacity|trim_end|trim_start.",
            P(("id", "string", "camada"), ("prop", "string", "x|y|scale|rotation|opacity|…"),
              ("kind", "string", "code|spring|wiggle|none"),
              ("code", "string", "JavaScript AE (quando kind=code)"),
              ("freq", "number", "spring:bounciness / wiggle:Hz"),
              ("amount", "number", "spring:decay / wiggle:amplitude")),
            new[] { "id", "prop", "kind" },
            a => _w.ApiSetExpression(Str(a, "id") ?? "", Str(a, "prop") ?? "", Str(a, "kind") ?? "spring",
                                     Num(a, "freq"), Num(a, "amount"), Str(a, "code"))),

        ["extract_palette"] = new("Extrai a paleta da imagem (k-means) e gera o quadro masonry: blocos desiguais perfeitamente espaçados + hex em cima. id = camada de imagem.",
            P(("id", "string", "camada de imagem"), ("x", "number", "offset centro"), ("y", "number", "")),
            new[] { "id" },
            a => _w.ApiExtractPalette(Str(a, "id") ?? "", Num(a, "x") ?? 0, Num(a, "y") ?? 0)),

        ["export_svg"] = new("Exporta o canvas em SVG VERDADEIRO (vetores).",
            P(("path", "string", ".svg absoluto")), new[] { "path" },
            a => _w.ApiExportSvg(Str(a, "path") ?? "")),

        ["export_gif"] = new("Exporta a timeline em GIF animado (background).",
            P(("path", "string", ".gif absoluto")), new[] { "path" },
            a => _w.ApiExportGif(Str(a, "path") ?? "")),

        ["export_lottie"] = new("Exporta a timeline em Lottie JSON (bodymovin) — transform+trim+fill/stroke; imagens/3D ficam de fora (skipped).",
            P(("path", "string", ".json absoluto")), new[] { "path" },
            a => _w.ApiExportLottie(Str(a, "path") ?? "")),

        ["path_boolean"] = new("Booleana entre 2 camadas: op=subtract|union|intersect|xor. Resultado substitui ambas.",
            P(("a", "string", "camada de cima"), ("b", "string", "camada de baixo"),
              ("op", "string", "subtract|union|intersect|xor")),
            new[] { "a", "b" },
            a => _w.ApiBoolean(Str(a, "a") ?? "", Str(a, "b") ?? "", Str(a, "op") ?? "subtract")),

        ["powerclip"] = new("Mete a camada 'content' DENTRO da forma da camada 'container'.",
            P(("content", "string", ""), ("container", "string", "")),
            new[] { "content", "container" },
            a => _w.ApiPowerClip(Str(a, "content") ?? "", Str(a, "container") ?? "")),

        ["remove_item"] = new("Remove uma camada por id.",
            P(("id", "string", "")), new[] { "id" }, a => _w.ApiRemove(Str(a, "id") ?? "")),

        ["clear_page"] = new("Limpa todas as camadas.",
            P(), Array.Empty<string>(), _ => _w.ApiClear()),

        ["undo"] = new("Desfaz.", P(), Array.Empty<string>(), _ => _w.ApiUndo()),
        ["redo"] = new("Refaz.", P(), Array.Empty<string>(), _ => _w.ApiRedo()),

        ["export_page"] = new("Exporta o canvas para PNG. resolution: 1080|2k|4k|<altura>. t = tempo (s).",
            P(("path", "string", "caminho .png absoluto"), ("resolution", "string", "1080|2k|4k opcional"), ("t", "number", "tempo em s")),
            new[] { "path" },
            a => _w.ApiExportImage(Str(a, "path") ?? "", Str(a, "resolution"), Num(a, "t") ?? 0)),

        ["render_frame"] = new("VÊ O TEU TRABALHO: renderiza o frame no tempo t e RECEBE-lo como imagem (visão) — olha e critica antes de continuar (o loop compor→ver→raciocinar→prosseguir).",
            P(("path", "string", "caminho .png"), ("t", "number", "tempo em s (default 0)")),
            new[] { "path" },
            a => { var p = Str(a, "path") ?? "";  _w.ApiExportImage(p, null, Num(a, "t") ?? 0);
                   return new { _image = p, t = Num(a, "t") ?? 0 }; }),

        ["set_cmyk"] = new("Pinta uma camada em CMYK (0-100 cada canal) — editor de cor de print.",
            P(("id", "string", ""), ("c", "number", "0-100"), ("m", "number", "0-100"),
              ("y", "number", "0-100"), ("k", "number", "0-100")),
            new[] { "id" },
            a => _w.ApiSetCmyk(Str(a, "id") ?? "", Num(a, "c") ?? 0, Num(a, "m") ?? 0,
                               Num(a, "y") ?? 0, Num(a, "k") ?? 0)),

        ["export_cmyk"] = new("Exporta TIFF CMYK print-ready (perfil ICC do sistema se existir).",
            P(("path", "string", "caminho .tif absoluto")), new[] { "path" },
            a => _w.ApiExportCmyk(Str(a, "path") ?? "")),

        ["set_motion"] = new("Define a timeline do comp: duração (s) e fps.",
            P(("duration", "number", "segundos"), ("fps", "number", "frames/s")),
            new[] { "duration" },
            a => _w.ApiSetMotion(Num(a, "duration") ?? 4, Num(a, "fps") ?? 30)),

        ["add_keyframe"] = new("Keyframe: prop=x|y|scale|rotation|opacity|blur|trim_start|trim_end; ease=linear|hold|in|out|inout|outback; bez=\"x1,y1,x2,y2\" cubic-bezier (avançado, sobrepõe ease).",
            P(("id", "string", ""), ("prop", "string", "x|y|scale|rotation|opacity|blur|trim_start|trim_end"),
              ("time", "number", "segundos"), ("value", "number", ""), ("ease", "string", ""),
              ("bez", "string", "cubic-bezier x1,y1,x2,y2 opcional")),
            new[] { "id", "prop", "time", "value" },
            a => _w.ApiAddKeyframe(Str(a, "id") ?? "", Str(a, "prop") ?? "x",
                                   Num(a, "time") ?? 0, Num(a, "value") ?? 0, Str(a, "ease") ?? "linear", Str(a, "bez"))),

        // ===== Fase 1: sistema de propriedades UNIFORME — endereça QUALQUER prop, incl. COR keyframável =====
        ["set_keyframe"] = new("Keyframe UNIFORME em QUALQUER propriedade, incl. COR. path=position.x|position.y|rotation|scale|scale.x|scale.y|skew.x|blur|opacity|trim.start|trim.end|color.fill|color.stroke|color.fill2 (aliases x,y,scale_x,scale_y,fill,stroke também servem). value = número OU cor \"#RRGGBB\"/\"#AARRGGBB\". Ex.: set_keyframe(id,\"color.fill\",0,\"#FF0000\") + set_keyframe(id,\"color.fill\",1,\"#0000FF\") → o fill anima vermelho→azul. A 1ª kf de cor semeia-se da cor atual (sem salto).",
            P(("id", "string", ""), ("path", "string", "propriedade canónica ou alias"),
              ("time", "number", "segundos"), ("value", "string", "número ou #hex de cor"),
              ("ease", "string", "linear|hold|in|out|inout|outback"), ("bez", "string", "cubic-bezier opcional")),
            new[] { "id", "path", "time", "value" },
            a => _w.ApiSetKeyframe(Str(a, "id") ?? "", Str(a, "path") ?? "opacity",
                                   Num(a, "time") ?? 0, Str(a, "value") ?? "0", Str(a, "ease") ?? "linear", Str(a, "bez"))),

        ["set_prop"] = new("Define o valor ESTÁTICO de qualquer propriedade (incl. cor). value = número ou #hex.",
            P(("id", "string", ""), ("path", "string", ""), ("value", "string", "número ou #hex")),
            new[] { "id", "path", "value" },
            a => _w.ApiSetProp(Str(a, "id") ?? "", Str(a, "path") ?? "", Str(a, "value") ?? "0")),

        ["get_prop"] = new("Lê o valor de qualquer propriedade no tempo t (escalar ou cor).",
            P(("id", "string", ""), ("path", "string", ""), ("t", "number", "tempo em s, default 0")),
            new[] { "id", "path" },
            a => _w.ApiGetProp(Str(a, "id") ?? "", Str(a, "path") ?? "", Num(a, "t") ?? 0)),

        ["remove_keyframe"] = new("Remove o keyframe de uma propriedade no tempo dado.",
            P(("id", "string", ""), ("path", "string", ""), ("time", "number", "segundos")),
            new[] { "id", "path", "time" },
            a => _w.ApiRemoveKeyframe(Str(a, "id") ?? "", Str(a, "path") ?? "", Num(a, "time") ?? 0)),

        ["list_props"] = new("Lista as propriedades animáveis endereçáveis de uma camada (path, kind, animated).",
            P(("id", "string", "")),
            new[] { "id" },
            a => _w.ApiListProps(Str(a, "id") ?? "")),

        ["set_camera"] = new("Câmara 3D REAL (estática): posição x,y,z, alvo tx,ty,tz, fov graus. Só os campos passados mudam.",
            P(("x", "number", ""), ("y", "number", ""), ("z", "number", "distância, default 5.2"),
              ("tx", "number", ""), ("ty", "number", ""), ("tz", "number", ""), ("fov", "number", "graus, default 34")),
            Array.Empty<string>(),
            a => _w.ApiSetCamera(Num(a, "x"), Num(a, "y"), Num(a, "z"),
                                 Num(a, "tx"), Num(a, "ty"), Num(a, "tz"), Num(a, "fov"))),

        ["camera_keyframe"] = new("Keyframe na câmara 3D: prop=x|y|z|tx|ty|tz|fov (dolly/orbit/pan animados).",
            P(("prop", "string", "x|y|z|tx|ty|tz|fov"), ("time", "number", "segundos"),
              ("value", "number", ""), ("ease", "string", ""), ("bez", "string", "cubic-bezier opcional")),
            new[] { "prop", "time", "value" },
            a => _w.ApiCameraKeyframe(Str(a, "prop") ?? "z", Num(a, "time") ?? 0, Num(a, "value") ?? 0,
                                      Str(a, "ease") ?? "linear", Str(a, "bez"))),

        ["set_3d"] = new("Torna uma camada 3D REAL (extrude+bevel, iluminada, vista pela câmara). depth<=0 remove.",
            P(("id", "string", ""), ("depth", "number", "profundidade mundo ~0.5"), ("bevel", "number", "~0.07")),
            new[] { "id", "depth" },
            a => _w.ApiSet3D(Str(a, "id") ?? "", Num(a, "depth") ?? 0.5, Num(a, "bevel") ?? 0.07)),

        ["blender_object"] = new("BLENDER → OBJETO 3D REAL na cena do KLIP (não uma imagem!). O teu script Python só MODELA (nada de render, nada de câmara/luzes); o KLIP exporta a malha e mete-a como camada 3D rodável/animável, com o PBR+IBL dele. Usa modificadores à vontade (ARRAY/SUBSURF/BEVEL/SOLIDIFY) — vêm aplicados. Usa isto quando o utilizador quiser O OBJETO; usa blender_render quando quiser só a fotografia fotorreal.",
            P(("script", "string", "Python (bpy) que constrói a geometria"), ("name", "string", "nome da camada"),
              ("timeout", "number", "segundos, default 600")),
            new[] { "script" },
            a => _w.ApiBlenderObject(Str(a, "script") ?? "", Str(a, "name"), Num(a, "timeout"))),

        ["set_material"] = new("Material PBR da camada 3D. rough 0.04(espelho)→1(mate); metal 0(plástico/vidro)→1(metal). Cromado=rough .05/metal 1 · Ouro=.32/1 · Plástico=.22/0 · Mate=.75/0.",
            P(("id", "string", ""), ("rough", "number", "0.04–1"), ("metal", "number", "0–1")),
            new[] { "id" },
            a => _w.ApiSetMaterial(Str(a, "id") ?? "", Num(a, "rough"), Num(a, "metal"))),

        ["set_face_texture"] = new("Textura na FACE do produto 3D (arte de cartão/embalagem/etiqueta): imagem na frente e/ou verso + cor da borda (núcleo de papel). Caminhos ABSOLUTOS png/jpg.",
            P(("id", "string", ""), ("front", "string", "caminho da arte da frente"),
              ("back", "string", "caminho da arte do verso"), ("edge", "string", "#RRGGBB da borda")),
            new[] { "id" },
            a => _w.ApiSetFaceTexture(Str(a, "id") ?? "", Str(a, "front"), Str(a, "back"), Str(a, "edge"))),

        ["set_stroke"] = new("Contorno/stroke numa camada (line-work): width<=0 remove. Combina com trim_start/trim_end p/ 'linha a desenhar-se'.",
            P(("id", "string", ""), ("color", "string", "#RRGGBB"), ("width", "number", "px")),
            new[] { "id", "width" },
            a => _w.ApiSetStroke(Str(a, "id") ?? "", Str(a, "color") ?? "#232326", Num(a, "width") ?? 0)),

        ["export_animation"] = new("Exporta a timeline animada para MP4 (background). resolution: 1080|2k|4k (vetorial, SEM perda).",
            P(("path", "string", "caminho .mp4 absoluto"), ("resolution", "string", "1080|2k|4k opcional")),
            new[] { "path" },
            a => _w.ApiExportAnimation(Str(a, "path") ?? "", Str(a, "resolution"))),

        ["set_grid"] = new("Grelha de construção GRID-LOGO: kind=circles|square|both|off. Gera âncoras matemáticas (interseções φ).",
            P(("kind", "string", "circles|square|both|off")), new[] { "kind" },
            a => _w.ApiSetGrid(Str(a, "kind") ?? "circles")),

        ["list_anchors"] = new("Âncoras da grelha (centros, cardeais, INTERSEÇÕES círculo-círculo) em coords centradas — constrói caminhos SÓ com estes pontos.",
            P(), Array.Empty<string>(), _ => _w.ApiListAnchors()),

        ["web_open"] = new("BROWSER: abre o browser embutido e navega até url (ou pesquisa no Google se não for URL). Vês o que o utilizador está a ver.",
            P(("url", "string", "URL ou termos de pesquisa")), new[] { "url" },
            a => _w.ApiWebOpen(Str(a, "url") ?? "")),

        ["download_image"] = new("BROWSER: baixa uma imagem por URL para as pastas do KLIP e insere-a na tela. Devolve id+path. Depois usa render_frame p/ a VERES e criticares.",
            P(("url", "string", "URL directo de uma imagem")), new[] { "url" },
            a => _w.ApiDownloadImage(Str(a, "url") ?? "")),

        ["download_youtube"] = new("BROWSER: baixa um vídeo do YouTube (yt-dlp, SABR bypass) para assets\\videos, em background. Faz list_assets depois para o encontrares.",
            P(("url", "string", "URL do vídeo YouTube")), new[] { "url" },
            a => _w.ApiDownloadYoutube(Str(a, "url") ?? "")),

        ["list_assets"] = new("BROWSER/ASSETS: lista os ficheiros nas pastas especiais do KLIP (images/downloads/videos/audio) que sobrevivem a resets.",
            P(), Array.Empty<string>(), _ => _w.ApiListAssets()),

        ["transcribe"] = new("VOZ→TEXTO on-device (a 'mesma tech' com que lês imagens, mas p/ áudio): dá o caminho de um vídeo/áudio (ex.: nome de assets\\videos) e devolve o texto. 1ª vez descarrega o modelo (~142MB) — se responder 'model_downloading', espera o meu aviso no chat e repete.",
            P(("path", "string", "caminho do áudio/vídeo (absoluto ou nome em assets)")), new[] { "path" },
            a => _w.ApiTranscribe(Str(a, "path") ?? ""), Background: true),

        ["read_text"] = new("Lê um ficheiro de texto (ex.: uma transcrição em assets\\audio\\*.txt) e devolve o conteúdo.",
            P(("path", "string", "caminho .txt (absoluto ou nome em assets)")), new[] { "path" },
            a => _w.ApiReadText(Str(a, "path") ?? "")),

        ["browser_capture"] = new("BROWSER — VÊ O QUE O UTILIZADOR VÊ: tira uma captura do que o browser mostra agora (WebView2 nativo) e recebe-la como IMAGEM (visão). Usa p/ ler/descrever a página, escolher imagens, seguir o que ele está a navegar.",
            P(), Array.Empty<string>(), _ => _w.ApiBrowserCapture()),

        ["screenshot"] = new("Tira uma captura da JANELA inteira do KLIP e recebe-la como imagem (visão) — p/ veres o estado do editor tal como o utilizador o vê.",
            P(), Array.Empty<string>(), _ => _w.ApiScreenshot()),

        // ===== Fase 8: agência ao nível do DOM (WebView2) + baixar/implementar qualquer asset =====
        ["browser_dom"] = new("BROWSER (DOM): lê a página ATUAL e devolve o mapa estruturado {title, headings, links:[{href,text}], images:[{src,alt}], videos, audios, text}. URLs já absolutos. 'Lê' a página sem precisar de visão.",
            P(), Array.Empty<string>(), _ => null, RunAsync: _ => _w.ApiBrowserDom()),

        ["browser_extract_assets"] = new("BROWSER (DOM): devolve só as listas de URLs de assets da página aberta (images/videos/audios/links), já absolutos — escolhe um e passa a download_asset.",
            P(), Array.Empty<string>(), _ => null, RunAsync: _ => _w.ApiBrowserExtractAssets()),

        ["browser_click"] = new("BROWSER (DOM): clica um elemento por seletor CSS (selector) OU pelo texto do link/botão (text). wait_nav=true espera a navegação resultante.",
            P(("selector", "string", "seletor CSS"), ("text", "string", "texto do link/botão"), ("wait_nav", "boolean", "esperar navegação")),
            Array.Empty<string>(), _ => null, RunAsync: a => _w.ApiBrowserClick(Str(a, "selector"), Str(a, "text"), Bool(a, "wait_nav") ?? false)),

        ["browser_type"] = new("BROWSER (DOM): escreve texto num campo (input/textarea) por seletor CSS e dispara input/change (compatível com React/Vue).",
            P(("selector", "string", "seletor CSS do campo"), ("text", "string", "texto a escrever")),
            new[] { "selector", "text" }, _ => null, RunAsync: a => _w.ApiBrowserType(Str(a, "selector") ?? "", Str(a, "text") ?? "")),

        ["browser_eval"] = new("BROWSER (poder total): corre JavaScript na página e devolve o resultado como JSON. O teu código deve fazer 'return <valor>'. Ex.: 'return document.querySelectorAll(\".price\").length'.",
            P(("js", "string", "expressão/statements JS que fazem return")), new[] { "js" },
            _ => null, RunAsync: a => _w.ApiBrowserEval(Str(a, "js") ?? "")),

        ["browser_wait_idle"] = new("BROWSER: espera a próxima navegação terminar (após um clique que muda de página), até timeout_ms (default 8000). Devolve {navigated}.",
            P(("timeout_ms", "number", "default 8000")), Array.Empty<string>(),
            _ => null, RunAsync: a => _w.ApiBrowserWaitIdle((int)(Num(a, "timeout_ms") ?? 8000))),

        ["download_asset"] = new("BROWSER/ASSETS: baixa QUALQUER url (imagem/áudio/vídeo/fonte/ficheiro; http(s) ou data:) p/ a pasta certa (validação por magic bytes) e, se for imagem, insere-a como camada (usa render_frame p/ a VERES). Fonte → devolve family p/ set_font/insert_text. Devolve {kind, path, from_cache}.",
            P(("url", "string", "URL de qualquer asset (ou data:)")), new[] { "url" },
            _ => null, RunAsync: a => _w.ApiDownloadAsset(Str(a, "url") ?? "")),

        // ===== Fase 9: SVG editável (nós) + rotoscoping =====
        ["list_nodes"] = new("SVG EDITÁVEL: lista os NÓS de uma camada (ponto on-curve, handles bezier in/out, tipo corner/smooth, contorno). Usa antes de edit_node.",
            P(("id", "string", ""), ("key", "number", "índice da MorphKey, default 0")), new[] { "id" },
            a => _w.ApiListNodes(Str(a, "id") ?? "", (int)(Num(a, "key") ?? 0))),

        ["edit_node"] = new("SVG EDITÁVEL: edita um nó. op=move (desloca dx,dy) | insert (subdivide o segmento que SAI de index em t 0..1, SEM deformar) | delete | set_handle (side=in|out, offset dx,dy; smooth espelha) | set_type (type=corner|smooth).",
            P(("id", "string", ""), ("op", "string", "move|insert|delete|set_handle|set_type"), ("index", "number", "de list_nodes"),
              ("dx", "number", ""), ("dy", "number", ""), ("side", "string", "in|out"), ("t", "number", "0..1 (insert)"), ("type", "string", "corner|smooth"), ("key", "number", "default 0")),
            new[] { "id", "op", "index" },
            a => _w.ApiEditNode(Str(a, "id") ?? "", Str(a, "op") ?? "move", (int)(Num(a, "index") ?? 0), Num(a, "dx") ?? 0, Num(a, "dy") ?? 0, Str(a, "side"), Num(a, "t") ?? 0.5, Str(a, "type"), (int)(Num(a, "key") ?? 0))),

        ["simplify_path"] = new("SVG EDITÁVEL: reduz nós redundantes (Ramer-Douglas-Peucker) mantendo a forma — limpa o output do trace/roto. tolerance px (maior = menos nós).",
            P(("id", "string", ""), ("tolerance", "number", "px, default 2"), ("key", "number", "default 0")), new[] { "id" },
            a => _w.ApiSimplifyPath(Str(a, "id") ?? "", Num(a, "tolerance") ?? 2, (int)(Num(a, "key") ?? 0))),

        ["import_svg"] = new("SVG EDITÁVEL: importa um .svg (caminho) OU texto SVG inline — cada <path> vira camada editável (+rect/circle/ellipse/polygon). Aplica transforms, lê fill, recentra. Depois usa list_nodes/edit_node.",
            P(("path_or_text", "string", "caminho .svg ou SVG inline"), ("x", "number", "offset do centro"), ("y", "number", "")), new[] { "path_or_text" },
            a => _w.ApiImportSvg(Str(a, "path_or_text") ?? "", Num(a, "x") ?? 0, Num(a, "y") ?? 0)),

        ["trace_bitmap"] = new("ROTO/VETOR: traça o alpha (ou luma) de uma camada de imagem/máscara para um PATH vetorial editável (contorno + RDP). A ponte raster→vetor.",
            P(("id", "string", "camada imagem/máscara"), ("threshold", "number", "0-255, default 128"), ("simplify", "number", "px RDP, default 1.5"), ("luma", "boolean", "usar luminância")), new[] { "id" },
            a => _w.ApiTraceBitmap(Str(a, "id") ?? "", Num(a, "threshold") ?? 128, Num(a, "simplify") ?? 1.5, Bool(a, "luma") ?? false)),

        ["roto"] = new("ROTOSCOPING: isola o sujeito (ONNX on-device) e traça-o num recorte vetorial editável; as_matte recorta o próprio sujeito via track-matte. Model-gated: erro claro se o modelo faltar.",
            P(("id", "string", "camada imagem"), ("threshold", "number", "default 128"), ("simplify", "number", "default 1.5"), ("as_matte", "boolean", "recortar o sujeito"), ("invert", "boolean", "")), new[] { "id" },
            a => _w.ApiRoto(Str(a, "id") ?? "", Num(a, "threshold") ?? 128, Num(a, "simplify") ?? 1.5, Bool(a, "as_matte") ?? false, Bool(a, "invert") ?? false), Background: true),

        ["set_matte"] = new("TRACK-MATTE (Fase 7): a camada source vira stencil da camada id. mode=alpha|alpha_invert|luma|luma_invert|none. Liga roto→recorte.",
            P(("id", "string", "alvo"), ("source", "string", "camada-fonte/stencil"), ("mode", "string", "alpha|alpha_invert|luma|luma_invert|none")), new[] { "id", "source", "mode" },
            a => _w.ApiSetMatte(Str(a, "id") ?? "", Str(a, "source") ?? "", Str(a, "mode") ?? "alpha")),

        // ===== Fase 10: emissor de partículas =====
        ["set_particles"] = new("PARTÍCULAS: torna a camada um EMISSOR (o Shape é o sprite). preset=confetti|sparks|smoke|stars aplica um look completo; depois afina rate/lifetime/speed/gravity/spread/direction/spin/spawn_radius/particle_scale/fade_in/fade_out/color_a/color_b/seed. rate/gravity/etc também são keyframáveis via set_keyframe.",
            P(("id", "string", ""), ("preset", "string", "confetti|sparks|smoke|stars"),
              ("rate", "number", "partículas/seg"), ("lifetime", "number", "seg"), ("speed", "number", "px/s"),
              ("gravity", "number", "px/s² (+baixo)"), ("spread", "number", "± graus (180=omni)"), ("direction", "number", "graus (-90=cima)"),
              ("spin", "number", "graus/s"), ("spawn_radius", "number", "px"), ("particle_scale", "number", ""),
              ("fade_in", "number", "0..1"), ("fade_out", "number", "0..1"), ("color_a", "string", "#hex"), ("color_b", "string", "#hex"), ("seed", "number", "")),
            new[] { "id" },
            a => _w.ApiSetParticles(Str(a, "id") ?? "", Str(a, "preset"), Num(a, "rate"), Num(a, "lifetime"), Num(a, "speed"),
                Num(a, "gravity"), Num(a, "spread"), Num(a, "direction"), Num(a, "spin"), Num(a, "spawn_radius"), Num(a, "particle_scale"),
                Num(a, "fade_in"), Num(a, "fade_out"), Str(a, "color_a"), Str(a, "color_b"), Num(a, "seed") is double sd ? (int)sd : (int?)null)),

        ["clear_particles"] = new("PARTÍCULAS: remove o emissor da camada (volta a ser uma forma normal).",
            P(("id", "string", "")), new[] { "id" }, a => _w.ApiClearParticles(Str(a, "id") ?? "")),

        // ===== Ponte Blender: 3D fotorreal (Cycles) que o motor Skia do KLIP não faz =====
        ["blender_render"] = new("BLENDER (3D fotorreal): corre um script Python no Blender headless e devolve o PNG — usa quando o pedido exige path-tracing real (produto, vidro, metal, GI) em vez do 3D do KLIP. O script recebe o caminho de saída em sys.argv depois de '--' (usa-o em bpy.context.scene.render.filepath). Blender 5.x: NÃO uses action.fcurves, scene.node_tree, mesh.use_auto_smooth nem BLENDER_EEVEE_NEXT; sockets do Principled são 'Specular IOR Level'/'Emission Color'/'Coat Weight'. Sem GPU aqui: Cycles em CPU, 64-128 amostras + denoise, e põe view_transform='Standard' senão sai lavado. Recebes a imagem de volta para a criticares.",
            P(("script", "string", "código Python (bpy) completo"), ("path", "string", "caminho .png absoluto de saída"),
              ("timeout", "number", "segundos, default 900")),
            new[] { "script", "path" },
            a => _w.ApiBlenderRender(Str(a, "script") ?? "", Str(a, "path") ?? "", Num(a, "timeout")), Background: true),
    };

    public object Manifest() => Acts.Select(kv => new
    {
        name = kv.Key,
        description = kv.Value.Description,
        @params = kv.Value.Params,
        required = kv.Value.Required,
    }).ToArray();

    public async Task<object?> Execute(string action, JsonElement args)
    {
        if (!Acts.TryGetValue(action, out var act))
            throw new InvalidOperationException($"ação desconhecida: {action}");
        OpLog.Op("action", action);   // auto-registo de operações
        try
        {
            // Fase 8: verbo async (Browser.InvokeScript é Task) — corre e awaita na UI thread (sem deadlock).
            if (act.RunAsync is not null)
                return await Dispatcher.UIThread.InvokeAsync(() => act.RunAsync(args));
            // Background=true: corre num thread do pool (transcrição pesada) sem congelar a UI.
            return act.Background
                ? await Task.Run(() => act.Run(args))
                : await Dispatcher.UIThread.InvokeAsync(() => act.Run(args));
        }
        catch (Exception ex) { OpLog.Error(action + ": " + ex.Message); throw; }
    }

    private static string? Str(JsonElement e, string n)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static double? Num(JsonElement e, string n)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
    private static bool? Bool(JsonElement e, string n)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v)
           && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) ? v.GetBoolean() : null;
}
