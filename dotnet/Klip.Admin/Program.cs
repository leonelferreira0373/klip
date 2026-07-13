using Avalonia;
using System;

namespace Klip.Admin;

class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // O mesmo exe é a ponte MCP (--mcp-stdio) para qualquer cliente MCP conduzir o admin.
        if (Array.Exists(args, a => a == "--mcp-stdio"))
            return Ai.AdminMcpStdioBridge.Run();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
