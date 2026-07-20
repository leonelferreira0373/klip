using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Klip.Engine;
using Klip.Model;

namespace Klip.App;

/// <summary>
/// Cores SPOT (PANTONE, HKS, TOYO, DIC, FOCOLTONE, TRUMATCH) — bus + UI.
///
/// Os livros NÃO vêm embutidos: são lidos do CorelDRAW instalado nesta máquina
/// (<see cref="SpotPalettes"/>). Por isso todo o texto que sai daqui diz de ONDE veio a cor —
/// sem Corel só há as paletas livres, e o utilizador tem de saber disso antes de mandar imprimir.
///
/// E é só isso: uma etiqueta com o CMYK do livro. Quem garante a cor na chapa é o
/// export_cmyk com perfil ICC — aqui não se promete certificação nenhuma.
/// </summary>
public partial class MainWindow : Window
{
    // Largura do flyout: cada linha leva mancha + código + CMYK; abaixo disto o CMYK partia-se em duas linhas.
    private const double SpotFlyoutWidth = 340;

    // Um livro PANTONE traz ~2000 cores. Materializar 2000 botões Avalonia de uma vez cola a UI
    // quase um segundo — por isso a lista mostra só as primeiras N e a caixa de procura faz o resto.
    private const int SpotFlyoutRows = 240;

    // ---------------------------------------------------------------- bus

    /// <summary>Aplica uma cor spot pelo código, no fill ou no stroke.</summary>
    public object ApiSetSpot(string id, string code, string? target)
    {
        int ix = FindLayer(id);
        var l = Sel(id);

        var hit = LookupSpot(code, null)
                  ?? throw new InvalidOperationException(SpotNaoEncontrada(code));
        var (book, col) = hit;
        var spot = ToSpotRef(book, col);

        bool stroke = string.Equals(target, "stroke", StringComparison.OrdinalIgnoreCase);

        Mutate(() => _layers[ix] = stroke
            ? l with
            {
                StrokeArgb = col.Argb,
                // NÃO escrever o track de cor: ele MANDA sobre o uint e ninguém no código o volta a
                // pôr a null, portanto a camada ficava com a cor presa — um set_stroke a seguir
                // reportava ok e não mudava nada. O uint sozinho já pinta.
                StrokeColor = l.StrokeColor is { Keys.Count: > 1 } ? l.StrokeColor : null,
                // sem espessura o contorno não se desenha — a cor "não pegava" e parecia bug do verbo
                StrokeWidth = l.StrokeWidth > 0 ? l.StrokeWidth : 4,
                StrokeSpot = spot,
            }
            : l with
            {
                FillArgb = col.Argb,
                // idem: só se mantém o track quando ele é uma ANIMAÇÃO a sério (>1 keyframe),
                // senão a cor ficava presa e os verbos de cor seguintes não pegavam
                FillColor = l.FillColor is { Keys.Count: > 1 } ? l.FillColor : null,
                // uma spot é cor CHAPADA: um gradiente por baixo manda sobre o FillArgb e a cor de marca
                // nunca chegava a aparecer — logo, escolher spot desfaz o gradiente de propósito
                FillGradient = null,
                FillArgb2 = null,
                FillSpot = spot,
            });

        return new
        {
            ok = true,
            target = stroke ? "stroke" : "fill",
            code = spot.Code,
            library = spot.Library,
            hex = Hex(col.Argb),
            cmyk = spot.HasCmyk
                ? (object?)new { c = R2(spot.C), m = R2(spot.M), y = R2(spot.Y), k = R2(spot.K) }
                : null,
            source = OrigemCurta(book),
            note = "chapa CMYK do livro, em 0-100 (a mesma escala do set_cmyk). A cor final para gráfica sai do export_cmyk com perfil ICC.",
        };
    }

    /// <summary>Cores spot mais próximas de um hex, por ΔE.</summary>
    public object ApiFindSpot(string hex, int n, string? library)
    {
        uint argb = ParseColor(hex, 0);
        if (argb == 0) throw new InvalidOperationException($"cor '{hex}' ilegível — usa #RRGGBB");

        int take = n <= 0 ? 5 : Math.Min(n, 50);
        var alvo = LabD50(argb);

        // Só livros profissionais: responder "parecido com PANTONE 185" com o "crimson" do CSS não serve a ninguém.
        var hits = LivrosPara(library)
            .Where(b => b.Group != "Livre")
            .SelectMany(b => b.Colors.Select(c => (book: b, color: c)))
            // Lab dos DOIS lados derivado do sRGB, de propósito. O livro traz também o Lab MEDIDO da
            // tinta, que é mais fiel ao papel — mas comparar uma cor de ecrã contra um valor medido é
            // misturar domínios: dar o hex exacto de um PANTONE devolvia OUTRO PANTONE em primeiro.
            // Aqui a pergunta é "que cor do livro se parece com este pixel", e essa responde-se em sRGB.
            .Select(t => (t.book, t.color, dE: DistLab(alvo, LabD50(t.color.Argb))))
            .OrderBy(t => t.dE)
            .Take(take)
            .ToList();

        if (hits.Count == 0)
            return new
            {
                ok = false,
                hex = Hex(argb),
                matches = Array.Empty<object>(),
                note = SemLivrosProfissionais(),
            };

        return new
        {
            ok = true,
            hex = Hex(argb),
            matches = hits.Select(h => new
            {
                code = h.color.Name,
                library = h.book.Name,
                hex = Hex(h.color.Argb),
                dE = Math.Round(h.dE, 2),
                // sem isto, uma cor cujo sRGB foi calculado a partir do CMYK (TOYO, DIC, HKS,
                // TRUMATCH, FOCOLTONE — não trazem RGB nenhum) aparecia com um ΔE tão preciso
                // como o de um PANTONE medido. O ΔE é real; o que ele mede é que muda.
                rgb_estimado = h.color.RgbDerivado ? (object)true : null,
                cmyk = h.color.HasCmyk
                    ? (object?)new { c = Pct(h.color.C), m = Pct(h.color.M), y = Pct(h.color.Y), k = Pct(h.color.K) }
                    : null,
            }).ToList(),
            aviso_rgb_estimado = hits.Any(h => h.color.RgbDerivado)
                ? "Algumas destas cores (TOYO, DIC, HKS, TRUMATCH, FOCOLTONE) não trazem sRGB no livro — "
                  + "o valor de ecrã é calculado do CMYK, sem perfil. Para essas, o ΔE compara contra uma estimativa."
                : null,
            note = "ΔE2000 entre a tua cor e o sRGB do livro. É uma sugestão de tradução, não uma prova de cor — "
                 + "para a gráfica, confirma com export_cmyk.",
        };
    }

    /// <summary>Procura por código/nome nos livros disponíveis.</summary>
    public object ApiListSpot(string? filter, int limit, string? library)
    {
        int take = limit <= 0 ? 40 : Math.Min(limit, 500);
        List<(SpotPalettes.Palette book, SpotPalettes.SpotColor color)> hits;

        if (string.IsNullOrWhiteSpace(filter))
        {
            hits = LivrosPara(library).SelectMany(b => b.Colors.Select(c => (book: b, color: c)))
                                      .Take(take).ToList();
        }
        else
        {
            var permitidos = LivrosPara(library).Select(b => b.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            // Search é preguiçoso e pára no limite de RESULTADOS, não de varrimento: um tecto generoso
            // aqui só é gasto quando o filtro de livro rejeita quase tudo — o Take(take) trava o resto.
            hits = SpotPalettes.Search(filter, 5000)
                .Where(h => permitidos.Contains(h.pal.Name))
                .Take(take)
                .Select(h => (book: h.pal, color: h.color))
                .ToList();
        }

        return new
        {
            ok = true,
            count = hits.Count,
            colors = hits.Select(h => new
            {
                code = h.color.Name,
                library = h.book.Name,
                hex = Hex(h.color.Argb),
                cmyk = h.color.HasCmyk
                    ? (object?)new { c = Pct(h.color.C), m = Pct(h.color.M), y = Pct(h.color.Y), k = Pct(h.color.K) }
                    : null,
            }).ToList(),
            note = hits.Count == 0 && !HaLivrosProfissionais ? SemLivrosProfissionais() : null,
        };
    }

    /// <summary>Que livros de cor existem nesta máquina.</summary>
    public object ApiListPalettes()
    {
        var livros = LivrosPara(null).ToList();
        bool pro = HaLivrosProfissionais;

        return new
        {
            ok = true,
            professional = pro,
            count = livros.Count,
            palettes = livros.Select(p => new
            {
                name = p.Name,
                group = p.Group,
                colors = p.Colors.Count,
                source = p.Source,
            }).ToList(),
            note = pro
                ? "Os livros profissionais são lidos do CorelDRAW instalado nesta máquina (o KLIP não os distribui). A cor para gráfica confirma-se no export_cmyk com perfil ICC."
                : SemLivrosProfissionais(),
        };
    }

    // ---------------------------------------------------------------- UI

    /// <summary>Lista escolhível de cores spot, para o seletor de cor.</summary>
    internal Control SpotFlyout(Action<uint, SpotRef> pick)
    {
        var livros = LivrosPara(null).ToList();

        var linhas = new StackPanel { Spacing = 1 };
        var scroll = new ScrollViewer
        {
            MaxHeight = 300,
            Content = linhas,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };

        var procura = new TextBox
        {
            Watermark = "Procurar código ou nome…",
            FontSize = 11.5, Height = 28, Padding = new Thickness(7, 0),
            CornerRadius = new CornerRadius(7),
        };

        var seletor = new ComboBox
        {
            FontSize = 11, Height = 28, HorizontalAlignment = HorizontalAlignment.Stretch,
            CornerRadius = new CornerRadius(7),
        };
        seletor.ItemsSource = new[] { "Todos os livros" }
            .Concat(livros.Select(b => $"{b.Name}  ({b.Colors.Count})"))
            .ToList();
        seletor.SelectedIndex = 0;   // depois do ItemsSource, senão a atribuição perde-se

        var rodape = new TextBlock
        {
            FontSize = 9.5, TextWrapping = TextWrapping.Wrap, LineHeight = 13,
            Foreground = new SolidColorBrush(Color.Parse("#8A8A87")),
        };

        void Reconstruir()
        {
            linhas.Children.Clear();
            int sel = seletor.SelectedIndex;
            var ambito = sel <= 0 ? livros : new List<SpotPalettes.Palette> { livros[sel - 1] };
            string q = procura.Text?.Trim() ?? "";

            int mostradas = 0, encontradas = 0;
            foreach (var b in ambito)
                foreach (var c in b.Colors)
                {
                    if (q.Length > 0 && !c.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
                    encontradas++;
                    if (mostradas >= SpotFlyoutRows) continue;   // conta tudo, desenha pouco — ver SpotFlyoutRows
                    linhas.Children.Add(SpotRow(b, c, pick));
                    mostradas++;
                }

            string origem = ambito.Count == 1
                ? $"{ambito[0].Name} — {OrigemCurta(ambito[0])}"
                : HaLivrosProfissionais
                    ? $"{livros.Count} livros — os profissionais são lidos do CorelDRAW instalado nesta máquina"
                    : "sem CorelDRAW instalado nesta máquina: só existem as paletas livres do KLIP";

            string cabeca = encontradas == 0
                ? "Nenhuma cor com esse texto."
                : encontradas > mostradas
                    ? $"{mostradas} de {encontradas} — escreve mais para afinar."
                    : $"{encontradas} cores.";

            rodape.Text = cabeca + "\n" + origem
                        + "\nA cor definitiva para gráfica sai do export CMYK com perfil ICC.";
        }

        procura.TextChanged += (_, _) => Reconstruir();
        seletor.SelectionChanged += (_, _) => Reconstruir();
        Reconstruir();

        var raiz = new StackPanel { Width = SpotFlyoutWidth, Spacing = 6, Margin = new Thickness(6) };
        raiz.Children.Add(procura);
        raiz.Children.Add(seletor);
        raiz.Children.Add(scroll);
        raiz.Children.Add(new Border
        {
            Height = 1, Background = new SolidColorBrush(Color.Parse("#ECECEA")), Margin = new Thickness(0, 2, 0, 0),
        });
        raiz.Children.Add(rodape);
        return raiz;
    }

    /// <summary>Uma linha do flyout: mancha + código + chapa CMYK.</summary>
    private Control SpotRow(SpotPalettes.Palette book, SpotPalettes.SpotColor c, Action<uint, SpotRef> pick)
    {
        var mancha = new Border
        {
            Width = 18, Height = 18, CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.FromUInt32(c.Argb)),
            BorderBrush = new SolidColorBrush(Color.Parse("#DDDDDA")), BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var codigo = new TextBlock
        {
            Text = c.Name, FontSize = 11.5, Margin = new Thickness(8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = new SolidColorBrush(Color.Parse("#3A3A38")),
        };
        // sem CMYK a linha ficava vazia à direita e não se distinguia uma cor de livro de uma cor livre
        var chapa = new TextBlock
        {
            Text = c.HasCmyk ? $"C{Pct(c.C)} M{Pct(c.M)} Y{Pct(c.Y)} K{Pct(c.K)}" : Hex(c.Argb),
            FontSize = 9.5, VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.Parse("#9A9A97")),
        };

        // qualificado: a MainWindow tem um membro chamado Grid (o passo da grelha do editor) que
        // esconde o tipo Avalonia.Controls.Grid neste ficheiro
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        Avalonia.Controls.Grid.SetColumn(mancha, 0);
        Avalonia.Controls.Grid.SetColumn(codigo, 1);
        Avalonia.Controls.Grid.SetColumn(chapa, 2);
        g.Children.Add(mancha);
        g.Children.Add(codigo);
        g.Children.Add(chapa);

        var btn = new Button
        {
            Content = g, Height = 28, Padding = new Thickness(6, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
        };
        ToolTip.SetTip(btn, $"{c.Name} · {book.Name}\n{OrigemCurta(book)}");
        btn.Click += (_, _) => pick(c.Argb, ToSpotRef(book, c));
        return btn;
    }

    // ---------------------------------------------------------------- interno

    private static bool HaLivrosProfissionais => SpotPalettes.All.Any(p => p.Group != "Livre");

    private static string SemLivrosProfissionais() =>
        "Não há livros profissionais nesta máquina. O KLIP lê os livros PANTONE/HKS/TOYO/DIC/FOCOLTONE/TRUMATCH " +
        "de um CorelDRAW instalado (são licenciados, não vêm com o KLIP) — sem ele só existem as paletas livres.";

    private static string OrigemCurta(SpotPalettes.Palette p) =>
        p.Source == "embutida" ? "paleta livre embutida no KLIP" : Path.GetFileName(p.Source);

    /// <summary>Livros a considerar, filtrados por nome/grupo. Profissionais primeiro.</summary>
    private static IEnumerable<SpotPalettes.Palette> LivrosPara(string? library)
    {
        var q = SpotPalettes.All.Where(p =>
            library is not { Length: > 0 }
            || p.Name.Contains(library, StringComparison.OrdinalIgnoreCase)
            || p.Group.Contains(library, StringComparison.OrdinalIgnoreCase));

        // quem escreve "185" quer o PANTONE, não um "185" que calhe estar numa paleta livre
        return q.OrderBy(p => p.Group == "Livre" ? 1 : 0)
                .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Resolve um código escrito à mão para uma cor concreta de um livro concreto.</summary>
    private static (SpotPalettes.Palette book, SpotPalettes.SpotColor color)? LookupSpot(string code, string? library)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var livros = LivrosPara(library).ToList();
        string alvo = code.Trim();
        string alvoN = Norm(alvo);

        // 1ª: nome igual, tal e qual — o caso normal ("PANTONE 185 C" copiado do livro)
        foreach (var b in livros)
            foreach (var c in b.Colors)
                if (string.Equals(c.Name.Trim(), alvo, StringComparison.OrdinalIgnoreCase))
                    return (b, c);

        // 2ª: normalizado — "pantone185c", "Pantone 185C" e "PANTONE 185 C" são a mesma coisa para quem escreve
        foreach (var b in livros)
            foreach (var c in b.Colors)
                if (Norm(c.Name) == alvoN)
                    return (b, c);

        // 3ª: escreveu só a cauda ("185") — só vale se for INEQUÍVOCO, senão devolvíamos uma cor à sorte
        var caudas = livros.SelectMany(b => b.Colors.Select(c => (book: b, color: c)))
                           .Where(t => Norm(t.color.Name).EndsWith(alvoN, StringComparison.Ordinal))
                           .Take(2).ToList();
        return caudas.Count == 1 ? caudas[0] : null;
    }

    /// <summary>Mensagem de falha COM alternativas — falhar seco obrigava a adivinhar a grafia do livro.</summary>
    private static string SpotNaoEncontrada(string code)
    {
        var perto = SpotPalettes.Search(code, 6).Select(h => h.color.Name).Distinct().Take(6).ToList();
        if (perto.Count == 0)
        {
            // o texto todo não deu; tenta só a parte numérica ("185" de "pantone-185"), que é o que identifica a cor
            var digitos = new string(code.Where(char.IsDigit).ToArray());
            if (digitos.Length >= 2)
                perto = SpotPalettes.Search(digitos, 6).Select(h => h.color.Name).Distinct().Take(6).ToList();
        }

        if (perto.Count > 0)
            return $"não encontrei a cor '{code}'. Parecidas: {string.Join(" · ", perto)}";

        return HaLivrosProfissionais
            ? $"não encontrei a cor '{code}' em nenhum dos {SpotPalettes.All.Count} livros desta máquina (usa list_spot para procurar)."
            : $"não encontrei a cor '{code}'. " + SemLivrosProfissionais();
    }

    private static SpotRef ToSpotRef(SpotPalettes.Palette book, SpotPalettes.SpotColor c) => new(
        c.Name, book.Name, c.Argb,
        // o livro guarda CMYK em 0..1; o resto do KLIP (set_cmyk, CmykExport.CmykToArgb) fala 0-100.
        // Guardamos na moeda da casa para a etiqueta atravessar o export sem ninguém ter de reescalar.
        c.C * 100.0, c.M * 100.0, c.Y * 100.0, c.K * 100.0, c.HasCmyk,
        c.L, c.A, c.B);

    private static string Norm(string s) =>
        new(s.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static double R2(double v) => Math.Round(v, 2);
    private static int Pct(float v) => (int)Math.Round(v * 100f);

    /// <summary>
    /// ΔE2000 — a métrica que corresponde ao que o olho vê, e não a distância a direito do CIE76.
    /// (Implementação validada contra os 34 pares de referência do Sharma, Wu &amp; Dalal.)
    /// </summary>
    private static double DistLab((double L, double a, double b) x, (double L, double a, double b) y)
        => Klip.Engine.ColorScience.DeltaE2000(x, y);

    /// <summary>
    /// sRGB → L*a*b* em D50. Repetido aqui de propósito: o Nearest do SpotPalettes devolve UMA cor e
    /// as conversões dele são privadas — para ordenar as N melhores precisamos do Lab em bruto. Tem de
    /// ser D50 (e não D65) porque é nesse ponto branco que vêm os Lab dos livros do Corel.
    /// </summary>
    private static (double L, double a, double b) LabD50(uint argb)
    {
        static double Lin(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        double r = Lin(((argb >> 16) & 0xFF) / 255.0),
               g = Lin(((argb >> 8) & 0xFF) / 255.0),
               b = Lin((argb & 0xFF) / 255.0);
        double X = 0.4360747 * r + 0.3850649 * g + 0.1430804 * b;
        double Y = 0.2225045 * r + 0.7168786 * g + 0.0606169 * b;
        double Z = 0.0139322 * r + 0.0971045 * g + 0.7141733 * b;
        static double F(double t) => t > 216.0 / 24389.0 ? Math.Cbrt(t) : (24389.0 / 27.0 * t + 16.0) / 116.0;
        double fx = F(X / 0.9642), fy = F(Y), fz = F(Z / 0.8249);
        return (116.0 * fy - 16.0, 500.0 * (fx - fy), 200.0 * (fy - fz));
    }
}
