using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SkiaSharp;

namespace Klip.Engine.Rive;

/// <summary>Cached facade: load a .riv once, render an animated frame at time t on demand.</summary>
public static class RiveClip
{
    private static readonly ConcurrentDictionary<string, RiveDocument?> _cache = new();

    private static RiveDocument? Doc(string path) => _cache.GetOrAdd(path, p =>
    {
        try { return RiveLoader.Load(File.ReadAllBytes(p)); }
        catch { return null; }
    });

    /// <summary>Draw the .riv's animation at time t (seconds) into dst. Returns false if unloadable.</summary>
    public static bool Draw(SKCanvas canvas, SKRect dst, string path, string? animName, double t)
        => Draw(canvas, dst, path, animName, t, null, null);

    // Máquinas já lidas, por ficheiro. Ler o .riv a cada fotograma seria absurdo.
    private static readonly ConcurrentDictionary<string, IReadOnlyList<RiveMachine>> _maqs = new();
    // Uma tranca por ficheiro: o Avancar MUTA a máquina, e o preview (thread da UI) e o export
    // (workers) desenham ao mesmo tempo. Sem isto, duas simulações pisavam-se.
    private static readonly ConcurrentDictionary<string, object> _trancas = new();

    /// <summary>Passo fixo da simulação. Fixo de propósito — ver o comentário do determinismo.</summary>
    private const double Passo = 1.0 / 60.0;

    /// <summary>
    /// Desenha o .riv em t, opcionalmente conduzido por uma MÁQUINA DE ESTADOS.
    ///
    /// O PROBLEMA: o KLIP é uma linha temporal — desenha o instante t, deixa arrastar para trás e
    /// exporta fora de ordem. Uma máquina de estados é o contrário: tem memória e reage a eventos.
    /// Se guardássemos o estado entre chamadas, arrastar para trás dava um resultado diferente de
    /// avançar até ao mesmo sítio, e o vídeo exportado não seria igual ao que se viu.
    ///
    /// A SOLUÇÃO: reiniciar e simular SEMPRE de 0 até t, em passos fixos. O mesmo t dá sempre o
    /// mesmo fotograma, venha do scrub, do preview ou do export. Custa t×60 passos de lógica pura
    /// (sem desenho), que é barato — e é o preço de a linha temporal não mentir.
    /// </summary>
    /// <param name="machineName">null = tocar a animação linear como antes.</param>
    /// <param name="inputs">valores dos inputs da máquina; gatilhos disparam com valor != 0.</param>
    public static bool Draw(SKCanvas canvas, SKRect dst, string path, string? animName, double t,
                            string? machineName, IReadOnlyDictionary<string, double>? inputs)
    {
        var doc = Doc(path);
        var ab = doc?.First;
        if (ab is null) return false;

        var player = new RivePlayer(ab);
        RiveAnimation? anim = null;
        double tempoAnim = t;

        if (!string.IsNullOrWhiteSpace(machineName))
        {
            var lista = _maqs.GetOrAdd(path, p =>
            {
                try { return RiveStateMachine.Ler(p); }
                catch { return Array.Empty<RiveMachine>(); }
            });
            var maq = lista.FirstOrDefault(m => string.Equals(m.Nome, machineName, StringComparison.OrdinalIgnoreCase));
            if (maq is not null)
            {
                var tranca = _trancas.GetOrAdd(path, _ => new object());
                lock (tranca)
                {
                    maq.Reiniciar();
                    if (inputs is not null)
                        foreach (var (nome, v) in inputs)
                        {
                            // um gatilho não tem valor: ou dispara ou não. Tratá-lo como número
                            // deixava-o armado para sempre e a máquina transitava em ciclo.
                            var inp = maq.Inputs.FirstOrDefault(i => i.Nome == nome);
                            if (inp is not null && inp.Tipo == "gatilho") { if (v != 0) maq.Disparar(nome); }
                            else maq.Definir(nome, v);
                        }

                    RiveQuadro? ultimo = null;
                    double restante = Math.Max(0, t);
                    int guarda = 0;
                    while (restante > 1e-9 && guarda++ < 20000)
                    {
                        double dt = Math.Min(Passo, restante);
                        ultimo = maq.Avancar(dt);
                        restante -= dt;
                    }
                    ultimo ??= maq.Avancar(0);

                    if (ultimo.Animacao is { Length: > 0 } nomeAnim)
                    {
                        anim = player.Find(nomeAnim);
                        tempoAnim = ultimo.Tempo;
                    }
                }
            }
        }

        anim ??= player.Find(animName);
        if (anim is not null) player.Apply(anim, tempoAnim);
        new RiveRenderer(ab).Render(canvas, dst);
        return true;
    }

    public static (double w, double h, string[] anims)? Info(string path)
    {
        var ab = Doc(path)?.First;
        if (ab is null) return null;
        var names = new string[ab.Animations.Count];
        for (int i = 0; i < names.Length; i++) names[i] = ab.Animations[i].Name;
        return (ab.Width, ab.Height, names);
    }
}
