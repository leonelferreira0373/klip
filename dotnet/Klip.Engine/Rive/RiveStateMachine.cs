using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Klip.Engine.Rive;

// =====================================================================================
//  MÁQUINAS DE ESTADOS DO RIVE
//
//  PORQUÊ ISTO EXISTE: sem máquina de estados, um .riv no KLIP é um GIF vectorial — toca
//  uma LinearAnimation do princípio ao fim e mais nada. A máquina de estados é a camada
//  que faz o .riv REAGIR: tem INPUTS (booleano / número / gatilho), ESTADOS (cada estado
//  aponta para uma LinearAnimation) e TRANSIÇÕES com condições sobre os inputs.
//
//  Tudo o que está aqui foi extraído do runtime C++ que já vive em
//  dotnet/external/rive-runtime (headers gerados + src/animation/*.cpp), não foi inventado.
//  Ficheiros de referência:
//     include/rive/generated/animation/*_base.hpp        → typeKey e *PropertyKey
//     include/rive/animation/state_transition_flags.hpp  → bits das flags
//     include/rive/animation/transition_condition_op.hpp → operadores de comparação
//     src/importers/state_machine_layer_importer.cpp     → como se resolvem os índices
//     src/animation/state_transition.cpp                 → StateTransition::allowed()
//     src/animation/state_machine_instance.cpp           → o ciclo de avanço por camada
// =====================================================================================

/// <summary>Género de um input. O nome do tipo em texto vai em <see cref="RiveInput.Tipo"/>.</summary>
public enum RiveTipoInput { Booleano = 0, Numero = 1, Gatilho = 2 }

/// <summary>
/// Fotografia (imutável) de um input num dado instante. É um record de propósito: quem consome
/// a UI não deve poder escrever aqui por acidente — muda-se sempre por
/// <see cref="RiveMachine.Definir(string,double)"/> / <see cref="RiveMachine.Disparar"/>.
/// </summary>
public sealed record RiveInput(string Nome, string Tipo, double Valor);

/// <summary>Referência a uma LinearAnimation do artboard (o que a máquina precisa de saber dela).</summary>
public sealed record RiveAnimacaoRef(string Nome, double DuracaoSegundos, int Loop, double Velocidade);

/// <summary>Uma condição de transição: compara um input com um valor.</summary>
public sealed class RiveCondicao
{
    /// <summary>Type key original: 68 gatilho, 70 número, 71 booleano.</summary>
    public int Gene;
    /// <summary>Índice na lista de inputs da máquina (-1 = não resolvido).</summary>
    public int InputIndice = -1;
    /// <summary>TransitionConditionOp: 0 ==, 1 !=, 2 &lt;=, 3 &gt;=, 4 &lt;, 5 &gt;.</summary>
    public int Op;
    public double Valor;

    public string Descrever(IReadOnlyList<RiveInput> inputs)
    {
        string nome = InputIndice >= 0 && InputIndice < inputs.Count ? inputs[InputIndice].Nome : "?";
        return Gene switch
        {
            RiveKeys.TransitionTriggerCondition => nome + " disparado",
            RiveKeys.TransitionBoolCondition => nome + (Op == 0 ? " = verdadeiro" : " = falso"),
            RiveKeys.TransitionNumberCondition =>
                nome + " " + OpTexto(Op) + " " + Valor.ToString("0.###", CultureInfo.InvariantCulture),
            _ => nome + " (condição não suportada → sempre verdadeira)",
        };
    }

    private static string OpTexto(int op) => op switch
    { 0 => "=", 1 => "≠", 2 => "≤", 3 => "≥", 4 => "<", 5 => ">", _ => "?" };
}

/// <summary>Uma transição de um estado para outro da MESMA camada.</summary>
public sealed class RiveTransicao
{
    /// <summary>Índice do estado de destino dentro da camada (-1 = inválido).</summary>
    public int ParaEstado = -1;
    public uint Flags;
    /// <summary>Mistura: milissegundos, ou percentagem da animação de origem se a flag o disser.</summary>
    public uint Duracao;
    /// <summary>Idem para o tempo de saída.</summary>
    public uint TempoSaida;
    /// <summary>Peso para escolha aleatória (só usado se a camada/estado tiver a flag Random).</summary>
    public uint PesoAleatorio;
    public List<RiveCondicao> Condicoes { get; } = new();

    // Bits de StateTransitionFlags (state_transition_flags.hpp)
    public bool Desactivada => (Flags & 1) != 0;
    public bool DuracaoEmPercentagem => (Flags & 2) != 0;
    public bool UsaTempoSaida => (Flags & 4) != 0;
    public bool TempoSaidaEmPercentagem => (Flags & 8) != 0;
    public bool PausaAoSair => (Flags & 16) != 0;
    public bool SaidaAntecipada => (Flags & 32) != 0;
}

/// <summary>Um estado de uma camada. Só o estado "animacao" (e a mistura) toca alguma coisa.</summary>
public sealed class RiveEstado
{
    public string Nome = "";
    public int TipoBruto;
    /// <summary>"qualquer" | "entrada" | "saida" | "animacao" | "mistura" | "outro".</summary>
    public string Genero = "outro";
    /// <summary>Índice na lista de animações do artboard (-1 se o estado não toca nada).</summary>
    public int AnimacaoIndice = -1;
    /// <summary>Animações de um blend state, por ordem (não misturamos — usamos a primeira).</summary>
    public List<int> Mistura { get; } = new();
    public uint Flags;
    public List<RiveTransicao> Transicoes { get; } = new();

    public bool EscolhaAleatoria => (Flags & 1) != 0;

    /// <summary>
    /// Nome para mostrar na UI. O .riv NÃO guarda o nome dos estados — LayerState desce de
    /// StateMachineLayerComponent que desce directamente de Core e não tem propriedade de nome;
    /// o nome fica só no editor. Por isso inventa-se um legível: a animação que toca, ou o papel.
    /// </summary>
    public string NomeVisivel(IReadOnlyList<RiveAnimacaoRef> animacoes)
    {
        if (!string.IsNullOrEmpty(Nome)) return Nome;
        if (AnimacaoIndice >= 0 && AnimacaoIndice < animacoes.Count) return animacoes[AnimacaoIndice].Nome;
        if (Mistura.Count > 0 && Mistura[0] >= 0 && Mistura[0] < animacoes.Count)
            return "mistura: " + animacoes[Mistura[0]].Nome;
        return Genero switch
        {
            "qualquer" => "(qualquer estado)",
            "entrada" => "(entrada)",
            "saida" => "(saída)",
            _ => "(estado vazio)",
        };
    }
}

/// <summary>
/// Uma camada. Cada camada tem O SEU estado activo e avança sozinha — é por isso que o
/// runtime do Rive consegue, por exemplo, piscar os olhos enquanto o corpo anda.
/// </summary>
public sealed class RiveCamada
{
    public string Nome = "";
    public List<RiveEstado> Estados { get; } = new();

    internal int IdxQualquer = -1, IdxEntrada = -1, IdxSaida = -1;

    /// <summary>Índices dos três estados de sistema que toda a camada válida tem (-1 se faltar).</summary>
    public int IndiceQualquer => IdxQualquer;
    public int IndiceEntrada => IdxEntrada;
    public int IndiceSaida => IdxSaida;

    /// <summary>Índice do estado activo (-1 antes do primeiro Avancar/Reiniciar).</summary>
    public int Actual { get; internal set; } = -1;
    /// <summary>Segundos de relógio decorridos desde a entrada no estado actual.</summary>
    public double Tempo { get; internal set; }

    /// <summary>
    /// Segundos que faltam da mistura da transição em curso. Não misturamos imagem nenhuma, mas
    /// contamos o tempo: enquanto corre, a camada não muda outra vez de estado (a não ser que a
    /// transição permita saída antecipada). Sem isto, uma cadeia A→B→C com durações atravessava-se
    /// toda num só quadro e o estado do meio nunca se via.
    /// </summary>
    public double MisturaRestante { get; internal set; }
    internal bool MisturaAntecipavel;

    public RiveEstado? EstadoActual => Actual >= 0 && Actual < Estados.Count ? Estados[Actual] : null;
}

/// <summary>O que uma camada está a tocar neste instante.</summary>
public sealed record RivePose(string Camada, string Estado, string? Animacao, double Tempo, bool Mudou);

/// <summary>
/// Resultado de um <see cref="RiveMachine.Avancar"/>: a animação a desenhar e em que segundo.
/// <c>Animacao</c>/<c>Tempo</c> são os da primeira camada que tem animação — é o que se passa
/// directamente a <c>RiveClip.Draw(canvas, dst, caminho, Animacao, Tempo)</c>.
/// </summary>
public sealed record RiveQuadro(string? Animacao, double Tempo, bool Mudou, IReadOnlyList<RivePose> Camadas);

/// <summary>
/// Uma máquina de estados lida de um .riv, já com estado de execução dentro (valores dos inputs
/// e estado activo de cada camada). Não é partilhável entre duas camadas do KLIP: cada
/// <see cref="RiveStateMachine.Ler(string)"/> devolve instâncias novas de propósito.
/// </summary>
public sealed class RiveMachine
{
    internal sealed class Entrada
    {
        public string Nome = "";
        public RiveTipoInput Tipo;
        public double Valor;
        public bool Disparado;
        /// <summary>Camadas que já consumiram este gatilho no avanço em curso.</summary>
        public readonly HashSet<int> UsadoEm = new();
    }

    private readonly List<Entrada> _inputs = new();
    private readonly List<RiveCamada> _camadas = new();
    private readonly List<RiveAnimacaoRef> _anims = new();

    public string Nome { get; internal set; } = "";
    public string Artboard { get; internal set; } = "";
    public int ArtboardIndice { get; internal set; }

    internal List<Entrada> InputsInternos => _inputs;
    internal List<RiveCamada> CamadasInternas => _camadas;
    internal List<RiveAnimacaoRef> AnimacoesInternas => _anims;

    public IReadOnlyList<RiveCamada> Camadas => _camadas;
    public IReadOnlyList<RiveAnimacaoRef> Animacoes => _anims;

    /// <summary>Fotografia dos inputs. Constrói uma lista nova a cada leitura (são records imutáveis).</summary>
    public IReadOnlyList<RiveInput> Inputs
    {
        get
        {
            var l = new List<RiveInput>(_inputs.Count);
            foreach (var e in _inputs)
                l.Add(new RiveInput(e.Nome, TipoTexto(e.Tipo), e.Tipo == RiveTipoInput.Gatilho ? (e.Disparado ? 1 : 0) : e.Valor));
            return l;
        }
    }

    public int TotalEstados { get { int n = 0; foreach (var c in _camadas) n += c.Estados.Count; return n; } }
    public int TotalTransicoes
    {
        get { int n = 0; foreach (var c in _camadas) foreach (var s in c.Estados) n += s.Transicoes.Count; return n; }
    }

    public static string TipoTexto(RiveTipoInput t) => t switch
    { RiveTipoInput.Booleano => "booleano", RiveTipoInput.Numero => "numero", _ => "gatilho" };

    // ---------------- inputs ----------------

    public int IndiceInput(string nome)
    {
        for (int i = 0; i < _inputs.Count; i++)
            if (string.Equals(_inputs[i].Nome, nome, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>
    /// Define um input numérico (ou booleano, com 0/1). Devolve falso se o nome não existir.
    /// Num GATILHO escrever um valor arma-o (≠0) ou desarma-o: sem isto, uma UI genérica que
    /// escrevesse por nome recebia "true" e não acontecia nada — o pior tipo de falha.
    /// </summary>
    public bool Definir(string nome, double valor)
    {
        int i = IndiceInput(nome);
        if (i < 0) return false;
        if (_inputs[i].Tipo == RiveTipoInput.Gatilho) { _inputs[i].Disparado = valor != 0; return true; }
        _inputs[i].Valor = valor;
        return true;
    }

    public bool Definir(string nome, bool valor) => Definir(nome, valor ? 1 : 0);

    /// <summary>
    /// Arma um gatilho. CONSOME-SE: ao fim do próximo <see cref="Avancar"/> volta a zero, tenha ou
    /// não feito disparar alguma transição — é exactamente o que o runtime faz (SMITrigger::advanced).
    /// </summary>
    public bool Disparar(string nome)
    {
        int i = IndiceInput(nome);
        if (i < 0 || _inputs[i].Tipo != RiveTipoInput.Gatilho) return false;
        _inputs[i].Disparado = true;
        return true;
    }

    /// <summary>
    /// Valor actual de um input. Num gatilho devolve 1 enquanto estiver armado — a mesma leitura
    /// que <see cref="Inputs"/> dá. (Antes lia o campo Valor, que num gatilho é sempre 0: a UI
    /// mostrava 0 logo a seguir a um Disparar bem-sucedido.)
    /// </summary>
    public double Valor(string nome)
    {
        int i = IndiceInput(nome);
        if (i < 0) return 0;
        var e = _inputs[i];
        return e.Tipo == RiveTipoInput.Gatilho ? (e.Disparado ? 1 : 0) : e.Valor;
    }

    // ---------------- execução ----------------

    /// <summary>Volta ao estado de entrada de cada camada e zera os tempos (não mexe nos inputs).</summary>
    public void Reiniciar()
    {
        foreach (var c in _camadas)
        {
            c.Actual = c.IdxEntrada >= 0 ? c.IdxEntrada : (c.Estados.Count > 0 ? 0 : -1);
            c.Tempo = 0;
            c.MisturaRestante = 0;
            c.MisturaAntecipavel = false;
        }
    }

    /// <summary>
    /// Avança dt segundos. Devolve a animação activa e o tempo dentro dela.
    /// ATENÇÃO ao tempo devolvido: é tempo de RELÓGIO desde que se entrou no estado, sem
    /// enrolamento nem velocidade aplicada — porque o RivePlayer já multiplica pela Speed da
    /// animação e já faz oneShot/loop/pingPong. Aplicar aqui outra vez daria velocidade ao quadrado.
    /// </summary>
    public RiveQuadro Avancar(double dt)
    {
        if (_camadas.Count == 0) return new RiveQuadro(null, 0, false, Array.Empty<RivePose>());
        if (dt < 0) dt = 0;

        bool mudouAlgo = false;
        var poses = new List<RivePose>(_camadas.Count);

        for (int li = 0; li < _camadas.Count; li++)
        {
            var cam = _camadas[li];
            if (cam.Actual < 0)
            {
                cam.Actual = cam.IdxEntrada >= 0 ? cam.IdxEntrada : (cam.Estados.Count > 0 ? 0 : -1);
                cam.Tempo = 0; cam.MisturaRestante = 0; cam.MisturaAntecipavel = false;
            }
            if (cam.Actual < 0) { continue; }

            double tempoAnterior = cam.Tempo;
            cam.Tempo += dt;
            cam.MisturaRestante = Math.Max(0, cam.MisturaRestante - dt);

            bool mudou = false;
            // O runtime volta a avaliar depois de cada mudança (um estado de entrada pode encadear
            // várias transições no mesmo quadro). O tecto evita ciclos infinitos num .riv mal feito.
            for (int it = 0; it < 100; it++)
            {
                if (cam.MisturaRestante > 0 && !cam.MisturaAntecipavel) break;

                var de = cam.EstadoActual;
                var t = Escolher(li, cam, tempoAnterior);
                if (t is null) break;

                cam.MisturaRestante = TempoMistura(t, de);
                cam.MisturaAntecipavel = t.SaidaAntecipada;
                cam.Actual = t.ParaEstado;
                tempoAnterior = 0;
                cam.Tempo = 0;
                mudou = true;
                mudouAlgo = true;
            }

            var est = cam.EstadoActual;
            int ai = est is null ? -1 : (est.AnimacaoIndice >= 0 ? est.AnimacaoIndice : (est.Mistura.Count > 0 ? est.Mistura[0] : -1));
            string? animNome = ai >= 0 && ai < _anims.Count ? _anims[ai].Nome : null;
            poses.Add(new RivePose(cam.Nome, est is null ? "" : NomeDoEstado(est), animNome, cam.Tempo, mudou));
        }

        // Gatilhos consomem-se ao fim do avanço, depois de TODAS as camadas os terem podido ver.
        foreach (var e in _inputs) { e.Disparado = false; e.UsadoEm.Clear(); }

        string? anim = null; double tempo = 0;
        foreach (var p in poses) if (p.Animacao is not null) { anim = p.Animacao; tempo = p.Tempo; break; }
        return new RiveQuadro(anim, tempo, mudouAlgo, poses);
    }

    /// <summary>
    /// Escolhe a transição a seguir. Primeiro as do estado "qualquer" (AnyState), depois as do
    /// estado actual — é a ordem do runtime (updateState → tryChangeState(any) → tryChangeState(actual)).
    /// Devolve a transição escolhida, ou null se nada transita.
    /// </summary>
    private RiveTransicao? Escolher(int camadaIdx, RiveCamada cam, double tempoAnterior)
    {
        if (cam.IdxQualquer >= 0 && cam.IdxQualquer != cam.Actual)
        {
            // A partir do AnyState não há estado de origem "real", logo o tempo de saída não conta.
            var d = Primeira(camadaIdx, cam, cam.Estados[cam.IdxQualquer], null, 0);
            if (d is not null) return d;
        }
        var actual = cam.EstadoActual;
        if (actual is null) return null;
        return Primeira(camadaIdx, cam, actual, actual, tempoAnterior);
    }

    /// <summary>Primeira transição permitida (não implementamos a escolha aleatória por peso).</summary>
    private RiveTransicao? Primeira(int camadaIdx, RiveCamada cam, RiveEstado de, RiveEstado? origemReal, double tempoAnterior)
    {
        foreach (var t in de.Transicoes)
        {
            if (t.ParaEstado < 0 || t.ParaEstado >= cam.Estados.Count) continue;
            if (t.ParaEstado == cam.Actual) continue;                 // canChangeState: já lá estamos
            if (!Permitida(camadaIdx, cam, t, origemReal, tempoAnterior)) continue;
            Consumir(camadaIdx, t);
            return t;
        }
        return null;
    }

    /// <summary>StateTransition::mixTime — ms, ou percentagem da duração da animação de origem.</summary>
    private double TempoMistura(RiveTransicao t, RiveEstado? de)
    {
        if (t.Duracao == 0) return 0;
        if (!t.DuracaoEmPercentagem) return t.Duracao / 1000.0;
        double dur = de is not null && de.AnimacaoIndice >= 0 && de.AnimacaoIndice < _anims.Count
            ? _anims[de.AnimacaoIndice].DuracaoSegundos : 0;
        return t.Duracao / 100.0 * dur;
    }

    /// <summary>StateTransition::allowed — condições E tempo de saída.</summary>
    private bool Permitida(int camadaIdx, RiveCamada cam, RiveTransicao t, RiveEstado? de, double tempoAnterior)
    {
        if (t.Desactivada) return false;

        foreach (var c in t.Condicoes)
            if (!Avaliar(camadaIdx, c)) return false;

        if (!t.UsaTempoSaida) return true;

        // O tempo de saída só faz sentido a sair de um estado de animação; das outras origens
        // (incluindo o AnyState) o runtime ignora-o pura e simplesmente.
        if (de is null || de.AnimacaoIndice < 0 || de.AnimacaoIndice >= _anims.Count) return true;
        var a = _anims[de.AnimacaoIndice];
        double dur = a.DuracaoSegundos;
        if (dur <= 0) return true;

        double saida = t.TempoSaidaEmPercentagem ? t.TempoSaida / 100.0 * dur : t.TempoSaida / 1000.0;

        // Velocidade: o tempo guardado é de relógio; o runtime compara em tempo de animação.
        double vel = Math.Abs(a.Velocidade) < 1e-9 ? 1 : Math.Abs(a.Velocidade);
        double agora = cam.Tempo * vel;
        double antes = tempoAnterior * vel;

        // Se o tempo de saída cabe dentro de uma volta e a animação repete, sobe-se até à volta
        // em que estávamos — senão uma saída a 50% só dispararia na primeira volta.
        if (saida <= dur && a.Loop != 0) saida += Math.Floor(antes / dur) * dur;

        return agora >= saida;
    }

    private bool Avaliar(int camadaIdx, RiveCondicao c)
    {
        if (c.InputIndice < 0 || c.InputIndice >= _inputs.Count) return true;  // runtime tolera input em falta
        var e = _inputs[c.InputIndice];
        switch (c.Gene)
        {
            case RiveKeys.TransitionTriggerCondition:
                return e.Disparado && !e.UsadoEm.Contains(camadaIdx);
            case RiveKeys.TransitionBoolCondition:
                bool v = e.Valor != 0;
                return (v && c.Op == 0) || (!v && c.Op == 1);
            case RiveKeys.TransitionNumberCondition:
                return c.Op switch
                {
                    0 => e.Valor == c.Valor,
                    1 => e.Valor != c.Valor,
                    2 => e.Valor <= c.Valor,
                    3 => e.Valor >= c.Valor,
                    4 => e.Valor < c.Valor,
                    5 => e.Valor > c.Valor,
                    _ => false,
                };
            default:
                return true;   // condição de um tipo mais novo → o runtime também a dá por verdadeira
        }
    }

    /// <summary>Marca os gatilhos usados nesta camada, para não dispararem duas transições seguidas.</summary>
    private void Consumir(int camadaIdx, RiveTransicao t)
    {
        foreach (var c in t.Condicoes)
            if (c.Gene == RiveKeys.TransitionTriggerCondition && c.InputIndice >= 0 && c.InputIndice < _inputs.Count)
                _inputs[c.InputIndice].UsadoEm.Add(camadaIdx);
    }

    /// <summary>Nome legível de um estado (os estados não têm nome no ficheiro — ver RiveEstado).</summary>
    public string NomeDoEstado(RiveEstado e) => e.NomeVisivel(_anims);

    /// <summary>Resumo de uma linha para a barra de estado do painel.</summary>
    public string Resumo()
        => $"{Nome} · {_inputs.Count} inputs · {_camadas.Count} camadas · {TotalEstados} estados · {TotalTransicoes} transições";
}

/// <summary>
/// Leitor das máquinas de estados de um .riv. Faz a sua própria passagem sobre o fluxo binário —
/// de propósito: o RiveLoader agrupa por artboard/animação e achata tudo o resto numa lista única,
/// o que perde o ANINHAMENTO (que estado pertence a que camada, que condição a que transição).
/// Aqui só interessa esse aninhamento, por isso vale mais reler do que remendar o loader.
/// </summary>
public static class RiveStateMachine
{
    public static IReadOnlyList<RiveMachine> Ler(string caminhoRiv) => Ler(caminhoRiv, out _);

    /// <summary>Versão com erro devolvido em texto — para a barra do painel (nada de diálogos nativos).</summary>
    public static IReadOnlyList<RiveMachine> Ler(string caminhoRiv, out string? erro)
    {
        try
        {
            var b = File.ReadAllBytes(caminhoRiv);
            erro = null;
            return LerBytes(b);
        }
        catch (Exception ex)
        {
            erro = ex.Message;
            return Array.Empty<RiveMachine>();
        }
    }

    public static IReadOnlyList<RiveMachine> LerBytes(byte[] dados)
    {
        var objectos = LerFluxo(dados);
        return Construir(objectos);
    }

    // ---------------------------------------------------------------------------------
    //  Passagem 1 — fluxo binário → lista plana de objectos (mesma mecânica do RiveLoader,
    //  duplicada aqui porque não podemos mexer nele e precisamos da lista SEM agrupamento).
    // ---------------------------------------------------------------------------------
    private static List<RiveObject> LerFluxo(byte[] dados)
    {
        var r = new RiveReader(dados);
        if (r.ReadByte() != 'R' || r.ReadByte() != 'I' || r.ReadByte() != 'V' || r.ReadByte() != 'E')
            throw new InvalidOperationException("não é um ficheiro .riv (fingerprint)");
        r.ReadVarUint();   // major
        r.ReadVarUint();   // minor
        r.ReadVarUint();   // fileId

        var chaves = new List<int>();
        while (true) { int k = (int)r.ReadVarUint(); if (k == 0) break; chaves.Add(k); }

        // Bitmap de tipos de campo: 2 bits por propriedade, 4 propriedades por palavra de 32 bits
        // (os 24 bits de cima ficam por usar — é assim no formato, não é engano).
        var toc = new Dictionary<int, int>();
        int palavra = 0, bit = 8;
        foreach (var k in chaves)
        {
            if (bit == 8)
            {
                palavra = r.ReadByte() | (r.ReadByte() << 8) | (r.ReadByte() << 16) | (r.ReadByte() << 24);
                bit = 0;
            }
            toc[k] = (palavra >> bit) & 3;
            bit += 2;
        }

        var lista = new List<RiveObject>();
        while (!r.End)
        {
            int tipo;
            try { tipo = (int)r.ReadVarUint(); }
            catch (EndOfStreamRive) { break; }

            var obj = new RiveObject(tipo);
            bool ok = true;
            while (true)
            {
                int pk;
                try { pk = (int)r.ReadVarUint(); }
                catch (EndOfStreamRive) { ok = false; break; }
                if (pk == 0) break;

                int ft = RiveFieldTypes.Map.TryGetValue(pk, out var f) ? f : -1;
                if (ft < 0 && toc.TryGetValue(pk, out var tf)) ft = tf;
                // Sem tipo de campo não sabemos quantos bytes saltar → o fluxo desalinha-se. Parar.
                if (ft < 0) { ok = false; break; }

                object val;
                try
                {
                    val = ft switch
                    {
                        0 => (object)r.ReadVarUint(),
                        1 => r.ReadString(),
                        2 => r.ReadFloat32(),
                        3 => r.ReadColorU32(),
                        4 => (object)(r.ReadByte() == 1),
                        5 => r.ReadBytes((int)r.ReadVarUint()),
                        _ => r.ReadVarUint(),
                    };
                }
                catch (EndOfStreamRive) { ok = false; break; }
                obj.Props[pk] = val;
            }
            if (!ok && obj.Props.Count == 0) break;
            lista.Add(obj);
            if (!ok) break;
        }
        return lista;
    }

    // ---------------------------------------------------------------------------------
    //  Passagem 2 — lista plana → máquinas.
    //  O aninhamento no .riv é POSICIONAL: cada objecto pendura-se no último objecto do
    //  tipo-pai que passou (é o ImportStack do runtime). Daí as variáveis "corrente".
    // ---------------------------------------------------------------------------------
    private static List<RiveMachine> Construir(List<RiveObject> objectos)
    {
        var saida = new List<RiveMachine>();

        int artboardIdx = -1;
        string artboardNome = "";
        var animsArtboard = new List<RiveAnimacaoRef>();
        var maquinasArtboard = new List<RiveMachine>();

        RiveMachine? maq = null;
        RiveCamada? cam = null;
        RiveEstado? est = null;
        RiveTransicao? tr = null;
        bool dentroDeEscuta = false;   // ignoramos escutas de rato, mas os filhos delas também

        void FecharArtboard()
        {
            // As animações só ficam todas conhecidas no fim do artboard, e as máquinas
            // referem-nas por índice — por isso a ligação faz-se aqui, não à cabeça.
            foreach (var m in maquinasArtboard) m.AnimacoesInternas.AddRange(animsArtboard);
            maquinasArtboard.Clear();
        }

        foreach (var o in objectos)
        {
            switch (o.TypeKey)
            {
                case RiveKeys.Artboard:
                    FecharArtboard();
                    artboardIdx++;
                    artboardNome = o.S(RiveKeys.NameKey);
                    animsArtboard = new List<RiveAnimacaoRef>();
                    maq = null; cam = null; est = null; tr = null; dentroDeEscuta = false;
                    continue;

                case RiveKeys.LinearAnimation:
                {
                    // DEFEITOS DO RUNTIME, não zeros. LinearAnimationBase declara
                    //   uint32_t m_Fps = 60;  uint32_t m_Duration = 60;  float m_Speed = 1.0f;
                    // e o exportador OMITE a propriedade quando ela vale o defeito. Medido no
                    // corpus vendorizado: 1213 de 2096 animações não escrevem a chave 57.
                    // Ler 0 aqui punha DuracaoSegundos=0, e uma duração 0 desliga o tempo de
                    // saída em percentagem (dur <= 0 → return true), ou seja a transição
                    // dispara logo em vez de esperar pelo fim da animação.
                    int fps = (int)o.U(RiveKeys.FpsKey, 60);
                    int dur = (int)o.U(RiveKeys.DurationKey, 60);
                    animsArtboard.Add(new RiveAnimacaoRef(
                        o.S(RiveKeys.AnimNameKey),
                        fps > 0 ? (double)dur / fps : 0,
                        (int)o.U(RiveKeys.LoopValueKey),
                        o.D(RiveKeys.SpeedKey, 1)));
                    continue;
                }

                case RiveKeys.StateMachine:
                    maq = new RiveMachine
                    {
                        Nome = o.S(RiveKeys.SmMachineNameKey),
                        Artboard = artboardNome,
                        ArtboardIndice = artboardIdx,
                    };
                    saida.Add(maq);
                    maquinasArtboard.Add(maq);
                    cam = null; est = null; tr = null; dentroDeEscuta = false;
                    continue;

                case RiveKeys.StateMachineBool:
                case RiveKeys.StateMachineNumber:
                case RiveKeys.StateMachineTrigger:
                    if (maq is null) continue;
                    maq.InputsInternos.Add(new RiveMachine.Entrada
                    {
                        Nome = o.S(RiveKeys.SmNameKey),
                        Tipo = o.TypeKey switch
                        {
                            RiveKeys.StateMachineBool => RiveTipoInput.Booleano,
                            RiveKeys.StateMachineNumber => RiveTipoInput.Numero,
                            _ => RiveTipoInput.Gatilho,
                        },
                        Valor = o.TypeKey == RiveKeys.StateMachineNumber
                            ? o.D(RiveKeys.SmNumberValueKey)
                            : (o.TypeKey == RiveKeys.StateMachineBool && o.B(RiveKeys.SmBoolValueKey) ? 1 : 0),
                    });
                    dentroDeEscuta = false;
                    continue;

                case RiveKeys.StateMachineListener:
                case RiveKeys.StateMachineListenerNovo:
                    // Escutas (cliques/hover). O KLIP ainda não tem hit-testing sobre o .riv;
                    // marcamos para que os filhos da escuta não sejam confundidos com condições.
                    dentroDeEscuta = true;
                    continue;

                case RiveKeys.ListenerTriggerChange:
                case RiveKeys.ListenerInputChange:
                case RiveKeys.ListenerBoolChange:
                case RiveKeys.ListenerNumberChange:
                    continue;

                case RiveKeys.StateMachineLayer:
                    if (maq is null) continue;
                    cam = new RiveCamada { Nome = o.S(RiveKeys.SmNameKey) };
                    maq.CamadasInternas.Add(cam);
                    est = null; tr = null; dentroDeEscuta = false;
                    continue;

                case RiveKeys.LayerState:
                case RiveKeys.AdvanceableState:
                case RiveKeys.AnimationState:
                case RiveKeys.AnyState:
                case RiveKeys.EntryState:
                case RiveKeys.ExitState:
                case RiveKeys.BlendState:
                case RiveKeys.BlendStateDirect:
                case RiveKeys.BlendState1DInput:
                case RiveKeys.BlendState1D:
                case RiveKeys.BlendState1DViewModel:
                {
                    if (cam is null) continue;
                    est = new RiveEstado
                    {
                        Nome = o.S(RiveKeys.SmNameKey),
                        TipoBruto = o.TypeKey,
                        Flags = o.U(RiveKeys.LayerStateFlagsKey),
                        Genero = o.TypeKey switch
                        {
                            RiveKeys.AnyState => "qualquer",
                            RiveKeys.EntryState => "entrada",
                            RiveKeys.ExitState => "saida",
                            RiveKeys.AnimationState => "animacao",
                            RiveKeys.BlendState or RiveKeys.BlendStateDirect or RiveKeys.BlendState1DInput
                                or RiveKeys.BlendState1D or RiveKeys.BlendState1DViewModel => "mistura",
                            _ => "outro",
                        },
                    };
                    // Um AnimationState sem animationId é legítimo (estado que não toca nada) —
                    // por isso testa-se Has() em vez de assumir 0, que seria a 1ª animação.
                    if (o.TypeKey == RiveKeys.AnimationState && o.Has(RiveKeys.AnimationStateAnimIdKey))
                        est.AnimacaoIndice = (int)o.U(RiveKeys.AnimationStateAnimIdKey);

                    if (o.TypeKey == RiveKeys.AnyState) cam.IdxQualquer = cam.Estados.Count;
                    else if (o.TypeKey == RiveKeys.EntryState) cam.IdxEntrada = cam.Estados.Count;
                    else if (o.TypeKey == RiveKeys.ExitState) cam.IdxSaida = cam.Estados.Count;

                    cam.Estados.Add(est);
                    tr = null; dentroDeEscuta = false;
                    continue;
                }

                case RiveKeys.BlendAnimation1D:
                case RiveKeys.BlendAnimationDirect:
                    // Não misturamos: guardamos a ordem para poder tocar pelo menos a primeira.
                    if (est is not null && o.Has(RiveKeys.BlendAnimIdKey))
                        est.Mistura.Add((int)o.U(RiveKeys.BlendAnimIdKey));
                    continue;

                case RiveKeys.StateTransition:
                case RiveKeys.BlendStateTransition:
                    if (est is null) continue;
                    tr = new RiveTransicao
                    {
                        ParaEstado = o.Has(RiveKeys.TrStateToIdKey) ? (int)o.U(RiveKeys.TrStateToIdKey) : -1,
                        Flags = o.U(RiveKeys.TrFlagsKey),
                        Duracao = o.U(RiveKeys.TrDurationKey),
                        TempoSaida = o.U(RiveKeys.TrExitTimeKey),
                        PesoAleatorio = o.U(RiveKeys.TrRandomWeightKey, 1),
                    };
                    est.Transicoes.Add(tr);
                    dentroDeEscuta = false;
                    continue;

                case RiveKeys.TransitionTriggerCondition:
                case RiveKeys.TransitionNumberCondition:
                case RiveKeys.TransitionBoolCondition:
                    if (tr is null || dentroDeEscuta) continue;
                    tr.Condicoes.Add(new RiveCondicao
                    {
                        Gene = o.TypeKey,
                        InputIndice = o.Has(RiveKeys.TrInputIdKey) ? (int)o.U(RiveKeys.TrInputIdKey) : -1,
                        Op = (int)o.U(RiveKeys.TrOpValueKey),
                        Valor = o.D(RiveKeys.TrNumberValueKey),
                    });
                    continue;

                default:
                    continue;
            }
        }

        FecharArtboard();

        // Ficheiros antigos exportam a máquina sem nome. A UI tem de mostrar alguma coisa,
        // por isso damos um número em vez de uma linha vazia no combo.
        for (int i = 0; i < saida.Count; i++)
            if (string.IsNullOrWhiteSpace(saida[i].Nome)) saida[i].Nome = "Máquina " + (i + 1);

        foreach (var m in saida) m.Reiniciar();
        return saida;
    }
}
