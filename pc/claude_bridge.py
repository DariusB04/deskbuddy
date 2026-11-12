#!/usr/bin/env python3
"""
DeskBuddy ↔ Claude Code: trimite la DeskBuddy (UDP) usage-ul și ce face Claude Code.

Instalare (o singură dată):
    python3 claude_bridge.py install
  - adaugă în ~/.claude/settings.json hook-urile (async, nu încetinesc Claude Code)
  - pune scriptul ca status line; dacă aveai deja un status line, îl păstrează (îl rulează și afișează ce scotea el)
  - face backup: ~/.claude/settings.json.deskbuddy.bak
Dezinstalare:
    python3 claude_bridge.py uninstall

Moduri (le apelează Claude Code, nu tu):
    claude_bridge.py statusline [--wrap "comanda veche"]   -> usage 5h / 7 zile, model, context
    claude_bridge.py hook                                  -> prompt trimis / unealtă / gata / permisiune / eroare

Usage-ul (5h / 7 zile) vine DOAR din status line, iar status line-ul îl rulează doar `claude` din terminal
(aplicația desktop nu). Ultima valoare se păstrează în ~/.cache/deskbuddy/usage.json și pc_stats_sender.py
o retrimite periodic, ca DeskBuddy s-o aibă și după o repornire.

Unde trimite: broadcast pe portul 4210, pe fiecare rețea a PC-ului (ethernet + WiFi), ca pc_stats_sender.py.
Altfel: variabila DESKBUDDY_HOST=192.168.1.77
Test fără Claude Code:  python3 claude_bridge.py test
"""
import ipaddress
import json
import os
import shutil
import socket
import subprocess
import sys
import time

HOST = os.environ.get("DESKBUDDY_HOST", "")          # gol = broadcast pe toate rețelele
PORT = int(os.environ.get("DESKBUDDY_PORT", "4210"))
SETTINGS = os.path.join(os.path.expanduser("~"), ".claude", "settings.json")
BACKUP_SL = os.path.join(os.path.expanduser("~"), ".claude", "deskbuddy_statusline_backup.json")
MARK = "claude_bridge.py"
CACHE = os.path.join(os.path.expanduser("~"), ".cache", "deskbuddy")

# hook-urile pe care le ascultăm -> cod scurt trimis la ESP32
EVENTS = {
    "UserPromptSubmit": "prompt",
    "PreToolUse": "tool",
    "PostToolUse": "post",
    "Notification": "notify",
    "Stop": "stop",
    "StopFailure": "fail",
    "SessionEnd": "end",
}


def broadcast_targets():
    """Adresa de broadcast a fiecărei rețele. 255.255.255.255 pleacă doar pe ruta implicită
    (ex. ethernet), deci nu ajunge la DeskBuddy dacă el e pe WiFi-ul PC-ului."""
    out = []
    try:
        import psutil
        for addrs in psutil.net_if_addrs().values():
            for a in addrs:
                if a.family != socket.AF_INET or not a.netmask:
                    continue
                net = ipaddress.IPv4Network(f"{a.address}/{a.netmask}", strict=False)
                if net.is_loopback or net.prefixlen >= 31:      # lo, tailscale
                    continue
                b = str(net.broadcast_address)
                if b not in out:
                    out.append(b)
    except Exception:
        pass
    return out or ["255.255.255.255"]


def send(obj):
    data = json.dumps(obj, separators=(",", ":"), ensure_ascii=True).encode()
    if len(data) > 500:                      # ESP32 citește max 511 octeți
        return
    try:
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        s.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
        for host in [HOST] if HOST else broadcast_targets():
            try:
                s.sendto(data, (host, PORT))
            except OSError:
                pass
        s.close()
    except OSError:
        pass                                  # DeskBuddy offline: nu stricăm nimic în Claude Code


def epoch(v):
    """resets_at poate veni ca epoch (număr) sau ca text ISO 8601."""
    if v is None or v == "":
        return 0
    try:
        return int(float(v))
    except (TypeError, ValueError):
        pass
    try:
        from datetime import datetime
        return int(datetime.fromisoformat(str(v).replace("Z", "+00:00")).timestamp())
    except ValueError:
        return 0


def short_id(sid):
    return (sid or "")[:8]


def read_stdin_json():
    try:
        raw = sys.stdin.read()
        return raw, json.loads(raw) if raw.strip() else {}
    except Exception:
        return "", {}


# ---------------------------------------------------------------- status line
def statusline(argv):
    raw, d = read_stdin_json()
    try:                                     # ultimul input, pentru depanare
        os.makedirs(CACHE, exist_ok=True)
        with open(os.path.join(CACHE, "statusline.json"), "w", encoding="utf-8") as f:
            f.write(raw)
    except OSError:
        pass
    rl = d.get("rate_limits") or {}
    fh, sd = rl.get("five_hour") or {}, rl.get("seven_day") or {}
    pkt = {"t": "cu", "s": short_id(d.get("session_id")),
           "m": ((d.get("model") or {}).get("display_name") or "")[:14]}
    if fh.get("used_percentage") is not None:
        pkt["h"] = round(float(fh["used_percentage"]), 1)
        pkt["hr"] = epoch(fh.get("resets_at"))
    if sd.get("used_percentage") is not None:
        pkt["w"] = round(float(sd["used_percentage"]), 1)
        pkt["wr"] = epoch(sd.get("resets_at"))
    ctx = (d.get("context_window") or {}).get("used_percentage")
    if ctx is not None:
        pkt["c"] = int(ctx)
    pkt["at"] = int(time.time())
    send(pkt)
    if "h" in pkt or "w" in pkt:                # pentru pc_stats_sender.py (retrimite periodic)
        try:
            keep = {k: pkt[k] for k in ("m", "h", "hr", "w", "wr", "at") if k in pkt}
            tmp = os.path.join(CACHE, "usage.json.tmp")
            with open(tmp, "w", encoding="utf-8") as f:
                json.dump(keep, f)
            os.replace(tmp, os.path.join(CACHE, "usage.json"))
        except OSError:
            pass

    # ce apare în terminal: status line-ul tău vechi (dacă era) sau unul scurt
    if "--wrap" in argv:
        cmd = argv[argv.index("--wrap") + 1]
        try:
            out = subprocess.run(cmd, shell=True, input=raw, capture_output=True, text=True, timeout=10)
            sys.stdout.write(out.stdout)
        except Exception:
            pass
        return
    parts = [pkt["m"] or "Claude"]
    if "h" in pkt:
        parts.append(f"5h {pkt['h']:.0f}%")
    if "w" in pkt:
        parts.append(f"7z {pkt['w']:.0f}%")
    if "c" in pkt:
        parts.append(f"ctx {pkt['c']}%")
    print(" · ".join(parts))


# ---------------------------------------------------------------- hooks
def tool_label(name):
    """mcp__server__unealta -> unealta (pe ecran e loc de ~16 caractere)."""
    name = str(name or "")
    if name.startswith("mcp__"):
        name = name.split("__")[-1] or name
    return name[:22]


ERRORS = {"rate_limit": "limita de usage", "authentication_failed": "autentificare",
          "billing_error": "facturare", "invalid_request": "cerere invalida", "server_error": "eroare server",
          "overloaded": "server ocupat", "max_output_tokens": "raspuns prea lung"}


def error_label(err):
    if not isinstance(err, str):
        err = str((err or {}).get("type", "")) if isinstance(err, dict) else ""
    for k, v in ERRORS.items():
        if k in err:
            return v
    return (err or "eroare").replace("_", " ")[:20]


def hook():
    _, d = read_stdin_json()
    ev = EVENTS.get(d.get("hook_event_name", ""))
    if not ev:
        return
    pkt = {"t": "ce", "e": ev, "s": short_id(d.get("session_id"))}
    if ev == "tool":
        pkt["tool"] = tool_label(d.get("tool_name"))
    elif ev == "notify":
        pkt["n"] = str(d.get("notification_type", ""))[:20]
    elif ev == "fail":
        pkt["n"] = error_label(d.get("error_type") or d.get("error") or d.get("reason"))
    send(pkt)


# ---------------------------------------------------------------- install
def _q(p):
    if os.name == "nt":
        return '"' + p.replace('"', '\\"') + '"'
    return "'" + p.replace("'", "'\\''") + "'"      # ghilimele simple: shell-ul nu expandează nimic din interior


def _cmd(mode):
    return f"{_q(sys.executable or 'python3')} {_q(os.path.abspath(__file__))} {mode}"


def _load():
    if not os.path.exists(SETTINGS):
        return {}
    with open(SETTINGS, encoding="utf-8") as f:
        txt = f.read().strip()
    return json.loads(txt) if txt else {}


def _save(cfg):
    os.makedirs(os.path.dirname(SETTINGS), exist_ok=True)
    tmp = SETTINGS + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(cfg, f, indent=2, ensure_ascii=False)
        f.write("\n")
    os.replace(tmp, SETTINGS)


def _strip(cfg):
    """scoate tot ce a pus scriptul ăsta (pt. reinstalare / dezinstalare)."""
    hooks = cfg.get("hooks") or {}
    for ev in list(hooks):
        groups = []
        for g in hooks[ev]:
            hs = [h for h in g.get("hooks", []) if MARK not in str(h.get("command", ""))]
            if hs:
                g = dict(g, hooks=hs)
                groups.append(g)
        if groups:
            hooks[ev] = groups
        else:
            del hooks[ev]
    if hooks:
        cfg["hooks"] = hooks
    else:
        cfg.pop("hooks", None)
    sl = cfg.get("statusLine")
    if isinstance(sl, dict) and MARK in str(sl.get("command", "")):
        cfg.pop("statusLine")
        if os.path.exists(BACKUP_SL):                       # pune la loc status line-ul tău vechi
            with open(BACKUP_SL, encoding="utf-8") as f:
                old = json.load(f)
            if old:
                cfg["statusLine"] = old
    return cfg


def install():
    cfg = _load()
    if os.path.exists(SETTINGS):
        shutil.copy2(SETTINGS, SETTINGS + ".deskbuddy.bak")
    cfg = _strip(cfg)
    hooks = cfg.setdefault("hooks", {})
    for ev in EVENTS:
        hooks.setdefault(ev, []).append({"hooks": [{"type": "command", "command": _cmd("hook"), "async": True}]})
    old = cfg.get("statusLine")
    sl = {"type": "command", "command": _cmd("statusline"), "refreshInterval": 60}
    with open(BACKUP_SL, "w", encoding="utf-8") as f:       # ca să-l putem pune la loc la uninstall
        json.dump(old if isinstance(old, dict) else None, f, indent=2)
    if isinstance(old, dict) and old.get("command"):
        sl["command"] = _cmd("statusline") + " --wrap " + _q(old["command"])
        if "padding" in old:
            sl["padding"] = old["padding"]
    cfg["statusLine"] = sl
    _save(cfg)
    print(f"Gata: {SETTINGS} actualizat (backup: settings.json.deskbuddy.bak).")
    if old:
        print("Status line-ul tău vechi rămâne: scriptul îl rulează și afișează ce scotea el.")
    print("Repornește sesiunile Claude Code deschise ca să preia setările.")


def uninstall():
    cfg = _strip(_load())
    _save(cfg)
    if os.path.exists(BACKUP_SL):
        os.remove(BACKUP_SL)
    print("Scos din ~/.claude/settings.json.")


def test():
    print(f"Trimit date de test la {HOST or 'broadcast ' + ', '.join(broadcast_targets())}:{PORT} ...")
    sid = "test1234"
    send({"t": "cu", "s": sid, "m": "Opus", "h": 37.5, "hr": int(time.time()) + 7200,
          "w": 61.0, "wr": int(time.time()) + 3 * 86400, "c": 22})   # fără "at": nu blochează usage-ul real
    for ev, extra, wait in [("prompt", {}, 4), ("tool", {"tool": "Bash"}, 3), ("tool", {"tool": "Edit"}, 3),
                            ("notify", {"n": "permission_prompt"}, 4), ("post", {}, 2), ("stop", {}, 6)]:
        print("  ", ev, extra)
        send(dict({"t": "ce", "e": ev, "s": sid}, **extra))
        time.sleep(wait)
    print("Gata. Pe DeskBuddy, modul CLAUDE trebuie să fi arătat: lucrează -> Bash -> Edit -> te așteaptă -> gata.")


if __name__ == "__main__":
    mode = sys.argv[1] if len(sys.argv) > 1 else ""
    {"statusline": lambda: statusline(sys.argv), "hook": hook, "install": install,
     "uninstall": uninstall, "test": test}.get(mode, lambda: print(__doc__))()
