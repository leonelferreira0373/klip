using System;
using Avalonia.Controls;

namespace Klip.App;

/// <summary>
/// Aba COR: editor de gradiente multi-stop + seletor de cor rico (matiz/saturação, hex, paletas).
/// STUB — corpos implementados a seguir. Assinaturas congeladas.
/// </summary>
public partial class MainWindow : Window
{
    private bool _pcBuilt;

    private void BuildColorPanel() { }
    private void SyncColorPanel() { }

    /// <summary>Seletor de cor rico, usado pelo ColorButton da barra contextual.</summary>
    internal Control ColorFlyout(uint argb, Action<uint> pick) => new Panel();
}
