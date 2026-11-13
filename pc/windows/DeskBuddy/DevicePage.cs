using System.Diagnostics;
using System.ComponentModel;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DeskBuddy;

/// Pagina de setări de pe placă (http://<ip>/), afișată în fereastră cu WebView2: aceeași pagină ca în browser,
/// deci orice setare nouă din firmware apare și aici fără să schimbi aplicația.
/// Navigarea e limitată la placă; orice alt link se deschide în browserul tău.
sealed class DevicePage : Panel
{
    readonly TrayApp app;
    readonly Label address = Theme.Label("", Theme.Small, Theme.Muted);
    readonly Panel overlay = new() { Dock = DockStyle.Fill, BackColor = Theme.Bg };
    readonly Label overlayText = Theme.Label("", Theme.Body, Theme.Muted);
    readonly Button browserButton = Theme.Button("Deschide în browser");
    WebView2? web;
    Task? init;
    string? loaded;

    public DevicePage(TrayApp app)
    {
        this.app = app;
        Dock = DockStyle.Fill;
        BackColor = Theme.Bg;

        var reload = Theme.Button("Reîncarcă");
        reload.Click += (_, _) => { loaded = null; Navigate(); };
        var browser = browserButton;
        int btnH = reload.GetPreferredSize(Size.Empty).Height;
        var bar = new Panel { Dock = DockStyle.Top, Height = btnH + 16, BackColor = Theme.Nav, Padding = new Padding(16, 0, 8, 0) };
        address.Dock = DockStyle.Left;
        address.TextAlign = ContentAlignment.MiddleLeft;
        address.AutoSize = false;
        address.Width = 420;
        address.Margin = Padding.Empty;
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Nav, Padding = new Padding(0, 4, 0, 0) };
        browser.Click += (_, _) => { if (app.DeviceUrl is { } u) OpenExternal(u); };
        buttons.Controls.AddRange([reload, browser]);
        bar.Controls.Add(address);
        bar.Controls.Add(buttons);

        overlayText.AutoSize = false;
        overlayText.Dock = DockStyle.Fill;
        overlayText.TextAlign = ContentAlignment.MiddleCenter;
        overlay.Controls.Add(overlayText);

        Controls.Add(overlay);                        // Fill întâi (ultimul la Dock), apoi bara de sus
        Controls.Add(bar);
        ShowMessage("Se încarcă...");
    }

    /// pagina a devenit vizibilă
    public async void Activate()
    {
        init ??= InitAsync();
        await init;
        Navigate();
    }

    /// pagina plăcii: prin WiFi dacă o găsim în rețea, altfel prin cablul USB (aceeași pagină, servită de aplicație)
    string? TargetUrl => app.DeviceUrl ?? (app.UsbBoard != null ? TrayApp.UsbPageUrl : null);

    /// s-a schimbat IP-ul plăcii (găsită / pierdută / setată de mână) sau a apărut pe USB
    public void DeviceChanged()
    {
        if (web?.CoreWebView2 == null) return;
        // pe USB, placa dispare câteva secunde când repornește (după salvare): pagina se reîncarcă singură, nu o mutăm
        if (loaded == TrayApp.UsbPageUrl && TargetUrl == null) return;
        if (loaded != TargetUrl) Navigate();
    }

    async Task InitAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(AppSettings.Dir, "WebView2"));
            web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Theme.Bg };
            Controls.Add(web);
            Controls.SetChildIndex(web, 0);           // Fill, sub bara de sus
            await web.EnsureCoreWebView2Async(env);
            var core = web.CoreWebView2;
            var s = core.Settings;
            s.AreDevToolsEnabled = false;
            s.AreHostObjectsAllowed = false;
            s.IsWebMessageEnabled = false;
            s.IsStatusBarEnabled = false;
            core.NavigationStarting += (_, e) =>
            {
                if (!IsDeviceUrl(e.Uri)) { e.Cancel = true; OpenExternal(e.Uri); }
            };
            core.NewWindowRequested += (_, e) => { e.Handled = true; OpenExternal(e.Uri); };
            core.AddWebResourceRequestedFilter(TrayApp.UsbPageUrl + "*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += OnUsbRequest;
            core.NavigationCompleted += (_, e) =>
            {
                if (e.IsSuccess) HideMessage();
                else if (e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
                    ShowMessage($"Nu pot deschide pagina plăcii ({e.WebErrorStatus}).\nE DeskBuddy pornit și în aceeași rețea?");
            };
        }
        catch (Exception e)
        {
            web = null;
            ShowMessage("Nu pot afișa pagina în fereastră: " + e.Message + "\nFolosește „Deschide în browser”.");
        }
    }

    void Navigate()
    {
        string? url = TargetUrl;
        bool viaUsb = url == TrayApp.UsbPageUrl;
        address.Text = url == null ? "DeskBuddy negăsit în rețea sau pe USB" : viaUsb ? $"prin USB ({app.UsbBoard?.Port}), fără WiFi" : url;
        browserButton.Enabled = !viaUsb && url != null;
        if (web?.CoreWebView2 == null) return;
        if (url == null)
        {
            loaded = null;
            ShowMessage("Caut DeskBuddy în rețea (deskbuddy.local) și pe USB...\n\nDacă nu apare, leagă placa de PC cu un cablu USB " +
                        "sau scrie IP-ul ei în pagina PC (îl vezi pe ecranul PC al plăcii).");
            return;
        }
        if (url == loaded) return;
        loaded = url;
        ShowMessage((viaUsb ? "Încarc pagina plăcii prin USB" : "Se încarcă " + url) + " ...");
        web.CoreWebView2.Navigate(url);
    }

    bool IsDeviceUrl(string uri)
    {
        if (uri == "about:blank") return true;
        if (uri.StartsWith(TrayApp.UsbPageUrl, StringComparison.OrdinalIgnoreCase)) return true;
        return app.DeviceUrl is { } dev && Uri.TryCreate(uri, UriKind.Absolute, out var u) && Uri.TryCreate(dev, UriKind.Absolute, out var d)
               && u.Scheme == Uri.UriSchemeHttp && string.Equals(u.Host, d.Host, StringComparison.OrdinalIgnoreCase);
    }

    /// cererile paginii deschise prin USB: le face aplicația, pe cablu
    async void OnUsbRequest(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            var req = e.Request;
            var uri = new Uri(req.Uri);
            byte[]? body = null;
            if (req.Content != null)
            {
                using var ms = new MemoryStream();
                await req.Content.CopyToAsync(ms);
                body = ms.ToArray();
            }
            string ctype = req.Headers.Contains("Content-Type") ? req.Headers.GetHeader("Content-Type") : "";
            var (code, type, data) = await app.UsbFetchAsync(req.Method, uri.AbsolutePath, uri.Query, ctype, body);
            if (web?.CoreWebView2 == null) return;
            e.Response = web.CoreWebView2.Environment.CreateWebResourceResponse(new MemoryStream(data), code, code == 200 ? "OK" : "Error",
                $"Content-Type: {type}\r\nCache-Control: no-store");
        }
        catch (Exception ex)
        {
            if (web?.CoreWebView2 != null)
                e.Response = web.CoreWebView2.Environment.CreateWebResourceResponse(
                    new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{\"ok\":false,\"err\":\"" + ex.Message.Replace("\"", "'") + "\"}")),
                    502, "Error", "Content-Type: application/json");
        }
        finally { deferral.Complete(); }
    }

    void ShowMessage(string text)
    {
        overlayText.Text = text;
        overlay.Visible = true;
        overlay.BringToFront();
        Controls.SetChildIndex(overlay, 0);
    }

    void HideMessage() => overlay.Visible = false;

    static void OpenExternal(string url)
    {
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Win32Exception) { }
    }
}
