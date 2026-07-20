using System;
using Avalonia.Controls;
using Klip.Model;

namespace Klip.App;

/// <summary>
/// Cores SPOT (PANTONE, HKS, TOYO, DIC, FOCOLTONE, TRUMATCH) — bus + UI.
/// STUB — corpos implementados a seguir. Assinaturas congeladas.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>Aplica uma cor spot pelo código, no fill ou no stroke.</summary>
    public object ApiSetSpot(string id, string code, string? target) => new { ok = true };

    /// <summary>Cores spot mais próximas de um hex, por ΔE.</summary>
    public object ApiFindSpot(string hex, int n, string? library) => new { ok = true };

    /// <summary>Procura por código/nome nos livros disponíveis.</summary>
    public object ApiListSpot(string? filter, int limit, string? library) => new { ok = true };

    /// <summary>Que livros de cor existem nesta máquina.</summary>
    public object ApiListPalettes() => new { ok = true };

    /// <summary>Lista escolhível de cores spot, para o seletor de cor.</summary>
    internal Control SpotFlyout(Action<uint, SpotRef> pick) => new Panel();
}
