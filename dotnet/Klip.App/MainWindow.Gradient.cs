using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Klip.Model;

namespace Klip.App;

/// <summary>
/// Bus do gradiente multi-stop: é por aqui que a IA controla a cor AO MILÍMETRO.
///
/// Regra que atravessa este ficheiro inteiro: NADA de fallback silencioso. O <see cref="ParseColor"/>
/// do MainWindow devolve o valor antigo quando o hex é inválido — bom para a UI (não pisca), péssimo
/// para a IA (pede #0A1B2 e leva de volta ok=true com a cor de antes, e passa a corrigir uma coisa que
/// nunca mudou). Aqui um hex torto é uma excepção com a mensagem a dizer o que se esperava.
/// </summary>
public partial class MainWindow : Window
{
    // ---------------- parsing (estrito) ----------------

    /// <summary>
    /// Hex → ARGB, SEM fallback. Aceita #RGB, #RRGGBB e #AARRGGBB. #RGBA/#ARGB de 4 dígitos ficam de
    /// fora de propósito: são ambíguos entre convenções (CSS põe o alpha no fim, o Skia no início) e
    /// adivinhar errado dá cores com alpha trocado que ninguém percebe de onde vieram.
    /// </summary>
    private static uint ParseStopColor(string? hex, string where)
    {
        var h = (hex ?? "").Trim().TrimStart('#');
        if (h.Length == 3 && uint.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var s))
            // #RGB expande duplicando cada nibble (F0A → FF00AA), como no CSS.
            return 0xFF000000u
                 | (((s >> 8) & 0xF) * 0x11u) << 16
                 | (((s >> 4) & 0xF) * 0x11u) << 8
                 | ((s & 0xF) * 0x11u);
        if (h.Length == 6 && uint.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            return 0xFF000000u | rgb;
        if (h.Length == 8 && uint.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var argb))
            return argb;
        throw new InvalidOperationException(
            $"cor inválida em {where}: '{hex}'. Esperava #RGB, #RRGGBB ou #AARRGGBB (ex.: #1E90FF, #CC1E90FF).");
    }

    /// <summary>ARGB → hex. Só escreve o alpha quando ele existe mesmo, senão a IA lê "#FF..." e julga que é vermelho.</summary>
    private static string GradHex(uint argb)
        => (argb >> 24) == 0xFF ? "#" + (argb & 0xFFFFFFu).ToString("X6") : "#" + argb.ToString("X8");

    /// <summary>
    /// "#RRGGBB@0.35, #112233, #ABCDEF@1" → paragens. As posições omitidas distribuem-se por i/(n-1)
    /// sobre o índice na string, NÃO sobre "os que faltam": é assim que "#A, #B@0.9, #C" dá 0 / 0.9 / 1
    /// e a paragem escrita à mão fica exactamente onde o utilizador a pôs.
    /// </summary>
    private static List<GradStop> ParseStops(string stops, out List<string> avisos)
    {
        avisos = new List<string>();
        var raw = (stops ?? "").Split(',')
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)   // vírgula a mais no fim não é ambiguidade de cor — não vale rebentar por isso
            .ToList();

        if (raw.Count < 2)
            throw new InvalidOperationException(
                $"um gradiente precisa de pelo menos 2 paragens; recebi {raw.Count}. " +
                "Formato: \"#RRGGBB@0, #RRGGBB@0.5, #RRGGBB@1\" (o @pos é opcional).");
        if (raw.Count > GradientSpec.MaxStops)
            throw new InvalidOperationException(
                $"máximo de {GradientSpec.MaxStops} paragens; recebi {raw.Count}. " +
                "Corta as que não mudam nada — acima de 8 o shader não as consegue mostrar.");

        var outp = new List<GradStop>(raw.Count);
        for (int i = 0; i < raw.Count; i++)
        {
            var parts = raw[i].Split('@');
            if (parts.Length > 2)
                throw new InvalidOperationException(
                    $"paragem {i} ('{raw[i]}') tem mais do que um '@'. Formato: #RRGGBB@0.35");

            uint argb = ParseStopColor(parts[0], $"paragem {i}");

            double pos;
            if (parts.Length == 1)
            {
                pos = raw.Count == 1 ? 0.0 : i / (double)(raw.Count - 1);
            }
            else
            {
                var pt = parts[1].Trim();
                // IsFinite e não só TryParse: "NaN" é aceite pelo TryParse, o Clamp devolve NaN e a
                // guarda de aviso lá em baixo é falsa para NaN — passava calado para dentro do
                // documento e depois rebentava a gravação do .klip, que nem AllowNamedFloatingPointLiterals tem.
                if (!double.TryParse(pt, NumberStyles.Float, CultureInfo.InvariantCulture, out pos) || !double.IsFinite(pos))
                    throw new InvalidOperationException(
                        $"posição inválida na paragem {i}: '{parts[1]}'. Esperava um número 0-1 com ponto decimal (ex.: 0.35).");
                double clamped = Math.Clamp(pos, 0.0, 1.0);
                if (Math.Abs(clamped - pos) > 1e-12)
                {
                    avisos.Add($"paragem {i}: pos {pos.ToString(CultureInfo.InvariantCulture)} fora de 0-1, ficou {clamped.ToString(CultureInfo.InvariantCulture)}");
                    pos = clamped;
                }
            }
            outp.Add(new GradStop(argb, pos));
        }
        return outp;
    }

    private static GradKind ParseGradKind(string? kind, GradKind fallback) => (kind ?? "").Trim().ToLowerInvariant() switch
    {
        "" => fallback,
        "linear" or "line" or "lin" => GradKind.Linear,
        "radial" or "radius" or "circular" or "circle" => GradKind.Radial,
        "conic" or "conical" or "cone" or "angular" or "sweep" => GradKind.Conic,
        _ => throw new InvalidOperationException($"kind desconhecido: '{kind}'. Só existe linear, radial ou conic."),
    };

    private static int ParseGradTile(string? tile, int fallback) => (tile ?? "").Trim().ToLowerInvariant() switch
    {
        "" => fallback,
        "clamp" or "pad" or "none" => 0,
        "repeat" or "tile" or "wrap" => 1,
        "mirror" or "reflect" => 2,
        _ => throw new InvalidOperationException($"tile desconhecido: '{tile}'. Só existe clamp, repeat ou mirror."),
    };

    private static string GradKindName(GradKind k) => k.ToString().ToLowerInvariant();
    private static string GradTileName(int t) => t switch { 1 => "repeat", 2 => "mirror", _ => "clamp" };

    /// <summary>
    /// O gradiente da camada, semeado LAZY a partir do par legado quando ainda não existe — mesma
    /// jogada do PropRegistry.Grad (que é privado lá, por isso repete-se aqui). Sem isto, um set_stop
    /// numa camada de cor chapada explodia em vez de simplesmente começar um gradiente.
    /// </summary>
    private static GradientSpec GradOf(Layer l)
        => l.FillGradient ?? GradientSpec.Seed(l.FillArgb, l.FillArgb2, l.FillRadial, l.GradAngle);

    /// <summary>
    /// Grava o gradiente e põe a camada COERENTE consigo própria:
    ///  · FillArgb2 = null → o caminho legado (FillArgb→FillArgb2) deixa de competir com este;
    ///  · FillArgb = cor da 1ª paragem → a barra de contexto e o inspector param de mostrar a cor de antes;
    ///  · FillColor (cor animável) manda sobre FillArgb no Toolbar/Renderer, por isso colapsa-se também —
    ///    MAS só quando é estática. Se tiver keyframes a sério, fica intacta e sai um aviso: apagar
    ///    a animação de cor de alguém para arrumar um swatch seria roubo silencioso.
    /// </summary>
    private Layer ApplyGradient(Layer l, GradientSpec spec, List<string> avisos)
    {
        var norm = spec.Normalized();
        uint first = norm.Stops[0].EvalArgb(0);

        bool fillColorAnimada = (l.FillColor?.Keys.Count ?? 0) > 1;
        if (fillColorAnimada)
            avisos.Add("color.fill tem keyframes e continua a mandar na cor plana da camada; " +
                       "o gradiente desenha na mesma, mas o swatch da barra segue a animação.");

        return l with
        {
            FillGradient = norm,
            FillArgb = first,
            FillArgb2 = null,
            FillColor = fillColorAnimada ? l.FillColor : null,
        };
    }

    private static object[] StopsOut(GradientSpec g, double t) => g.Stops.Select((s, i) => (object)new
    {
        index = i,
        color = GradHex(s.EvalArgb(t)),
        pos = s.EvalPos(t),
        animated = s.Color is not null || s.Offset is not null,
    }).ToArray();

    // ---------------- verbos ----------------

    /// <summary>Substitui o gradiente inteiro. stops = "#RRGGBB@0, #RRGGBB@0.35, #RRGGBB@1".</summary>
    public object ApiSetGradient(string id, string stops, string? kind,
                                 double? angle, double? cx, double? cy, double? radius, string? tile)
    {
        int ix = FindLayer(id);
        var l = Sel(id);
        var prev = GradOf(l);   // guarda a geometria de antes: quem só manda 'stops' não quer perder o ângulo

        var parsed = ParseStops(stops, out var avisos);

        // Clampar cx/cy/radius aqui (e não só no Eval) para que o valor devolvido seja o valor REAL gravado.
        Track? Keep(double? v, Track? old, double lo, double hi, string nome)
        {
            if (v is not { } d) return old;
            double c = Math.Clamp(d, lo, hi);
            if (Math.Abs(c - d) > 1e-12)
                avisos.Add($"{nome} {d.ToString(CultureInfo.InvariantCulture)} fora de {lo}-{hi}, ficou {c.ToString(CultureInfo.InvariantCulture)}");
            return Track.Const(c);
        }

        var spec = new GradientSpec(
            parsed,
            ParseGradKind(kind, prev.Kind),
            angle is { } a ? Track.Const(a) : prev.Angle,   // graus: sem clamp, -45 e 315 são ambos legítimos
            Keep(cx, prev.CenterX, 0, 1, "cx"),
            Keep(cy, prev.CenterY, 0, 1, "cy"),
            Keep(radius, prev.Radius, 1e-3, 1, "radius"),
            ParseGradTile(tile, prev.Tile));

        var novo = ApplyGradient(l, spec, avisos);
        Mutate(() => _layers[ix] = novo);

        var g = novo.FillGradient!;
        return new
        {
            ok = true,
            id = novo.Key,
            kind = GradKindName(g.Kind),
            angle = g.EvalAngle(0),
            cx = g.EvalCenterX(0),
            cy = g.EvalCenterY(0),
            radius = g.EvalRadius(0),
            tile = GradTileName(g.Tile),
            stops = StopsOut(g, 0),
            warnings = avisos.ToArray(),
        };
    }

    /// <summary>Lê o gradiente completo em t — para a IA trabalhar por deltas em vez de reescrever tudo.</summary>
    public object ApiGetGradient(string id, double t)
    {
        var l = Sel(id);
        if (l.FillGradient is not { } g)
        {
            // Devolver "vazio" e calar-se era o caminho rápido para a IA repetir o mesmo pedido em loop.
            // Diz-se PORQUE não há nada, e o que a camada tem em vez disso.
            string porque = l.FillArgb2 is not null
                ? "esta camada ainda usa o gradiente LEGADO de 2 cores (fill→fill2), que não tem paragens endereçáveis"
                : "esta camada tem cor chapada, nunca lhe foi aplicado um gradiente";
            return new
            {
                ok = true,
                id = l.Key,
                has_gradient = false,
                hint = porque + ". Chama set_gradient para criar um multi-stop — ele nasce a partir destas cores, não do nada.",
                legacy = new
                {
                    fill = GradHex(l.FillColor?.Eval(t) ?? l.FillArgb),
                    fill2 = l.FillArgb2 is { } f2 ? GradHex(f2) : null,
                    radial = l.FillRadial,
                    angle = l.GradAngle,
                },
            };
        }

        return new
        {
            ok = true,
            id = l.Key,
            has_gradient = true,
            t,
            kind = GradKindName(g.Kind),
            angle = g.EvalAngle(t),
            cx = g.EvalCenterX(t),
            cy = g.EvalCenterY(t),
            radius = g.EvalRadius(t),
            tile = GradTileName(g.Tile),
            count = g.Stops.Count,
            stops = StopsOut(g, t),
        };
    }

    /// <summary>Mexe numa só paragem, sem tocar nas outras nem na geometria.</summary>
    public object ApiSetStop(string id, int index, string? color, double? pos)
    {
        if (color is null && pos is null)
            throw new InvalidOperationException("set_stop sem color nem pos não muda nada — diz pelo menos um dos dois.");

        int ix = FindLayer(id);
        var l = Sel(id);
        var g = GradOf(l);

        if (index < 0 || index >= g.Stops.Count)
            throw new InvalidOperationException(
                $"paragem {index} não existe: este gradiente tem {g.Stops.Count} (índices 0 a {g.Stops.Count - 1}). " +
                "Usa add_stop para criar uma nova.");

        var avisos = new List<string>();
        var s = g.Stops[index];

        uint argb = s.Argb;
        if (color is not null)
        {
            argb = ParseStopColor(color, $"paragem {index}");
            if ((s.Color?.Keys.Count ?? 0) > 1)
                avisos.Add($"paragem {index}: os keyframes de cor foram colapsados neste valor fixo (set_stop é estático).");
        }

        double p = s.Pos;
        if (pos is { } pv)
        {
            p = Math.Clamp(pv, 0.0, 1.0);
            if (Math.Abs(p - pv) > 1e-12)
                avisos.Add($"pos {pv.ToString(CultureInfo.InvariantCulture)} fora de 0-1, ficou {p.ToString(CultureInfo.InvariantCulture)}");
            if ((s.Offset?.Keys.Count ?? 0) > 1)
                avisos.Add($"paragem {index}: os keyframes de posição foram colapsados neste valor fixo.");
        }

        // Color/Offset a null é obrigatório, não é limpeza: enquanto o track animável existir é ELE que
        // manda (GradStop.EvalArgb/EvalPos), e o novo Argb/Pos ficava lá gravado sem nunca aparecer.
        var stopsList = g.Stops.ToList();
        stopsList[index] = s with
        {
            Argb = argb,
            Pos = p,
            Color = color is not null ? null : s.Color,
            Offset = pos is not null ? null : s.Offset,
        };

        var novo = ApplyGradient(l, g with { Stops = stopsList }, avisos);
        Mutate(() => _layers[ix] = novo);

        var ng = novo.FillGradient!;
        // Normalized() reordena por posição — mexer numa paragem pode fazê-la trocar de índice, e a IA
        // tem de saber por onde continuar. Daí devolver-se a lista inteira e não só a paragem tocada.
        return new
        {
            ok = true,
            id = novo.Key,
            index,
            color = GradHex(argb),
            pos = p,
            stops = StopsOut(ng, 0),
            warnings = avisos.ToArray(),
        };
    }

    /// <summary>Insere uma paragem nova (máx 8).</summary>
    public object ApiAddStop(string id, string color, double pos)
    {
        int ix = FindLayer(id);
        var l = Sel(id);
        var g = GradOf(l);

        if (g.Stops.Count >= GradientSpec.MaxStops)
            throw new InvalidOperationException(
                $"já há {g.Stops.Count} paragens e o máximo é {GradientSpec.MaxStops}. " +
                "Remove uma com remove_stop, ou muda uma existente com set_stop.");

        var avisos = new List<string>();
        uint argb = ParseStopColor(color, "add_stop");
        double p = Math.Clamp(pos, 0.0, 1.0);
        if (Math.Abs(p - pos) > 1e-12)
            avisos.Add($"pos {pos.ToString(CultureInfo.InvariantCulture)} fora de 0-1, ficou {p.ToString(CultureInfo.InvariantCulture)}");

        var nova = new GradStop(argb, p);
        var stopsList = g.Stops.ToList();
        stopsList.Add(nova);

        var novo = ApplyGradient(l, g with { Stops = stopsList }, avisos);
        Mutate(() => _layers[ix] = novo);

        var ng = novo.FillGradient!;
        // Índice REAL depois da ordenação — devolver o do fim da lista seria mentira em qualquer pos < 1.
        int idx = ng.Stops.ToList().FindIndex(x => ReferenceEquals(x, nova));
        return new
        {
            ok = true,
            id = novo.Key,
            index = idx,
            color = GradHex(argb),
            pos = p,
            count = ng.Stops.Count,
            stops = StopsOut(ng, 0),
            warnings = avisos.ToArray(),
        };
    }

    /// <summary>Remove uma paragem (mínimo 2).</summary>
    public object ApiRemoveStop(string id, int index)
    {
        int ix = FindLayer(id);
        var l = Sel(id);

        if (l.FillGradient is not { } g)
            throw new InvalidOperationException(
                "esta camada não tem gradiente multi-stop, portanto não há paragem nenhuma para remover. Lê com get_gradient.");

        if (index < 0 || index >= g.Stops.Count)
            throw new InvalidOperationException(
                $"paragem {index} não existe: este gradiente tem {g.Stops.Count} (índices 0 a {g.Stops.Count - 1}).");
        if (g.Stops.Count <= 2)
            throw new InvalidOperationException(
                "um gradiente precisa de 2 paragens no mínimo. Para o desfazer usa set_fill com uma cor só.");

        var avisos = new List<string>();
        var stopsList = g.Stops.ToList();
        var fora = stopsList[index];
        stopsList.RemoveAt(index);

        var novo = ApplyGradient(l, g with { Stops = stopsList }, avisos);
        Mutate(() => _layers[ix] = novo);

        var ng = novo.FillGradient!;
        return new
        {
            ok = true,
            id = novo.Key,
            removed = new { index, color = GradHex(fora.EvalArgb(0)), pos = fora.Pos },
            count = ng.Stops.Count,
            stops = StopsOut(ng, 0),
            warnings = avisos.ToArray(),
        };
    }
}
