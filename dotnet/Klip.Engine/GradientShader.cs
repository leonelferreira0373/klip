using System;
using Klip.Model;
using SkiaSharp;

namespace Klip.Engine;

/// <summary>
/// Constrói o SKShader de um <see cref="GradientSpec"/> multi-stop.
/// STUB — corpo implementado a seguir. Devolver null faz o Renderer cair no caminho de sempre.
/// </summary>
public static class GradientShader
{
    /// <param name="b">caixa da forma em coordenadas de desenho</param>
    /// <param name="t">tempo, para avaliar os tracks animáveis</param>
    /// <param name="alpha">alpha da camada, já resolvido</param>
    public static SKShader? Build(GradientSpec g, SKRect b, double t, byte alpha) => null;
}
