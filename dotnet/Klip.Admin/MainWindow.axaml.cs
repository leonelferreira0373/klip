using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Klip.Admin.Ai;
using Klip.Admin.Feed;
using Klip.Admin.Native;

namespace Klip.Admin;

public partial class MainWindow : Window
{
    private readonly AnthropicAdminBackend _ai = new();
    private readonly AdminControlServer _bus = new();
    private bool _started;

    public MainWindow()
    {
        InitializeComponent();
        Web.NavigationCompleted += OnNavCompleted;
        Web.WebMessageReceived += OnWebMessage;
        Opened += OnOpened;

        _ai.Emit = (obj) => Dispatcher.UIThread.Post(async () =>
        {
            try { await Web.InvokeScript($"window.klipOnAiEvent && window.klipOnAiEvent({obj.ToJsonString()})"); } catch { }
        });

        // ferramentas de IA que conduzem a UI: invocam window.KlipDash no WebView
        AdminActionRegistry.UiDriver = RunUiAsync;

        // toast falhou → traz a janela à frente
        Toasts.Fallback = (t, b) => Dispatcher.UIThread.Post(() => { try { Activate(); Topmost = true; Topmost = false; } catch { } });

        FeedWatcher.OnNewEvents = () => Dispatcher.UIThread.Post(async () =>
        { try { await Web.InvokeScript("window.KlipDash && window.KlipDash.refresh && window.KlipDash.refresh()"); } catch { } });
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (_started) return; _started = true;
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Klip.Admin", "web");
            Directory.CreateDirectory(dir);
            var htmlPath = Path.Combine(dir, "index.html");
            var asm = typeof(MainWindow).Assembly;
            using (var s = asm.GetManifestResourceStream("dashboard.html"))
            {
                if (s != null) { using var fs = File.Create(htmlPath); s.CopyTo(fs); }
                else File.WriteAllText(htmlPath, "<h1>dashboard.html em falta no bundle</h1>");
            }
            Web.Source = new Uri(new Uri(htmlPath).AbsoluteUri + "?shell=windows");
        }
        catch (Exception ex) { Debug.WriteLine("boot: " + ex); }

        _bus.Start();
        FeedWatcher.Start();
    }

    private async void OnNavCompleted(object? sender, Avalonia.Controls.WebViewNavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess) return;
        try { await Web.InvokeScript(BridgeJs); } catch { }
    }

    private async void OnWebMessage(object? sender, Avalonia.Controls.WebMessageReceivedEventArgs e)
    {
        int id = 0;
        try
        {
            var doc = JsonDocument.Parse(e.Body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("__klip", out _)) return;
            id = root.TryGetProperty("id", out var idEl) ? idEl.GetInt32() : 0;
            var method = root.TryGetProperty("method", out var mEl) ? mEl.GetString() ?? "" : "";
            var args = root.TryGetProperty("args", out var aEl) ? aEl : default;
            string A(string k) => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

            switch (method)
            {
                case "biometric":
                    {
                        bool ok = await WindowsHello.VerifyAsync("Entrar no KLIP Administrador");
                        await Resolve(id, true, new { ok, method = ok ? "hello" : "cancelled" });
                        break;
                    }
                case "notify": Toasts.Show(A("title"), A("body")); await Resolve(id, true, null); break;
                case "openExternal":
                    { var u = A("url"); if (Uri.TryCreate(u, UriKind.Absolute, out _)) { try { Process.Start(new ProcessStartInfo(u) { UseShellExecute = true }); } catch { } } await Resolve(id, true, null); break; }
                case "storage.get": await ResolveRaw(id, JsonSerializer.Serialize(AdminAiConfig.Get(A("key")))); break;
                case "storage.set": AdminAiConfig.Set(A("key"), A("value")); await Resolve(id, true, null); break;
                case "storage.remove": AdminAiConfig.Remove(A("key")); await Resolve(id, true, null); break;
                case "aiSend": _ = _ai.SendAsync(A("text")); await Resolve(id, true, null); break;
                case "aiCancel": _ai.Cancel(); await Resolve(id, true, null); break;
                case "aiConfirm":
                    { var cid = A("confirmId"); bool approve = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("approve", out var ap) && ap.ValueKind == JsonValueKind.True; _ai.Confirm(cid, approve); await Resolve(id, true, null); break; }
                case "aiHistory": await ResolveRaw(id, "[]"); break;
                default: await Resolve(id, false, new { error = "método desconhecido: " + method }); break;
            }
        }
        catch (Exception ex) { try { await Resolve(id, false, new { error = ex.Message }); } catch { } }
    }

    private async Task<string?> RunUiAsync(string method, string argsJson)
    {
        try
        {
            return await Dispatcher.UIThread.InvokeAsync(() =>
                Web.InvokeScript($"JSON.stringify((window.KlipDash && window.KlipDash.{method}) ? (window.KlipDash.{method}({argsJson}) ?? null) : null)"));
        }
        catch { return null; }
    }

    private Task Resolve(int id, bool ok, object? value) => ResolveRaw(id, JsonSerializer.Serialize(value));
    private async Task ResolveRaw(int id, string jsonValue)
    {
        if (id <= 0) return;
        try { await Web.InvokeScript($"window.__klipResolve && window.__klipResolve({id}, true, {jsonValue})"); } catch { }
    }

    // injectado após cada navegação: define window.KlipAdmin (ponte nativa)
    private const string BridgeJs = @"(function(){
  if(window.KlipAdmin) return;
  var P={}, ID=0;
  function call(method,args){ return new Promise(function(res,rej){ var id=++ID; P[id]={res:res,rej:rej};
    window.chrome.webview.postMessage(JSON.stringify({__klip:1,id:id,method:method,args:args||{}})); }); }
  window.__klipResolve=function(id,ok,val){ var p=P[id]; if(!p)return; delete P[id]; ok?p.res(val):p.rej(val); };
  window.KlipAdmin={
    platform:'windows',
    biometric:function(){ return call('biometric'); },
    notify:function(t,b){ return call('notify',{title:t,body:b}); },
    openExternal:function(u){ return call('openExternal',{url:u}); },
    aiSend:function(text,opts){ return call('aiSend',{text:text}); },
    aiCancel:function(){ return call('aiCancel'); },
    aiHistory:function(){ return call('aiHistory'); },
    aiConfirm:function(id,ok){ return call('aiConfirm',{confirmId:id,approve:!!ok}); },
    storage:{ get:function(k){ return call('storage.get',{key:k}); },
              set:function(k,v){ return call('storage.set',{key:k,value:String(v)}); },
              remove:function(k){ return call('storage.remove',{key:k}); } }
  };
})();";
}
