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
        Func<JsonElement, object?> Run, bool Background = false);

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

        ["insert_text"] = new("Insere texto como contornos vetoriais editáveis.",
            P(("text", "string", "o texto"), ("size", "number", "tamanho da fonte (default 120)"),
              ("fill", "string", "#RRGGBB"), ("x", "number", ""), ("y", "number", "")),
            new[] { "text" },
            a => _w.ApiInsertText(Str(a, "text") ?? "", Num(a, "size") ?? 120,
                                  Str(a, "fill"), Num(a, "x") ?? 0, Num(a, "y") ?? 0)),

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
