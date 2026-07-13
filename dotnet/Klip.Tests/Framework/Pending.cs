using System;

namespace Klip.Tests.Framework;

/// <summary>
/// Sinaliza um teste de CONTRATO ainda não ativável (a feature-alvo não aterrou).
/// O runner reporta como PENDING (amarelo), NÃO como falha — assim o teste de
/// aceitação vive no repo desde já e "acende" (PASS/FAIL) no dia em que a API existe,
/// sem nunca dar falso-vermelho no CI entretanto.
/// </summary>
public sealed class PendingException : Exception
{
    public PendingException(string reason) : base(reason) { }
}
