namespace Klip.App;

/// <summary>
/// §BETA — estado da PRIMEIRA utilização: o tutorial de 3 cards e o dot de descoberta no ◆.
/// Persistido em %APPDATA%\Klip\ai.json como "1"/"0" (mesmo padrão do Telemetry.OptIn).
/// Chave em falta → null != "1" → false → é a primeira vez. Não precisa de migração.
/// </summary>
public static class Onboarding
{
    /// <summary>Marcado ao MOSTRAR (não ao fechar): se a app morrer a meio, não volta a chatear.</summary>
    public static bool HasSeenTutorial
    {
        get => Ai.AiConfig.GetProfile("has_seen_tutorial") == "1";
        set => Ai.AiConfig.SetProfile("has_seen_tutorial", value ? "1" : "0");
    }

    /// <summary>Já abriu o painel de créditos alguma vez → o dot desaparece para sempre.</summary>
    public static bool HasSeenPayments
    {
        get => Ai.AiConfig.GetProfile("has_seen_payments") == "1";
        set => Ai.AiConfig.SetProfile("has_seen_payments", value ? "1" : "0");
    }
}
