using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeskBuddy;

/// Claude Code -> DeskBuddy: același protocol ca pc/claude_bridge.py.
///   DeskBuddy.exe claude-hook                 apelat de hook-urile Claude Code (async): prompt / unealtă / gata / permisiune / eroare
///   DeskBuddy.exe claude-statusline [--wrap]  status line: usage 5h / 7 zile; cu --wrap rulează și status line-ul tău vechi
/// Usage-ul vine doar din status line, iar status line-ul îl rulează doar `claude` din terminal (nu aplicația desktop).
static class ClaudeBridge
{
    static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    // CLAUDE_CONFIG_DIR: aceeași variabilă pe care o respectă și Claude Code (implicit ~/.claude)
    static readonly string ClaudeDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } d ? d : Path.Combine(Home, ".claude");
    static readonly string SettingsPath = Path.Combine(ClaudeDir, "settings.json");
    static readonly string BackupSl = Path.Combine(ClaudeDir, "deskbuddy_statusline_backup.json");   // același fișier ca scriptul Python
    static readonly string CacheDir = Path.Combine(Home, ".cache", "deskbuddy");
    public static readonly string UsageCache = Path.Combine(CacheDir, "usage.json");

    static readonly (string Hook, string Code)[] Events =
    [
        ("UserPromptSubmit", "prompt"), ("PreToolUse", "tool"), ("PostToolUse", "post"), ("Notification", "notify"),
        ("Stop", "stop"), ("StopFailure", "fail"), ("SessionEnd", "end"),
    ];

    static readonly (string Key, string Label)[] Errors =
    [
        ("rate_limit", "limita de usage"), ("authentication_failed", "autentificare"), ("billing_error", "facturare"),
        ("invalid_request", "cerere invalida"), ("server_error", "eroare server"), ("overloaded", "server ocupat"),
        ("max_output_tokens", "raspuns prea lung"),
    ];

    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // ------------------------------------------------------------ moduri apelate de Claude Code
    public static void Hook()
    {
        var (_, d) = ReadStdin();
        string evName = Str(d?["hook_event_name"]);
        string? ev = Events.FirstOrDefault(e => e.Hook == evName).Code;
        if (ev == null) return;
        var pkt = new JsonObject { ["t"] = "ce", ["e"] = ev, ["s"] = ShortId(d) };
        if (ev == "tool") pkt["tool"] = ToolLabel(Str(d?["tool_name"]));
        else if (ev == "notify") pkt["n"] = Cut(Str(d?["notification_type"]), 20);
        else if (ev == "fail") pkt["n"] = ErrorLabel(d?["error_type"] ?? d?["error"] ?? d?["reason"]);
        Relay.Send(Bytes(pkt));
    }

    public static void StatusLine(bool wrap)
    {
        var (raw, d) = ReadStdin();
        try { Directory.CreateDirectory(CacheDir); File.WriteAllText(Path.Combine(CacheDir, "statusline.json"), raw); }   // pentru depanare
        catch (IOException) { }

        var rl = d?["rate_limits"];
        var fh = rl?["five_hour"];
        var sd = rl?["seven_day"];
        var pkt = new JsonObject { ["t"] = "cu", ["s"] = ShortId(d), ["m"] = Cut(Str(d?["model"]?["display_name"]), 14) };
        if (Num(fh?["used_percentage"]) is double h) { pkt["h"] = Math.Round(h, 1); pkt["hr"] = Epoch(fh?["resets_at"]); }
        if (Num(sd?["used_percentage"]) is double w) { pkt["w"] = Math.Round(w, 1); pkt["wr"] = Epoch(sd?["resets_at"]); }
        if (Num(d?["context_window"]?["used_percentage"]) is double c) pkt["c"] = (int)c;
        pkt["at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Relay.Send(Bytes(pkt));

        if (pkt.ContainsKey("h") || pkt.ContainsKey("w"))      // aplicația din tray îl retrimite la 30 s
        {
            try
            {
                var keep = new JsonObject();
                foreach (var k in new[] { "m", "h", "hr", "w", "wr", "at" })
                    if (pkt[k] is JsonNode n) keep[k] = n.DeepClone();
                string tmp = UsageCache + ".tmp";
                File.WriteAllText(tmp, keep.ToJsonString());
                File.Move(tmp, UsageCache, overwrite: true);
            }
            catch (IOException) { }
        }

        string output;
        if (wrap) output = RunOldStatusLine(raw);
        else
        {
            var parts = new List<string> { Str(pkt["m"]) is { Length: > 0 } m ? m : "Claude" };
            if (Num(pkt["h"]) is double hh) parts.Add(string.Create(CultureInfo.InvariantCulture, $"5h {hh:0}%"));
            if (Num(pkt["w"]) is double ww) parts.Add(string.Create(CultureInfo.InvariantCulture, $"7z {ww:0}%"));
            if (Num(pkt["c"]) is double cc) parts.Add($"ctx {cc:0}%");
            output = string.Join(" · ", parts) + "\n";
        }
        using var stdout = Console.OpenStandardOutput();
        var bytes = Encoding.UTF8.GetBytes(output);
        stdout.Write(bytes, 0, bytes.Length);
    }

    /// Status line-ul pe care îl aveai înainte (salvat la instalare) primește același JSON, iar ce scoate el apare în terminal.
    static string RunOldStatusLine(string raw)
    {
        try
        {
            string cmd = Str(JsonNode.Parse(File.ReadAllText(BackupSl))?["command"]);
            if (cmd.Length == 0) return "";
            var psi = new ProcessStartInfo("cmd.exe", $"/d /s /c \"{cmd}\"")
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi)!;
            p.StandardInput.Write(raw);
            p.StandardInput.Close();
            var read = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(10_000)) { p.Kill(true); return ""; }
            return read.Result;
        }
        catch (Exception) { return ""; }
    }

    public static string SettingsFile => SettingsPath;

    public sealed record Usage(string Model, double? H, long Hr, double? W, long Wr, long At);

    /// Ultimul usage salvat de status line (pentru pagina Claude Code din fereastră).
    public static Usage? ReadUsage()
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(UsageCache)) is not JsonObject d) return null;
            return new Usage(Str(d["m"]), Num(d["h"]), (long)(Num(d["hr"]) ?? 0), Num(d["w"]), (long)(Num(d["wr"]) ?? 0), (long)(Num(d["at"]) ?? 0));
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    /// Ultimul usage salvat, ca DeskBuddy să-l aibă și după o repornire (fără "s": nu e o sesiune).
    public static byte[]? CachedUsagePacket()
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(UsageCache)) is not JsonObject d) return null;
            var o = new JsonObject();
            foreach (var k in new[] { "m", "h", "hr", "w", "wr", "at" })
                if (d[k] is JsonNode n) o[k] = n.DeepClone();
            if (o.Count == 0) return null;
            o["t"] = "cu";
            return Bytes(o);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    // ------------------------------------------------------------ instalare în ~/.claude/settings.json
    static string ExeForCommand()
    {
        string p = (Environment.ProcessPath ?? "DeskBuddy.exe").Replace('\\', '/');   // merge și în bash, și în cmd / PowerShell
        return p.Contains(' ') ? $"\"{p}\"" : p;
    }

    static string Cmd(string mode) => $"{ExeForCommand()} {mode}";

    static bool IsOurs(string cmd) =>
        cmd.Contains("claude_bridge.py") ||
        (cmd.Contains("DeskBuddy", StringComparison.OrdinalIgnoreCase) && (cmd.Contains(" claude-hook") || cmd.Contains(" claude-statusline")));

    static IEnumerable<string> HookCommands(JsonObject cfg) =>
        (cfg["hooks"] as JsonObject ?? new JsonObject()).Select(kv => kv.Value).OfType<JsonArray>()
            .SelectMany(groups => groups).OfType<JsonObject>()
            .SelectMany(g => g["hooks"] as JsonArray ?? new JsonArray()).Select(h => Str(h?["command"]));

    public static bool IsInstalled()
    {
        try { return HookCommands(Load()).Any(c => IsOurs(c) && c.Contains(" claude-hook")); }
        catch (Exception) { return false; }
    }

    /// Dacă ai mutat DeskBuddy.exe, hook-urile ar arăta spre calea veche: le refacem.
    public static void FixPathIfMoved()
    {
        try
        {
            var cmds = HookCommands(Load()).Where(c => IsOurs(c) && c.Contains(" claude-hook")).ToList();
            if (cmds.Count > 0 && cmds.Any(c => c != Cmd("claude-hook"))) Install();
        }
        catch (Exception) { }
    }

    public static void Install()
    {
        var cfg = Load();
        if (File.Exists(SettingsPath)) File.Copy(SettingsPath, SettingsPath + ".deskbuddy.bak", overwrite: true);
        Strip(cfg);                                   // scoate și varianta Python (claude_bridge.py), dacă era

        if (cfg["hooks"] is not JsonObject hooks) { hooks = new JsonObject(); cfg["hooks"] = hooks; }
        foreach (var (ev, _) in Events)
        {
            if (hooks[ev] is not JsonArray arr) { arr = new JsonArray(); hooks[ev] = arr; }
            arr.Add(new JsonObject
            {
                ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = Cmd("claude-hook"), ["async"] = true }),
            });
        }

        var old = cfg["statusLine"] as JsonObject;
        Directory.CreateDirectory(Path.GetDirectoryName(BackupSl)!);
        File.WriteAllText(BackupSl, old?.ToJsonString(Pretty) ?? "null");   // ca să-l punem la loc la dezinstalare
        bool wrap = Str(old?["command"]).Length > 0;
        var sl = new JsonObject { ["type"] = "command", ["command"] = Cmd("claude-statusline") + (wrap ? " --wrap" : ""), ["refreshInterval"] = 60 };
        if (old?["padding"] is JsonNode pad) sl["padding"] = pad.DeepClone();
        cfg["statusLine"] = sl;
        Save(cfg);
    }

    public static void Uninstall()
    {
        var cfg = Load();
        Strip(cfg);
        Save(cfg);
        if (File.Exists(BackupSl)) File.Delete(BackupSl);
    }

    static void Strip(JsonObject cfg)
    {
        if (cfg["hooks"] is JsonObject hooks)
        {
            foreach (string ev in hooks.Select(kv => kv.Key).ToList())
            {
                if (hooks[ev] is not JsonArray groups) continue;
                var keep = new JsonArray();
                foreach (var g in groups)
                {
                    if (g is JsonObject go && go["hooks"] is JsonArray hs)
                    {
                        var left = hs.Where(h => !IsOurs(Str(h?["command"]))).Select(h => h?.DeepClone()).ToArray();
                        if (left.Length == 0) continue;
                        var ng = (JsonObject)go.DeepClone();
                        ng["hooks"] = new JsonArray(left);
                        keep.Add(ng);
                    }
                    else keep.Add(g?.DeepClone());
                }
                if (keep.Count > 0) hooks[ev] = keep;
                else hooks.Remove(ev);
            }
            if (hooks.Count == 0) cfg.Remove("hooks");
        }

        if (cfg["statusLine"] is JsonObject sl && IsOurs(Str(sl["command"])))
        {
            cfg.Remove("statusLine");
            try
            {
                if (File.Exists(BackupSl) && JsonNode.Parse(File.ReadAllText(BackupSl)) is JsonObject old)
                    cfg["statusLine"] = old;          // status line-ul tău vechi
            }
            catch (JsonException) { }
        }
    }

    static JsonObject Load()
    {
        if (!File.Exists(SettingsPath)) return new JsonObject();
        string txt = File.ReadAllText(SettingsPath).Trim();
        if (txt.Length == 0) return new JsonObject();
        return JsonNode.Parse(txt, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
               ?? throw new InvalidDataException("settings.json nu e un obiect JSON");
    }

    static void Save(JsonObject cfg)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        string tmp = SettingsPath + ".tmp";
        File.WriteAllText(tmp, cfg.ToJsonString(Pretty) + "\n", new UTF8Encoding(false));
        File.Move(tmp, SettingsPath, overwrite: true);
    }

    // ------------------------------------------------------------ test fără Claude Code
    public static void Test(Action<byte[]> send)
    {
        const string sid = "test1234";
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        send(Bytes(new JsonObject   // fără "at": nu blochează usage-ul real
        {
            ["t"] = "cu", ["s"] = sid, ["m"] = "Opus", ["h"] = 37.5, ["hr"] = now + 7200, ["w"] = 61.0, ["wr"] = now + 3 * 86400, ["c"] = 22,
        }));
        (string Ev, string? Tool, string? N, int WaitMs)[] steps =
        [
            ("prompt", null, null, 4000), ("tool", "Bash", null, 3000), ("tool", "Edit", null, 3000),
            ("notify", null, "permission_prompt", 4000), ("post", null, null, 2000), ("stop", null, null, 0),
        ];
        foreach (var (ev, tool, n, wait) in steps)
        {
            var p = new JsonObject { ["t"] = "ce", ["e"] = ev, ["s"] = sid };
            if (tool != null) p["tool"] = tool;
            if (n != null) p["n"] = n;
            send(Bytes(p));
            Thread.Sleep(wait);
        }
    }

    // ------------------------------------------------------------ utilitare
    static (string Raw, JsonNode? Doc) ReadStdin()
    {
        try
        {
            using var r = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
            string raw = r.ReadToEnd();
            return (raw, string.IsNullOrWhiteSpace(raw) ? null : JsonNode.Parse(raw));
        }
        catch (Exception) { return ("", null); }
    }

    static byte[] Bytes(JsonObject o) => Encoding.UTF8.GetBytes(o.ToJsonString());   // non-ASCII iese ca \uXXXX

    static string Str(JsonNode? n) =>
        n is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : "";

    static double? Num(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.GetValueKind() == JsonValueKind.Number)      // merge și pentru valori puse din cod (int / long), nu doar parsate
            return double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : null;
        if (v.GetValueKind() == JsonValueKind.String &&
            double.TryParse(v.GetValue<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        return null;
    }

    /// resets_at poate veni ca epoch (număr) sau ca text ISO 8601.
    static long Epoch(JsonNode? n)
    {
        if (Num(n) is double d) return (long)d;
        return DateTimeOffset.TryParse(Str(n), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
            ? t.ToUnixTimeSeconds() : 0;
    }

    static string ShortId(JsonNode? d) => Cut(Str(d?["session_id"]), 8);

    static string Cut(string s, int n) => s.Length > n ? s[..n] : s;

    /// mcp__server__unealta -> unealta (pe ecran e loc de ~16 caractere)
    static string ToolLabel(string name)
    {
        if (name.StartsWith("mcp__", StringComparison.Ordinal))
        {
            string last = name.Split("__")[^1];
            if (last.Length > 0) name = last;
        }
        return Cut(name, 22);
    }

    static string ErrorLabel(JsonNode? err)
    {
        string e = err is JsonObject o ? Str(o["type"]) : Str(err);
        foreach (var (k, label) in Errors)
            if (e.Contains(k)) return label;
        return Cut((e.Length > 0 ? e : "eroare").Replace('_', ' '), 20);
    }
}
