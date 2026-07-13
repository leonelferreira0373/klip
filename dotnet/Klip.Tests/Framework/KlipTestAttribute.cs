using System;

namespace Klip.Tests.Framework;

/// <summary>
/// Marca um método de teste E2E. O runner descobre por reflexão TODOS os
/// métodos <c>public static void</c> (sem parâmetros) anotados com isto.
/// Agrupa por <see cref="Phase"/> para acumular testes fase-a-fase.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class KlipTestAttribute : Attribute
{
    public KlipTestAttribute(int phase, string name)
    {
        Phase = phase;
        Name = name;
    }

    /// <summary>Fase do roadmap a que o teste pertence (0 = self-tests do harness).</summary>
    public int Phase { get; }

    /// <summary>Nome legível do caso de teste.</summary>
    public string Name { get; }

    /// <summary>Objetivo/critério de aceitação em uma linha (aparece no relatório).</summary>
    public string? Criterion { get; init; }
}
