#!/usr/bin/env python3
"""
DeskBuddy – trimite temperaturile / load-ul PC-ului către ESP32 prin UDP.

Windows : temperaturi din LibreHardwareMonitor (Options -> Remote Web Server -> Run, port 8085)
          + psutil pentru load/RAM. GPU NVIDIA și prin nvidia-smi dacă LHM lipsește.
Linux   : psutil.sensors_temperatures() (k10temp / coretemp) + nvidia-smi.
          Jocul care rulează: numele din Steam (AppId), FPS-ul din log-ul MangoHud (scris în timp real),
          dacă în MangoHud.conf sunt autostart_log=1 și log_duration=0.

Instalare:  pip install psutil
Rulare:     python pc_stats_sender.py                 (broadcast pe toate rețelele PC-ului, merge fără IP)
            python pc_stats_sender.py --host 192.168.1.77
            python pc_stats_sender.py --verbose        (vezi ce trimite)
Pornire automată pe Windows: vezi README (pythonw + Task Scheduler).
"""
import argparse
import ipaddress
import json
import os
import re
import shutil
import socket
import subprocess
import sys
import time
import urllib.request

try:
    import psutil
except ImportError:
    psutil = None
    print("[!] pip install psutil  (fără el lipsesc load-ul CPU și RAM-ul)", file=sys.stderr)

NUM = re.compile(r"-?\d+(?:[.,]\d+)?")

CPU_TEMP_NAMES = ["Core (Tctl/Tdie)", "CPU Package", "Package", "Tctl/Tdie", "Core (Tdie)", "CCD1 (Tdie)", "Core Average"]
GPU_TEMP_NAMES = ["GPU Core", "GPU Temperature", "GPU Hot Spot"]


# ------------------------------------------------------------------ LibreHardwareMonitor
def _num(v):
    if v is None:
        return None
    m = NUM.search(str(v))
    return float(m.group(0).replace(",", ".")) if m else None


def _hw_kind(sensor_id, hw_text, hw_img):
    s = (sensor_id or "").lower()
    seg = s.split("/")[1] if s.count("/") >= 2 else ""
    t = f"{seg} {hw_text} {hw_img}".lower()
    if "gpu" in seg or "nvidia" in t or "radeon" in t or "geforce" in t or "ati.png" in t:
        return "gpu"
    if "cpu" in t or "ryzen" in t or "intel core" in t:
        return "cpu"
    if seg in ("ram", "memory") or "ram.png" in t or hw_text.lower().startswith(("generic memory", "total memory", "memory")):
        return "ram"
    return "other"


def _walk(node, out, hw_text="", hw_img="", group=""):
    img = node.get("ImageURL", "") or ""
    text = node.get("Text", "") or ""
    children = node.get("Children") or []
    # nod de hardware: are icon de hardware (cpu.png, nvidia.png, ram.png ...)
    if children and img and not any(k in img for k in ("temperature", "load", "power", "clock", "voltage", "fan", "data", "factor", "throughput", "level", "control", "energy", "computer")):
        hw_text, hw_img = text, img
    if children and node.get("Value", "") == "" and text in (
            "Temperatures", "Load", "Data", "Powers", "Clocks", "Voltages", "Fans", "SmallData", "Throughput"):
        group = text
    if not children and node.get("Value"):
        sid = node.get("SensorId", "")
        stype = (node.get("Type") or "").lower() or {"Temperatures": "temperature", "Load": "load",
                                                     "Data": "data", "SmallData": "smalldata"}.get(group, "")
        if not stype and sid.count("/") >= 3:
            stype = sid.split("/")[-2]
        out.append({"kind": _hw_kind(sid, hw_text, hw_img), "type": stype.lower(),
                    "name": text, "value": _num(node["Value"])})
    for c in children:
        _walk(c, out, hw_text, hw_img, group)


def _pick(sensors, kind, stype, names):
    cand = [s for s in sensors if s["kind"] == kind and s["type"] == stype and s["value"] is not None]
    for n in names:
        for s in cand:
            if s["name"] == n:
                return s["value"]
    return None


def read_lhm(url):
    try:
        with urllib.request.urlopen(url, timeout=1.5) as r:
            tree = json.load(r)
    except Exception:
        return {}
    sensors = []
    _walk(tree, sensors)
    d = {
        "ct": _pick(sensors, "cpu", "temperature", CPU_TEMP_NAMES),
        "cl": _pick(sensors, "cpu", "load", ["CPU Total"]),
        "gt": _pick(sensors, "gpu", "temperature", GPU_TEMP_NAMES),
        "gl": _pick(sensors, "gpu", "load", ["GPU Core"]),
        "rl": _pick(sensors, "ram", "load", ["Memory"]),
        "ru": _pick(sensors, "ram", "data", ["Memory Used", "Used Memory"]),
    }
    avail = _pick(sensors, "ram", "data", ["Memory Available", "Available Memory"])
    if d["ru"] is not None and avail is not None:
        d["rt"] = d["ru"] + avail
    return {k: v for k, v in d.items() if v is not None}


# ------------------------------------------------------------------ fallback-uri
def read_nvidia_smi():
    exe = shutil.which("nvidia-smi")
    if not exe:
        return {}
    try:
        out = subprocess.run([exe, "--query-gpu=temperature.gpu,utilization.gpu", "--format=csv,noheader,nounits"],
                             capture_output=True, text=True, timeout=2,
                             creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0)).stdout
        t, u = [float(x) for x in out.strip().splitlines()[0].split(",")]
        return {"gt": t, "gl": u}
    except Exception:
        return {}


def read_linux_temps():
    if psutil is None or not hasattr(psutil, "sensors_temperatures"):
        return {}
    try:
        temps = psutil.sensors_temperatures()
    except Exception:
        return {}
    for chip in ("k10temp", "coretemp", "zenpower", "cpu_thermal"):
        for e in temps.get(chip, []):
            if e.label in ("Tctl", "Tdie", "Package id 0", "") or chip == "cpu_thermal":
                return {"ct": e.current}
    return {}


def read_psutil():
    if psutil is None:
        return {}
    vm = psutil.virtual_memory()
    return {"cl": psutil.cpu_percent(interval=None), "rl": vm.percent,
            "ru": (vm.total - vm.available) / 2**30, "rt": vm.total / 2**30}


# ------------------------------------------------------------------ joc + FPS (Linux: Steam + MangoHud)
HOME = os.path.expanduser("~")
STEAM_ROOTS = [os.path.join(HOME, ".local/share/Steam"), os.path.join(HOME, ".steam/steam")]
NOT_GAMES = ("Proton", "Steam Linux Runtime", "Steamworks")      # unelte Steam, nu jocuri
_steam_names = {}
_mh_header = {}


def steam_game_name(appid):
    if appid in _steam_names:
        return _steam_names[appid]
    name = None
    libs = []
    for root in STEAM_ROOTS:
        vdf = os.path.join(root, "steamapps", "libraryfolders.vdf")
        try:
            libs += re.findall(r'"path"\s+"([^"]+)"', open(vdf, encoding="utf-8").read())
        except OSError:
            pass
        libs.append(root)
    for lib in dict.fromkeys(libs):
        try:
            acf = open(os.path.join(lib, "steamapps", f"appmanifest_{appid}.acf"), encoding="utf-8").read()
            m = re.search(r'"name"\s+"([^"]+)"', acf)
            if m:
                name = m.group(1)
                break
        except OSError:
            pass
    _steam_names[appid] = name
    return name


def running_steam_game():
    """Numele jocului Steam pornit acum (Steam îl lansează cu "SteamLaunch AppId=<id>")."""
    for pid in os.listdir("/proc"):
        if not pid.isdigit():
            continue
        try:
            cmd = open(f"/proc/{pid}/cmdline", "rb").read()
        except OSError:
            continue
        m = re.search(rb"AppId=(\d+)", cmd)
        if m:
            name = steam_game_name(int(m.group(1)))
            if name and not name.startswith(NOT_GAMES):
                return name
    return None


def mangohud_folder():
    path = os.environ.get("MANGOHUD_CONFIGFILE") or os.path.join(HOME, ".config/MangoHud/MangoHud.conf")
    try:
        for line in open(path, encoding="utf-8"):
            if line.strip().startswith("output_folder="):
                return os.path.expanduser(line.split("=", 1)[1].strip())
    except OSError:
        pass
    return None


def mangohud_fps(folder):
    """FPS + frametime din ultimul log MangoHud (doar dacă e scris acum), plus executabilul din numele fișierului."""
    if not folder or not os.path.isdir(folder):
        return None
    try:
        logs = [os.path.join(folder, f) for f in os.listdir(folder) if f.endswith(".csv") and not f.endswith("_summary.csv")]
        f = max(logs, key=os.path.getmtime) if logs else None
        if not f or time.time() - os.path.getmtime(f) > 3:
            return None
        if f not in _mh_header:                  # rândul 3 = numele coloanelor (fps, frametime, ...)
            with open(f, encoding="utf-8", errors="replace") as fh:
                head = [next(fh, "") for _ in range(3)]
            _mh_header[f] = head[2].strip().split(",")
        cols = _mh_header[f]
        with open(f, "rb") as fh:
            fh.seek(0, 2)
            fh.seek(max(0, fh.tell() - 4096))
            lines = fh.read().decode("utf-8", "replace").splitlines()[1:]
        rows = [l.split(",") for l in lines if l and l[0].isdigit()][-10:]     # ~1 s la log_interval=100
        fps = [float(r[cols.index("fps")]) for r in rows if len(r) == len(cols)]
        ft = [float(r[cols.index("frametime")]) for r in rows if len(r) == len(cols)]
        if not fps:
            return None
        exe = re.sub(r"_\d{4}-\d\d-\d\d_\d\d-\d\d-\d\d$", "", os.path.basename(f)[:-4])
        return {"fps": sum(fps) / len(fps), "ft": sum(ft) / len(ft) if ft else None, "exe": exe}
    except (OSError, ValueError, IndexError):
        return None


_game_cache = {"at": 0.0, "name": None}


def read_game():
    if not sys.platform.startswith("linux"):
        return {}
    if time.time() - _game_cache["at"] > 2:                  # /proc se scanează la 2 s
        _game_cache["at"] = time.time()
        _game_cache["name"] = running_steam_game()
    mh = mangohud_fps(mangohud_folder())
    name = _game_cache["name"] or (mh and mh["exe"].removesuffix(".exe"))
    if not name:
        return {}
    d = {"g": name[:24]}
    if mh:
        d["fps"] = round(mh["fps"])
        if mh["ft"] is not None:
            d["ft"] = round(mh["ft"], 1)
    return d


def collect(args):
    d = read_psutil()
    if sys.platform.startswith("win"):
        lhm = read_lhm(args.lhm_url)
        d.update({k: v for k, v in lhm.items() if k in ("ct", "gt", "gl")})   # load/RAM: psutil e mai stabil
        if "cl" not in d and "cl" in lhm:
            d["cl"] = lhm["cl"]
    else:
        d.update(read_linux_temps())
    if "gt" not in d:
        d.update(read_nvidia_smi())
    d = {k: round(v, 1) for k, v in d.items() if v is not None}
    d.update(read_game())
    return d


def broadcast_targets():
    """Adresa de broadcast a fiecărei rețele. 255.255.255.255 pleacă doar pe ruta implicită
    (ex. ethernet), deci nu ajunge la DeskBuddy dacă el e pe WiFi-ul PC-ului."""
    out = []
    if psutil:
        for addrs in psutil.net_if_addrs().values():
            for a in addrs:
                if a.family != socket.AF_INET or not a.netmask:
                    continue
                try:
                    net = ipaddress.IPv4Network(f"{a.address}/{a.netmask}", strict=False)
                except ValueError:
                    continue
                if net.is_loopback or net.prefixlen >= 31:      # lo, tailscale
                    continue
                b = str(net.broadcast_address)
                if b not in out:
                    out.append(b)
    return out or ["255.255.255.255"]


USAGE_CACHE = os.path.join(os.path.expanduser("~"), ".cache", "deskbuddy", "usage.json")


def claude_usage_packet():
    """Ultimul usage Claude (5h / 7 zile) salvat de claude_bridge.py, ca DeskBuddy să-l aibă și după repornire."""
    try:
        with open(USAGE_CACHE, encoding="utf-8") as f:
            d = json.load(f)
    except (OSError, ValueError):
        return None
    d = {k: d[k] for k in ("m", "h", "hr", "w", "wr", "at") if k in d}
    return json.dumps(dict(d, t="cu"), separators=(",", ":")).encode() if d else None


def main():
    ap = argparse.ArgumentParser(description="DeskBuddy PC stats -> ESP32 (UDP)")
    ap.add_argument("--host", default="", help="IP-ul ESP32 (implicit: broadcast pe toate rețelele)")
    ap.add_argument("--port", type=int, default=4210)
    ap.add_argument("--interval", type=float, default=1.0)
    ap.add_argument("--lhm-url", default="http://127.0.0.1:8085/data.json")
    ap.add_argument("--verbose", action="store_true")
    args = ap.parse_args()

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
    if psutil:
        psutil.cpu_percent(interval=None)   # primul apel întoarce 0, îl "încălzim"

    warned = False
    targets, targets_at = [], 0.0
    usage_at = 0.0
    while True:
        if args.host:
            targets = [args.host]
        elif time.time() - targets_at > 30:            # rețelele se pot schimba (WiFi pornit/oprit)
            targets, targets_at = broadcast_targets(), time.time()
        d = collect(args)
        if sys.platform.startswith("win") and "ct" not in d and not warned:
            print("[!] Nu găsesc temperatura CPU. Pornește LibreHardwareMonitor ca admin și activează "
                  "Options -> Remote Web Server -> Run (port 8085).", file=sys.stderr)
            warned = True
        payload = json.dumps(d, separators=(",", ":")).encode()
        for host in targets:
            try:
                sock.sendto(payload, (host, args.port))
            except OSError as e:
                print(f"[!] send {host}:", e, file=sys.stderr)
        if time.time() - usage_at > 30:
            usage_at = time.time()
            cu = claude_usage_packet()
            for host in targets if cu else []:
                try:
                    sock.sendto(cu, (host, args.port))
                except OSError:
                    pass
        if args.verbose:
            print(",".join(targets), payload.decode())
        time.sleep(args.interval)


if __name__ == "__main__":
    main()
