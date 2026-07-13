using System;
using Microsoft.Toolkit.Uwp.Notifications;

namespace Klip.Admin.Native;

/// <summary>Notificações do Windows. Toast nativo (auto-regista AUMID) com fallback in-app se falhar.</summary>
public static class Toasts
{
    /// <summary>Ligado pela MainWindow: toast in-app + flash da taskbar se o toast do SO falhar.</summary>
    public static Action<string, string>? Fallback;

    public static void Show(string title, string body)
    {
        try { new ToastContentBuilder().AddText(title).AddText(body).Show(); }
        catch { try { Fallback?.Invoke(title, body); } catch { } }
    }
}
