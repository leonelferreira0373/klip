using System.Collections.Generic;
using Klip.Engine;
using Klip.Model;
using Klip.Tests.Framework;

namespace Klip.Tests.Phase4_Stagger;

/// <summary>Fase 4 — stagger: a mesma animação a N camadas com offset temporal. Prova que cada camada
/// arranca desfasada (via keyframes semeados em delay*i pelo sistema uniforme da Fase 1).</summary>
public static class StaggerTests
{
    [KlipTest(4, "stagger: offset temporal desfasa as camadas (l0 arranca, l1/l2 ainda não)")]
    public static void StaggerOffsetsTiming()
    {
        double dur = 0.3, off = 0.2;
        var layers = new List<Layer>();
        for (int i = 0; i < 3; i++)
        {
            var l = new Layer($"l{i}", MorphTrack.Static(Shapes.Circle(10)), 0xFF000000);
            double delay = i * off;
            l = PropRegistry.AddKeyframe(l, "opacity", delay, PropValue.Of(0.0));
            l = PropRegistry.AddKeyframe(l, "opacity", delay + dur, PropValue.Of(1.0));
            layers.Add(l);
        }
        // t=0.15: l0 a meio (~0.5); l1 (delay .2) e l2 (delay .4) ainda seguram o 1º kf (=0)
        Assert.Greater(layers[0].Opacity!.Eval(0.15), 0.3, "l0 já começou a animar");
        Assert.Near(0.0, layers[1].Opacity!.Eval(0.15), 1e-6, "l1 ainda não começou (delay 0.2)");
        Assert.Near(0.0, layers[2].Opacity!.Eval(0.15), 1e-6, "l2 ainda não começou (delay 0.4)");
        // t=0.35: l1 já começou; l2 ainda não
        Assert.Greater(layers[1].Opacity!.Eval(0.35), 0.3, "l1 já começou em t=0.35");
        Assert.Near(0.0, layers[2].Opacity!.Eval(0.35), 1e-6, "l2 ainda não (delay 0.4)");
    }
}
