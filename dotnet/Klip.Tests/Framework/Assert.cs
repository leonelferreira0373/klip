using System;

namespace Klip.Tests.Framework;

/// <summary>Falha de asserção — capturada pelo runner como FAIL (não rebenta o processo).</summary>
public sealed class AssertException : Exception
{
    public AssertException(string message) : base(message) { }
}

/// <summary>Asserções OBJETIVAS mínimas (sem dependências externas). Mensagens ricas p/ debug.</summary>
public static class Assert
{
    public static void True(bool cond, string because)
    {
        if (!cond) throw new AssertException($"esperava VERDADEIRO: {because}");
    }

    public static void False(bool cond, string because)
    {
        if (cond) throw new AssertException($"esperava FALSO: {because}");
    }

    public static void Equal(int expected, int actual, string what)
    {
        if (expected != actual)
            throw new AssertException($"{what}: esperava {expected}, obtive {actual}");
    }

    /// <summary>Igualdade numérica com tolerância absoluta.</summary>
    public static void Near(double expected, double actual, double tol, string what)
    {
        if (double.IsNaN(actual) || Math.Abs(expected - actual) > tol)
            throw new AssertException(
                $"{what}: esperava {expected:0.###} ±{tol:0.###}, obtive {actual:0.###} (Δ={Math.Abs(expected - actual):0.###})");
    }

    /// <summary>Valor dentro de [lo, hi] inclusive.</summary>
    public static void InRange(double lo, double hi, double actual, string what)
    {
        if (double.IsNaN(actual) || actual < lo || actual > hi)
            throw new AssertException($"{what}: esperava ∈ [{lo:0.###}, {hi:0.###}], obtive {actual:0.###}");
    }

    public static void Less(double a, double b, string what)
    {
        if (!(a < b)) throw new AssertException($"{what}: esperava {a:0.###} < {b:0.###}");
    }

    public static void Greater(double a, double b, string what)
    {
        if (!(a > b)) throw new AssertException($"{what}: esperava {a:0.###} > {b:0.###}");
    }

    public static void Fail(string message) => throw new AssertException(message);
}
