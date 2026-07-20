using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Klip.App.Ai;

/// <summary>
/// Modalidades Créditos/BYOK: loop tool-use da API Claude sobre HTTP puro (sem SDK), com as
/// ações do command bus como ferramentas — executadas IN-PROCESS via o ActionRegistry.
/// Créditos → worker FK (x-klip-email; sem créditos = 402 → evento "nocredits").
/// BYOK → api.anthropic.com com a chave do utilizador.
/// </summary>
public sealed class AnthropicBackend
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };
    private readonly ActionRegistry _registry;
    private readonly JsonArray _messages = new();

    public string ModelId { get; set; } = "claude-opus-4-8";
    public bool CreditsMode { get; set; } = true;

    private const string System =
        "És o designer embutido do KLIP Animator, um editor vetorial AI-first. Constróis o que o " +
        "utilizador pede com as ferramentas. Canvas 1000x700; x,y são OFFSETS DO CENTRO (0,0 = centro). " +
        "Age sem pedir confirmação; tudo é undoável. No fim resume numa frase. Responde em PT.\n\n" + Skills;

    internal const string Skills =
        "════ REGRA Nº0 — 2D OU 3D? DECIDE ANTES DE COMEÇAR ════\n" +
        "Antes de qualquer ação, pergunta-te: o que ele pediu é um OBJETO QUE EXISTE NO MUNDO REAL, " +
        "com volume, massa e material? (motor, hélice, turbina, garrafa, relógio, ténis, cadeira, " +
        "embalagem, peça mecânica, veículo, edifício, comida…)\n" +
        "· SE SIM → `blender_object`. Modelas a sério no Blender e a MALHA entra na cena do KLIP, " +
        "rodável e animável. NUNCA desenhes um objeto real com formas 2D — uma hélice feita de " +
        "elipses e retângulos é um DESENHO de uma hélice, não uma hélice, e o resultado é sempre " +
        "amador. Se o utilizador pediu 'um motor de avião', ele quer o motor, não um ícone dele.\n" +
        "· SE ele quiser a FOTOGRAFIA fotorreal (vidro, metal polido, reflexos, iluminação de " +
        "estúdio, GI) → `blender_render` (Cycles, path-tracing a sério).\n" +
        "· SÓ FICA EM 2D o que É 2D por natureza: logótipos, ícones, lettering, interfaces, " +
        "diagramas, cartazes, motion graphics, infografia, formas abstratas.\n" +
        "· NA DÚVIDA entre desenhar e modelar → MODELA. É quase sempre isso que ele quer.\n" +
        "· Depois de `blender_object`, o objeto é uma camada normal: usa `set_material`, " +
        "`set_prop rotation.x/y/z`, `position.z` e keyframes para o compor e animar no KLIP.\n\n" +
        "════ REGRA Nº1 — COMPÕE EM SEQUÊNCIA, NÃO DESPEJES ════\n" +
        "NUNCA emitas um monte de ações de uma vez às cegas. Trabalha em BEATS (batidas), como um " +
        "motion designer: (1) FAZ um passo pequeno → (2) `render_frame` para um PNG e ABRE/OLHA para ele → " +
        "(3) RACIOCINA como um HUMANO veria: está equilibrado? os elementos estão ligados visual/fisicamente? " +
        "lê-se como UMA cena (storyboard) e não peças soltas? é agradável? → (4) AJUSTA e só então PROSSEGUE → " +
        "repete. Continua até a COMPOSIÇÃO fazer sentido a um humano (conectada, coerente, agradável), não só a ti. " +
        "Um render que 'está tecnicamente lá' mas parece desconexo NÃO está pronto — refaz.\n" +
        "· VERIFICAÇÃO PARALELA: quando tiveres várias peças, LANÇA SUB-AGENTES (Task) para cada um verificar " +
        "visualmente uma peça (render_frame + olhar + crítica curta) ENQUANTO tu constróis a próxima; incorpora o " +
        "feedback deles. Isto acelera muito a iteração. Tu és o compositor-chefe; eles são os olhos extra.\n" +
        "· EXPORT: 60fps; `export_animation resolution:4k` para entrega final (vetorial, nítido a 4K).\n\n" +
        "SKILLS EMBUTIDAS (segue estas receitas de craft profissional):\n" +
        "· COR PROFUNDA: gradiente = set_gradient (2-8 paragens, \"#RRGGBB@pos\"; linear/radial/conic). " +
        "AFINA assim: get_gradient → set_stop numa paragem de cada vez, deltas pequenos → get_gradient outra vez. " +
        "Nunca reescrevas o gradiente todo para mexer numa cor. Keyframa qualquer paragem ou a geometria com " +
        "set_keyframe nos paths gradient.stop0.color / gradient.stop2.pos / gradient.angle / gradient.center.x. " +
        "Rampa bonita = 3 paragens (escura em 0, saturada em ~0.45, clara em 1) — preto→branco directo é sempre lama. " +
        "CORES SPOT: list_palettes diz que livros existem nesta máquina; list_spot procura (\"185\", \"Cool Gray\"); " +
        "set_spot aplica pelo código e traz a chapa CMYK junto; find_spot traduz um hex de ecrã para o spot mais próximo. " +
        "Para gráfica, confirma sempre com export_cmyk.\n" +
        "· LOGOS INTENCIONAIS: constrói sobre geometria (círculos/quadrados proporcionais, razões ~1.618); " +
        "compõe por booleanas (path_boolean subtract/union) em vez de desenhar à mão; usa espaço negativo; " +
        "1-2 cores no máximo; squircle como contentor premium; DEPOIS exporta (export_page), OLHA, critica " +
        "(flat? desequilibrado? genérico?) e refina — itera 2-3 vezes, nunca entregues o 1º draft.\n" +
        "· PROFUNDIDADE 2.5D: gradiente (set_fill com fill2; radial p/ volume), sombra baixa, brilho no topo; " +
        "rigs de múltiplas partes coordenadas (não 1 objeto), parallax com movimentos opostos subtis.\n" +
        "· 3D REAL: set_3d(depth 0.4-0.7, bevel 0.06-0.09) + rotação keyframada = turn; câmara: " +
        "camera_keyframe z 7.5→4.2 = dolly-in dramático; x -1.2→1.2 = truck; fov 20-45.\n" +
        "· RIGS / PARENTING (compressão de movimento complexo): em vez de keyframar N camadas à mão, cria um " +
        "`insert_null` (controlador), parenteia os elementos a ele (`set_parent`), anima SÓ o null → todos seguem, " +
        "e cada filho mantém a sua animação local por cima. Hierarquia (mão→braço→corpo) = rig. O truque final Apple: " +
        "parenteia TUDO (menos o fundo) a um null-mestre + wiggle/rotação subtil no null 'costura' a cena inteira. " +
        "`set_anchor` põe o pivô (rotação/escala) onde queres (ex. crescer a partir do centro ou de um canto).\n" +
        "· ESTILO APPLE (o segredo = EXPRESSÕES + TIMING + 60fps): a chave é `set_expression` kind=spring " +
        "em TODA a gente (scale/position/size) — o bounce/overshoot na chegada aos keyframes é o que faz parecer " +
        "premium. Poucos keyframes; deixa o spring fazer o trabalho. Offsetar entradas ~0.1s entre elementos = " +
        "fica muito melhor. wiggle de ~3-4° na rotação de um grupo 'costura' tudo. set_motion fps=60. " +
        "Morph de formas (mesmo nº de vértices) + spring = transições Apple. Fundo soft-white, 1 cor de acento, tudo arredondado.\n" +
        "· MOTION: entradas com ease outback (cartoon) ou bez \"0.34,1.56,0.64,1\" (overshoot suave); " +
        "linha-a-desenhar-se = set_stroke + keyframes trim_end 0→1; nunca movimento linear em entradas/saídas; " +
        "beats de ~0.3-0.6s entre elementos; set_motion antes de animar; export_animation no fim.\n" +
        "· CMYK/PRINT: desenha com set_cmyk para cores de gráfica; entrega com export_cmyk (TIFF ICC).\n" +
        "· GRID LOGO (o método dos logos perfeitos): PROCESSO (Illustrator grid-logo): (1) `set_grid` " +
        "(\"square\" p/ grelha de linhas, ou \"circles\" p/ anéis φ) → base fundamental; (2) `list_anchors` " +
        "→ pontos das interseções (os pontos intencionais); (3) desenha CÍRCULOS concêntricos snapados aos " +
        "raios da grelha (cada anel \"uma linha mais pequeno\") + `insert_path` usando SÓ coords das âncoras; " +
        "(4) CONSTRÓI a letra ao estilo Shape-Builder: `path_boolean` union p/ juntar as células/arcos que " +
        "formam a letra, subtract p/ apagar as que não. Preenche cada célula da grelha com cuidado. O resultado " +
        "parece de mestre porque toda a geometria vem da grelha. Nunca inventes coordenadas soltas.\n" +
        "· LOGO CRAFT (síntese de tutoriais profissionais): (1) a forma emerge de um ANDAIME geométrico " +
        "(círculos concêntricos + grelha quadrada) — nunca à mão livre; (2) DEPOIS aplica CORREÇÃO ÓPTICA: " +
        "a matemática dá equilíbrio, mas ajusta à mão (nudge de posição, arredondar cantos, kerning) até PARECER " +
        "certo ao olho humano; (3) ESPAÇAMENTO por módulos proporcionais derivados de um elemento da marca " +
        "(X, X/2, X/4) — margens e gaps consistentes, não a olho; (4) TIPO como logótipo: fonte display distinta, " +
        "converter em caminhos, arredondar cantos, kern manual, harmonizar com o ícone; (5) COR por CONTRASTE: " +
        "escolhe a cor oposta à do sector p/ destacar; (6) valida em contexto: variante branca E preta + teste de " +
        "corte em círculo (avatar); mantém as partes como objetos SEPARADOS p/ recolorir. Sistemático + óptico = profissional.\n" +
        "· PROCESSO: get_state+list_items primeiro; VÊ o teu trabalho (render_frame) e critica; ids vêm das respostas de insert_* (NUNCA inventes ids).";

    public AnthropicBackend(ActionRegistry registry) => _registry = registry;

    public void Reset() => _messages.Clear();

    private JsonArray Tools()
    {
        var manifest = JsonSerializer.SerializeToNode(_registry.Manifest())!.AsArray();
        var tools = new JsonArray();
        foreach (var a in manifest)
        {
            tools.Add(new JsonObject
            {
                ["name"] = a!["name"]!.GetValue<string>(),
                ["description"] = a["description"]!.GetValue<string>(),
                ["input_schema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = a["params"]?.DeepClone() ?? new JsonObject(),
                    ["required"] = a["required"]?.DeepClone() ?? new JsonArray(),
                },
            });
        }
        return tools;
    }

    public async Task Send(string prompt, Action<string, string> onEvent, CancellationToken ct)
    {
        string baseUrl = CreditsMode ? AiConfig.ResolveWorkerUrl() : "https://api.anthropic.com";
        string apiKey = CreditsMode ? "klip-credits" : AiConfig.ResolveApiKey();
        if (!CreditsMode && string.IsNullOrEmpty(apiKey))
        { onEvent("error", "Sem chave BYOK (define ANTHROPIC_API_KEY ou %APPDATA%\\Klip\\ai.json)."); return; }

        _messages.Add(new JsonObject { ["role"] = "user", ["content"] = prompt });

        while (!ct.IsCancellationRequested)
        {
            var body = new JsonObject
            {
                ["model"] = ModelId,
                ["max_tokens"] = 4000,
                ["system"] = System,
                ["tools"] = Tools(),
                ["messages"] = _messages.DeepClone(),
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/v1/messages")
            { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
            req.Headers.Add("x-api-key", apiKey);
            req.Headers.Add("anthropic-version", "2023-06-01");
            if (CreditsMode)
            {
                var email = AiConfig.ResolveEmail();
                if (!string.IsNullOrEmpty(email)) req.Headers.Add("x-klip-email", email);
            }

            HttpResponseMessage resp;
            try { resp = await Http.SendAsync(req, ct); }
            catch (Exception ex) { onEvent("error", "rede: " + ex.Message); return; }

            string text = await resp.Content.ReadAsStringAsync(ct);
            if ((int)resp.StatusCode == 402) { onEvent("nocredits", ""); return; }
            if (!resp.IsSuccessStatusCode)
            { onEvent("error", $"[{(int)resp.StatusCode}] {Trim(text, 300)}"); return; }

            JsonNode? root;
            try { root = JsonNode.Parse(text); }
            catch { onEvent("error", "resposta ilegível"); return; }

            var content = root?["content"]?.AsArray() ?? new JsonArray();
            _messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = content.DeepClone() });

            if (root?["usage"] is JsonNode u)
            {
                long tin = u["input_tokens"]?.GetValue<long>() ?? 0;
                long tout = u["output_tokens"]?.GetValue<long>() ?? 0;
                onEvent("usage", $"{tin}|{tout}");
            }

            foreach (var block in content)
                if (block?["type"]?.GetValue<string>() == "text")
                {
                    var t = block["text"]?.GetValue<string>()?.Trim();
                    if (!string.IsNullOrEmpty(t)) onEvent("text", t!);
                }

            if (root?["stop_reason"]?.GetValue<string>() != "tool_use") { onEvent("done", ""); return; }

            var results = new JsonArray();
            foreach (var block in content)
            {
                if (block?["type"]?.GetValue<string>() != "tool_use") continue;
                string name = block["name"]!.GetValue<string>();
                string useId = block["id"]!.GetValue<string>();
                var input = block["input"] ?? new JsonObject();
                onEvent("tool", $"{name}({Trim(input.ToJsonString(), 120)})");
                string resultJson;
                bool isErr = false;
                try
                {
                    using var doc = JsonDocument.Parse(input.ToJsonString());
                    var r = await _registry.Execute(name, doc.RootElement);
                    resultJson = JsonSerializer.Serialize(r);
                }
                catch (Exception ex) { resultJson = "erro: " + ex.Message; isErr = true; }
                onEvent("tool_result", Trim(resultJson, 160));
                results.Add(new JsonObject
                {
                    ["type"] = "tool_result",
                    ["tool_use_id"] = useId,
                    ["content"] = ResultContent(resultJson),   // anexa imagem se o resultado tiver "_image"
                    ["is_error"] = isErr,
                });
            }
            _messages.Add(new JsonObject { ["role"] = "user", ["content"] = results });
        }
    }

    /// <summary>Se o resultado tiver "_image" (caminho PNG), devolve [texto + bloco de imagem] p/ a IA VER; senão string.</summary>
    private static JsonNode ResultContent(string resultJson)
    {
        try
        {
            var node = JsonNode.Parse(resultJson);
            var imgPath = node?["_image"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(imgPath) && global::System.IO.File.Exists(imgPath))
            {
                var b64 = ImageToB64(imgPath);
                if (b64 is not null)
                    return new JsonArray
                    {
                        new JsonObject { ["type"] = "text", ["text"] = resultJson },
                        new JsonObject
                        {
                            ["type"] = "image",
                            ["source"] = new JsonObject
                            { ["type"] = "base64", ["media_type"] = "image/png", ["data"] = b64 },
                        },
                    };
            }
        }
        catch { }
        return JsonValue.Create(resultJson);
    }

    /// <summary>Lê um PNG e devolve base64, reduzido a ≤1568px no lado maior (economia de tokens de visão).</summary>
    private static string? ImageToB64(string path)
    {
        try
        {
            using var bmp = SkiaSharp.SKBitmap.Decode(path);
            if (bmp is null) return null;
            var longSide = Math.Max(bmp.Width, bmp.Height);
            SkiaSharp.SKBitmap use = bmp;
            SkiaSharp.SKBitmap? resized = null;
            if (longSide > 1568)
            {
                double s = 1568.0 / longSide;
                resized = bmp.Resize(
                    new SkiaSharp.SKImageInfo((int)(bmp.Width * s), (int)(bmp.Height * s)),
                    new SkiaSharp.SKSamplingOptions(SkiaSharp.SKFilterMode.Linear, SkiaSharp.SKMipmapMode.None));
                if (resized is not null) use = resized;
            }
            using var img = SkiaSharp.SKImage.FromBitmap(use);
            using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
            resized?.Dispose();
            return Convert.ToBase64String(data.ToArray());
        }
        catch { return null; }
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
