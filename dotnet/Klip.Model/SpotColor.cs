namespace Klip.Model;

/// <summary>
/// Etiqueta de cor SPOT (PANTONE, HKS, TOYO, DIC…) associada a uma cor do documento.
///
/// É METADADO: o render continua a usar o uint/ColorTrack normal. Guardar isto aqui é o que
/// permite manter o NOME e o CMYK da cor através de gravar/abrir, exportar e editar — sem o qual
/// uma cor de marca perde a identidade assim que entra no ficheiro.
/// </summary>
public sealed record SpotRef(
    string Code,                  // ex.: "PANTONE 185 C"
    string Library = "",          // livro de onde veio, ex.: "PANTONE+ Solid Coated-V5"
    uint Argb = 0,                // sRGB que foi efectivamente aplicado
    double C = 0, double M = 0, double Y = 0, double K = 0,   // chapa — é isto que vai para a gráfica
    bool HasCmyk = false,
    double L = 0, double A = 0, double B = 0);                // Lab, para comparar cores por ΔE
