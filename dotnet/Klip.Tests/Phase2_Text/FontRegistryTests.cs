using System;
using Klip.Engine;
using Klip.Tests.Framework;

namespace Klip.Tests.Phase2_Text;

/// <summary>Fase 2 — sistema de fontes. Prova que o Resolve nunca rebenta (fallback) e que a IA
/// consegue BAIXAR uma fonte do Google Fonts por nome e usá-la (outlines distintos), tolerante a offline.</summary>
public static class FontRegistryTests
{
    [KlipTest(2, "fontes: Resolve nunca lança e faz fallback (sistema → Default)")]
    public static void ResolveNeverThrows()
    {
        Assert.True(FontRegistry.Shared.Resolve("Segoe UI", true) != null, "família de sistema resolve");
        Assert.True(FontRegistry.Shared.Resolve("fonte-inexistente-xyz-123", true) != null, "família falsa → fallback (não lança, não null)");
        Assert.True(FontRegistry.Shared.Resolve("", true) != null, "vazio → Default");
        Assert.True(FontRegistry.Shared.Resolve(null, true) != null, "null → Default");
    }

    [KlipTest(2, "fontes: baixar do Google Fonts por nome e usar (tolerante a offline)")]
    public static void DownloadGoogleFont()
    {
        FontResult r;
        try { r = FontRegistry.Shared.LoadAsync("Bebas Neue").GetAwaiter().GetResult(); }
        catch (Exception e) { throw new PendingException("sem rede / Google Fonts indisponível: " + e.Message); }

        Assert.True(!string.IsNullOrEmpty(r.Family), "family devolvida");
        // a fonte baixada chega mesmo ao TextPathD → outlines diferentes de Segoe UI para o mesmo texto
        var withBebas = TextShape.TextPathD("KLIP", 100, r.Family);
        var withSegoe = TextShape.TextPathD("KLIP", 100, "Segoe UI");
        Assert.True(withBebas != null && withBebas != withSegoe, "Bebas Neue produz outlines diferentes de Segoe UI");
        // idempotência: a 2ª carga vem da cache (sem rede)
        var r2 = FontRegistry.Shared.LoadAsync("Bebas Neue").GetAwaiter().GetResult();
        Assert.True(r2.FromCache, "2ª load_font vem da cache");
    }
}
