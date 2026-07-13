using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Klip.Tests.Framework;

// ============================================================================
// KLIP E2E TEST RUNNER — headless console. Descobre por reflexão todos os
// métodos [KlipTest], corre-os, imprime relatório por FASE e devolve exit code
// (0 = tudo verde ; 1 = houve FAIL). PENDING não falha o build.
//
//   dotnet run --project Klip.Tests                 # corre tudo
//   dotnet run --project Klip.Tests -- --phase 1    # só a Fase 1
//   dotnet run --project Klip.Tests -- --list       # lista sem correr
// ============================================================================

int? phaseFilter = null;
bool listOnly = false;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--phase" && i + 1 < args.Length && int.TryParse(args[i + 1], out var p)) phaseFilter = p;
    else if (args[i] == "--list") listOnly = true;
}

// Toca o motor (força o ModuleInitializer do ExprEngine → Track.CodeEval ligado)
_ = new Klip.Engine.Renderer();

var tests = Assembly.GetExecutingAssembly()
    .GetTypes()
    .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
    .Select(m => (Method: m, Attr: m.GetCustomAttribute<KlipTestAttribute>()))
    .Where(x => x.Attr is not null)
    .Where(x => x.Method.GetParameters().Length == 0)
    .Where(x => phaseFilter is null || x.Attr!.Phase == phaseFilter)
    .OrderBy(x => x.Attr!.Phase).ThenBy(x => x.Attr!.Name)
    .ToList();

Console.WriteLine();
Console.WriteLine($"KLIP E2E — {tests.Count} teste(s){(phaseFilter is { } pf ? $" (Fase {pf})" : "")}");
Console.WriteLine(new string('=', 60));

int pass = 0, fail = 0, pending = 0;
var failures = new List<string>();
int? lastPhase = null;

foreach (var (method, attr) in tests)
{
    if (lastPhase != attr!.Phase)
    {
        lastPhase = attr.Phase;
        Console.WriteLine();
        Console.WriteLine($"── FASE {attr.Phase} " + new string('─', 48));
    }

    if (listOnly)
    {
        Console.WriteLine($"   ·  {attr.Name}{(attr.Criterion is { } cr ? $"  — {cr}" : "")}");
        continue;
    }

    var sw = Stopwatch.StartNew();
    string status;
    ConsoleColor color;
    try
    {
        method.Invoke(null, null);
        sw.Stop();
        status = "PASS"; color = ConsoleColor.Green; pass++;
    }
    catch (TargetInvocationException tie) when (tie.InnerException is PendingException pe)
    {
        sw.Stop();
        status = "PEND"; color = ConsoleColor.Yellow; pending++;
        Write(color, $"  [{status}] {attr.Name}  ({sw.ElapsedMilliseconds} ms)");
        Console.WriteLine($"         ↳ {pe.Message}");
        continue;
    }
    catch (TargetInvocationException tie)
    {
        sw.Stop();
        var ex = tie.InnerException ?? tie;
        status = "FAIL"; color = ConsoleColor.Red; fail++;
        failures.Add($"Fase {attr.Phase} :: {attr.Name} → {ex.Message}");
        Write(color, $"  [{status}] {attr.Name}  ({sw.ElapsedMilliseconds} ms)");
        Console.WriteLine($"         ↳ {ex.Message}");
        continue;
    }
    catch (Exception ex)
    {
        sw.Stop();
        status = "FAIL"; color = ConsoleColor.Red; fail++;
        failures.Add($"Fase {attr.Phase} :: {attr.Name} → {ex.Message}");
        Write(color, $"  [{status}] {attr.Name}  ({sw.ElapsedMilliseconds} ms)");
        Console.WriteLine($"         ↳ {ex.Message}");
        continue;
    }

    Write(color, $"  [{status}] {attr.Name}  ({sw.ElapsedMilliseconds} ms)");
}

if (listOnly) return 0;

Console.WriteLine();
Console.WriteLine(new string('=', 60));
Write(fail == 0 ? ConsoleColor.Green : ConsoleColor.Red,
    $"RESULTADO: {pass} PASS · {fail} FAIL · {pending} PENDING");
if (failures.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("FALHAS:");
    foreach (var f in failures) Console.WriteLine("  ✗ " + f);
}
Console.WriteLine();

return fail == 0 ? 0 : 1;

static void Write(ConsoleColor c, string s)
{
    var prev = Console.ForegroundColor;
    Console.ForegroundColor = c;
    Console.WriteLine(s);
    Console.ForegroundColor = prev;
}
