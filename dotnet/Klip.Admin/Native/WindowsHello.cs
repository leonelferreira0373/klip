using System;
using System.Threading.Tasks;
using Windows.Security.Credentials.UI;

namespace Klip.Admin.Native;

/// <summary>Windows Hello via WinRT UserConsentVerifier (PIN/impressão/rosto).</summary>
public static class WindowsHello
{
    public static async Task<bool> IsAvailableAsync()
    {
        try { return (await UserConsentVerifier.CheckAvailabilityAsync()) == UserConsentVerifierAvailability.Available; }
        catch { return false; }
    }

    public static async Task<bool> VerifyAsync(string message)
    {
        try { return (await UserConsentVerifier.RequestVerificationAsync(message)) == UserConsentVerificationResult.Verified; }
        catch { return false; }
    }
}
