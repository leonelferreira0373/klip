using System;
using Avalonia.Controls;
using Klip.Model;

namespace Klip.App;

/// <summary>
/// Bus do gradiente multi-stop: é por aqui que a IA controla a cor AO MILÍMETRO.
/// STUB — corpos implementados a seguir. Assinaturas congeladas.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>Substitui o gradiente inteiro. stops = "#RRGGBB@0, #RRGGBB@0.35, #RRGGBB@1".</summary>
    public object ApiSetGradient(string id, string stops, string? kind,
                                 double? angle, double? cx, double? cy, double? radius, string? tile)
        => new { ok = true };

    /// <summary>Lê o gradiente completo em t — para a IA trabalhar por deltas em vez de reescrever tudo.</summary>
    public object ApiGetGradient(string id, double t) => new { ok = true };

    /// <summary>Mexe numa só paragem, sem tocar nas outras nem na geometria.</summary>
    public object ApiSetStop(string id, int index, string? color, double? pos) => new { ok = true };

    /// <summary>Insere uma paragem nova (máx 8).</summary>
    public object ApiAddStop(string id, string color, double pos) => new { ok = true };

    /// <summary>Remove uma paragem (mínimo 2).</summary>
    public object ApiRemoveStop(string id, int index) => new { ok = true };
}
