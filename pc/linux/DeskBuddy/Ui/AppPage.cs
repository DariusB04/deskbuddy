using Avalonia.Controls;
using Avalonia.Threading;

namespace DeskBuddy;

/// Aplicație: limba, update-urile (AppImage + firmware), firmware-ul prin USB, repo-ul privat, pornirea la logare, despre.
/// (Ca AppPage din Pages.cs de pe Windows.)
sealed class AppPage : PageBase
{
    readonly TrayApp app;
    readonly CheckBox autostart = Ui.Check(L.T("Pornește la logare (în tray)", "Start at login (in the tray)"));
    readonly CheckBox minimized = Ui.Check(L.T("La pornirea manuală, rămâi doar în tray (fără fereastră)", "When started manually, stay in the tray (no window)"));
    readonly CheckBox closeToTray = Ui.Check(L.T("Butonul X ascunde fereastra în tray (DeskBuddy continuă să trimită datele)", "The X button hides the window to the tray (DeskBuddy keeps sending data)"));
    readonly TextBlock about = Ui.Label(""), appVersion = Ui.Label(""), boardVersion = Ui.Label(""), updateStatus = Ui.Note(""), privInfo = Ui.Note(""), usbInfo = Ui.Note(""), usbStatus = Ui.Note("");
    readonly Button checkNow = Ui.Button(L.T("Verifică acum", "Check now")), updateApp = Ui.Button(L.T("Actualizează aplicația", "Update the app"), primary: true),
        updateBoard = Ui.Button(L.T("Urcă firmware-ul pe placă", "Upload firmware to the board"), primary: true), notes = Ui.Button(L.T("Ce e nou", "What's new")),
        usbFlash = Ui.Button(L.T("Scrie firmware-ul din release", "Write the firmware from the release"), primary: true),
        usbFile = Ui.Button(L.T("Din fișier...", "From file...")), usbScan = Ui.Button(L.T("Caută din nou", "Search again"));
    readonly CheckBox autoCheck = Ui.Check(L.T("Verifică automat (la pornire și o dată pe zi)", "Check automatically (at startup and once a day)"));
    readonly CheckBox usbErase = Ui.Check(L.T("Instalare completă: șterge și setările plăcii (WiFi, imprimantă, parolă)", "Full install: also erase the board's settings (WiFi, printer, password)"));
    readonly TextBox privRepo = Ui.TextBox(240), privToken = Ui.TextBox(240);
    readonly ComboBox usbPort = Ui.Combo(260), language = Ui.Combo(200);
    List<UsbLink.PortInfo> ports = [];
    bool updating, busy;

    public AppPage(TrayApp app) : base(L.T("Aplicație", "App"), L.T("Cum pornește, unde stă și cum se actualizează DeskBuddy.", "How DeskBuddy starts, where it lives and how it updates."))
    {
        this.app = app;
        var lang = AddCard(L.T("Limba / Language", "Language / Limba"));
        language.Items.Add("Română");
        language.Items.Add("English");
        language.SelectedIndex = L.En ? 1 : 0;
        language.SelectionChanged += (_, _) =>
        {
            string code = language.SelectedIndex == 1 ? "en" : "ro";
            if (code != app.Settings.LanguageCode) Dispatcher.UIThread.Post(() => app.SetLanguage(code));
        };
        lang.Children.Add(language);
        lang.Children.Add(Ui.Note(L.T("Se aplică aplicației și plăcii (textele de pe ecran și pagina ei web).", "Applies to the app and the board (its screen texts and web page).")));

        var upd = AddCard(L.T("Actualizări", "Updates"));
        upd.Children.AddRange([appVersion, boardVersion, Ui.Row(updateApp, updateBoard, checkNow, notes), updateStatus, autoCheck]);
        checkNow.Click += async (_, _) => await Busy(L.T("Verific GitHub...", "Checking GitHub..."), async () => { await app.CheckUpdatesAsync(manual: true); return null; });
        notes.Click += (_, _) => { if (app.Release?.PageUrl is { Length: > 0 } u) Ui.Open(u); };
        updateApp.Click += async (_, _) =>
        {
            if (!await Dialogs.Confirm(this, L.T($"Instalez DeskBuddy {app.Release?.Version.ToString(3)}? Aplicația se închide și pornește din nou.", $"Install DeskBuddy {app.Release?.Version.ToString(3)}? The app will close and start again."))) return;
            await Busy(L.T("Descarc aplicația din GitHub...", "Downloading the app from GitHub..."), app.UpdateAppAsync);
        };
        updateBoard.Click += async (_, _) =>
        {
            if (!await Dialogs.Confirm(this, L.T($"Urc firmware-ul {app.Release?.Version.ToString(3)} pe placă prin WiFi? Placa repornește, setările rămân.", $"Upload firmware {app.Release?.Version.ToString(3)} to the board over WiFi? The board restarts, settings are kept."))) return;
            string? password = null;
            await Busy(L.T("Pregătesc...", "Preparing..."), async () =>
            {
                var progress = new Progress<string>(s => updateStatus.Text = s);
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    var (result, message) = await app.UpdateFirmwareAsync(password, progress);
                    if (result != DeviceFirmware.Result.NeedPassword) return message;
                    password = await Dialogs.Ask(this, message + L.T(" Scrie parola paginii (utilizator admin):", " Enter the page password (user admin):"), password: true);
                    if (password == null) return L.Cancelled;
                }
                return L.T("Parolă greșită.", "Wrong password.");
            });
        };
        autoCheck.IsCheckedChanged += (_, _) => { if (!updating) { app.Settings.AutoUpdateCheck = autoCheck.IsChecked == true; app.Settings.Save(); } };

        var usbCard = AddCard(L.T("Firmware prin USB", "Firmware over USB"));
        usbCard.Children.Add(Ui.Note(L.T("Pentru o placă nouă (prima instalare) sau una fără WiFi. Leagă placa de PC cu un cablu USB de date. Pe Linux, utilizatorul trebuie să fie în grupul dialout.",
                                         "For a new board (first install) or one without WiFi. Connect the board with a USB data cable. On Linux your user must be in the dialout group.")));
        usbCard.Children.AddRange([Ui.Row(usbPort, usbScan), usbErase, Ui.Row(usbFlash, usbFile), usbInfo, usbStatus]);
        usbScan.Click += (_, _) => RefreshPorts();
        usbFlash.Click += async (_, _) => await FlashUsb(null);
        usbFile.Click += async (_, _) => { if (await Dialogs.PickFile(this, L.T("Alege firmware-ul", "Choose the firmware"), "*.bin") is { } f) await FlashUsb(f); };

        var priv = AddCard(L.T("Firmware din repo privat (opțional)", "Firmware from a private repo (optional)"));
        priv.Children.Add(Ui.Note(L.T("Dacă placa ta rulează un firmware compilat dintr-un repo GitHub privat, aplicația îl poate actualiza de acolo, cu un token GitHub doar de citire (Contents: Read).",
                                      "If your board runs firmware built from a private GitHub repo, the app can update it from there, with a read-only GitHub token (Contents: Read).")));
        privRepo.Watermark = "owner/repo";
        privToken.PasswordChar = '•';
        var savePriv = Ui.Button(L.T("Salvează", "Save"));
        var clearPriv = Ui.Button(L.T("Șterge", "Remove"));
        priv.Children.AddRange([Ui.Row(privRepo, privToken, savePriv, clearPriv), privInfo]);
        savePriv.Click += async (_, _) =>
        {
            string repo = (privRepo.Text ?? "").Trim();
            string? token = (privToken.Text ?? "").Trim() is { Length: > 0 } t ? t : null;
            if (!System.Text.RegularExpressions.Regex.IsMatch(repo, @"^[\w.-]+/[\w.-]+$")) { Ui.Fail(privInfo, L.T("Repo-ul se scrie owner/nume.", "Write the repo as owner/name.")); return; }
            if (token == null && app.Settings.FirmwareTokenProtected.Length == 0) { Ui.Fail(privInfo, L.T("Lipsește token-ul.", "The token is missing.")); return; }
            app.SetPrivateFirmware(repo, token);
            privToken.Text = "";
            await Busy(L.T("Verific repo-ul privat...", "Checking the private repo..."), async () => { await app.CheckUpdatesAsync(manual: true); return app.PrivateError; });
            RefreshPrivate();
        };
        clearPriv.Click += (_, _) => { app.SetPrivateFirmware("", ""); privRepo.Text = ""; RefreshPrivate(); RefreshData(); };

        var start = AddCard(L.T("Pornire", "Startup"));
        start.Children.AddRange([autostart, minimized, closeToTray]);
        autostart.IsCheckedChanged += (_, _) => { if (!updating) Autostart.Set(autostart.IsChecked == true); };
        minimized.IsCheckedChanged += (_, _) => { if (!updating) { app.Settings.StartMinimized = minimized.IsChecked == true; app.Settings.Save(); } };
        closeToTray.IsCheckedChanged += (_, _) => { if (!updating) { app.Settings.CloseToTray = closeToTray.IsChecked == true; app.Settings.Save(); } };

        var info = AddCard(L.T("Despre", "About"));
        var exit = Ui.Button(L.T("Ieșire din DeskBuddy", "Exit DeskBuddy"));
        exit.Click += (_, _) => app.Quit();
        info.Children.AddRange([about, exit]);
    }

    public override void OnShown()
    {
        updating = true;
        minimized.IsChecked = app.Settings.StartMinimized;
        closeToTray.IsChecked = app.Settings.CloseToTray;
        autoCheck.IsChecked = app.Settings.AutoUpdateCheck;
        autostart.IsChecked = Autostart.IsEnabled();
        updating = false;
        privRepo.Text = app.Settings.FirmwareRepo;
        RefreshPrivate();
        RefreshPorts();
        about.Text = L.T($"DeskBuddy pentru Linux {Updater.CurrentText}\n{Updater.SelfPath}", $"DeskBuddy for Linux {Updater.CurrentText}\n{Updater.SelfPath}");
        base.OnShown();
    }

    void RefreshPrivate()
    {
        privToken.Watermark = app.Settings.FirmwareTokenProtected.Length > 0 ? L.T("token salvat · lasă gol ca să-l păstrezi", "token saved · leave empty to keep it") : L.T("token GitHub (github_pat_...)", "GitHub token (github_pat_...)");
        if (app.PrivateError != null) Ui.Fail(privInfo, app.PrivateError);
        else if (app.PrivateRelease is { } r) Ui.Ok(privInfo, L.T($"Merge: ultimul release privat e {r.Version.ToString(3)}.", $"Works: the latest private release is {r.Version.ToString(3)}."));
        else { privInfo.Text = app.Settings.HasPrivateFirmware ? L.T("Salvat. Apasă „Verifică acum”.", "Saved. Press “Check now”.") : L.T("Neconfigurat.", "Not set up."); privInfo.Foreground = Ui.Muted; }
    }

    void RefreshPorts()
    {
        string? keep = usbPort.SelectedIndex >= 0 && usbPort.SelectedIndex < ports.Count ? ports[usbPort.SelectedIndex].Name : null;
        ports = TrayApp.UsbPorts();
        usbPort.Items.Clear();
        foreach (var p in ports) usbPort.Items.Add(app.UsbBoard?.Port == p.Name ? $"{p} · DeskBuddy {app.UsbBoard.Version}" : p.ToString());
        if (ports.Count == 0) { usbPort.Items.Add(L.T("nicio placă pe USB", "no board on USB")); usbPort.SelectedIndex = 0; }
        else usbPort.SelectedIndex = Math.Max(0, ports.FindIndex(p => p.Name == (keep ?? app.UsbBoard?.Port)));
        RefreshUsb();
    }

    void RefreshUsb()
    {
        bool hasPort = ports.Count > 0;
        usbFile.IsEnabled = usbPort.IsEnabled = usbErase.IsEnabled = !busy && hasPort;
        usbScan.IsEnabled = !busy;
        var rel = app.FirmwareRelease;
        string source = app.UsePrivateFirmware ? L.T("repo-ul privat", "the private repo") : L.T("release-ul public", "the public release");
        if (app.PrivateFirmwareMissing) { usbInfo.Text = L.T("Firmware-ul plăcii vine din repo-ul privat: completează-l mai jos. Sau alege un .bin cu „Din fișier...”.", "The board's firmware comes from the private repo: fill it in below. Or pick a .bin with “From file...”."); usbInfo.Foreground = Ui.Warn; }
        else if (rel?.Flash != null) { usbInfo.Text = L.T($"Din release: firmware {rel.Version.ToString(3)} din {source}.", $"From the release: firmware {rel.Version.ToString(3)} from {source}."); usbInfo.Foreground = Ui.Muted; }
        else { usbInfo.Text = L.T("Încă nu știu ultimul release: apasă „Verifică acum” sau alege un .bin cu „Din fișier...”.", "Don't know the latest release yet: press “Check now” or pick a .bin with “From file...”."); usbInfo.Foreground = Ui.Muted; }
        usbFlash.IsEnabled = !busy && hasPort && rel?.Flash != null;
    }

    async Task FlashUsb(string? file)
    {
        if (busy || ports.Count == 0) return;
        var port = ports[Math.Clamp(usbPort.SelectedIndex, 0, ports.Count - 1)];
        bool full = usbErase.IsChecked == true;
        string what = file != null ? Path.GetFileName(file) : L.T($"firmware-ul {app.FirmwareRelease?.Version.ToString(3)}", $"firmware {app.FirmwareRelease?.Version.ToString(3)}");
        string keep = full ? L.T("Se șterg și setările plăcii (WiFi, imprimantă, parolă): după aceea pornește în SETUP.", "The board's settings (WiFi, printer, password) are erased too: it will start in SETUP afterwards.")
                           : L.T("Setările plăcii rămân.", "The board's settings are kept.");
        if (!await Dialogs.Confirm(this, L.T($"Scriu {what} pe placa de pe {port.Name}? {keep}\nNu scoate cablul cât durează (~30 s).", $"Write {what} to the board on {port.Name}? {keep}\nDon't unplug the cable while it runs (~30 s)."))) return;
        busy = true;
        RefreshUsb();
        usbStatus.Foreground = Ui.Muted;
        var progress = new Progress<string>(s => usbStatus.Text = s);
        string result;
        try { result = await app.FlashUsbAsync(port.Name, full ? TrayApp.UsbFlashKind.Full : TrayApp.UsbFlashKind.KeepSettings, file, progress); }
        catch (Exception e) { result = L.T("Eroare: ", "Error: ") + e.Message; }
        busy = false;
        usbStatus.Text = result;
        usbStatus.Foreground = L.IsOk(result) ? Ui.Accent : Ui.Bad;
        usbErase.IsChecked = false;
        RefreshPorts();
        RefreshData();
    }

    async Task Busy(string text, Func<Task<string?>> work)
    {
        if (busy) return;
        busy = true;
        updateStatus.Text = text;
        updateStatus.Foreground = Ui.Muted;
        RefreshData();
        string? result;
        try { result = await work(); }
        catch (Exception e) { result = L.T("Eroare: ", "Error: ") + e.Message; }
        busy = false;
        updateStatus.Text = result ?? "";
        updateStatus.Foreground = L.IsOk(result) ? Ui.Accent : result != null && result != L.Cancelled ? Ui.Bad : Ui.Muted;
        RefreshData();
    }

    public override void RefreshData()
    {
        var rel = app.Release;
        string latest = rel?.Version.ToString(3) ?? "?";
        if (Updater.Repo == null) appVersion.Text = L.T($"Aplicația: {Updater.CurrentText} (build local, fără verificare de update-uri)", $"App: {Updater.CurrentText} (local build, no update checks)");
        else if (app.AppUpdateAvailable) appVersion.Text = L.T($"Aplicația: {Updater.CurrentText}  →  {latest} disponibilă", $"App: {Updater.CurrentText}  →  {latest} available");
        else if (rel != null) appVersion.Text = L.T($"Aplicația: {Updater.CurrentText} (la zi)", $"App: {Updater.CurrentText} (up to date)");
        else appVersion.Text = L.T($"Aplicația: {Updater.CurrentText}", $"App: {Updater.CurrentText}") + (app.UpdateError != null ? $" ({app.UpdateError})" : "");
        appVersion.Foreground = app.AppUpdateAvailable ? Ui.Accent : Ui.Fg;

        var fwRel = app.FirmwareRelease;
        string board = app.BoardVersion switch
        {
            null => app.Device == null ? L.T("negăsită în rețea sau pe USB", "not found on the network or USB") : L.T("nu răspunde", "not responding"),
            "" => L.T("firmware vechi (fără număr de versiune)", "old firmware (no version number)"),
            var v => "firmware " + v + (app.BoardViaUsb ? L.T(", pe USB", ", on USB") : ""),
        };
        string fwState = app.FirmwareUpdateAvailable ? L.T($"  →  {fwRel!.Version.ToString(3)} disponibil", $"  →  {fwRel!.Version.ToString(3)} available")
            : fwRel != null && app.BoardVersion is { Length: > 0 } ? L.T(" (la zi)", " (up to date)") : "";
        boardVersion.Text = L.T($"Placa: {board}{fwState}", $"Board: {board}{fwState}");
        boardVersion.Foreground = app.FirmwareUpdateAvailable ? Ui.Accent : Ui.Fg;
        // AppImage-ul se poate actualiza singur doar când rulează ca AppImage (variabila APPIMAGE)
        updateApp.IsVisible = app.AppUpdateAvailable && Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 };
        updateBoard.IsVisible = app.FirmwareUpdateAvailable && app.DeviceUrl != null;
        notes.IsVisible = rel != null;
        checkNow.IsEnabled = updateApp.IsEnabled = updateBoard.IsEnabled = !busy;
        RefreshUsb();
    }
}
