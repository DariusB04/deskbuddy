# DeskBuddy – ESP32 + OLED + LED „sub birou”

Un gadget de birou: ESP32 + ecran OLED 128×64 într-o carcasă printată 3D, ridicată pe 4 picioruțe, cu un LED RGB care luminează biroul de dedesubt.
Butonul de sus schimbă ecranele: **CEAS** → **VREME** → **PC** → **IMPRIMANTA** → **CLAUDE** → **SISTEM** (din pagina de setări alegi care apar).

- **CEAS**: ora cu cifre „dot-matrix”, data, secundele
- **VREME**: acum + 3 zile (Open-Meteo, fără cont), după IP sau orașul ales
- **PC**: temperaturi și load CPU / GPU / RAM de la PC (aplicația de Windows sau scriptul de Linux)
- **IMPRIMANTA**: Bambu Lab (cloud sau LAN): progres, timp rămas, temperaturi; sare singur pe ecran când se termină un print
- **CLAUDE**: ce face Claude Code acum (lucrează / te așteaptă / gata), usage-ul pe 5 ore și 7 zile, plus „Bit”, un roboțel animat
- **SISTEM**: ESP32-ul însuși: load pe nucleu, RAM, WiFi

Apăsare lungă: LED **AUTO** (slab noaptea) / **MAX** / **OFF**.

```
deskbuddy/
├── pc/windows/   aplicația de Windows (C#, .NET 10): sursa; exe-ul vine din Releases
├── pc/           pc_stats_sender.py, claude_bridge.py (Linux)
├── cad/          deskbuddy_cad.py – modelul parametric al carcasei (CadQuery)
└── stl/ step/    piesele carcasei
```

Firmware-ul plăcii se instalează din **[Releases](../../releases)** (sursa lui nu e publică).

---

## 1. Componente și cablaj

- ESP32 DevKit V1 cu 30 de pini (WROOM-32)
- OLED 0.96" SSD1306 I2C
- modul LED RGB KY-016 (catod comun)
- buton tactil 12×12×7.3 cu 4 picioare
- 6× șurub M3×14

| Componentă | Pin componentă | ESP32 |
|---|---|---|
| OLED 128x64 I2C | VCC / GND | 3V3 / GND |
| | SDA / SCL | GPIO21 / GPIO22 |
| Buton tactil 12×12 (4 picioare) | un picior | GPIO27 |
| | piciorul **opus pe diagonală** | GND |
| Modul LED KY-016 | `-` | GND |
| | R / G / B | GPIO25 / 26 / 33 |

KY-016 are de obicei rezistențele pe plăcuță (R1–R3). Dacă locurile lor sunt goale, pune câte o rezistență pe fiecare culoare: R 150 Ω, G și B 68 Ω.

## 2. Firmware

Din ultimul [release](../../releases/latest):
- **`DeskBuddy-flash.bin`**: prima instalare, prin USB. Cel mai simplu din Chrome / Edge, cu [ESP Web Flasher](https://espressif.github.io/esptool-js/): Connect → alegi portul plăcii → fișierul la adresa **0x0** → Program.
  Sau din linia de comandă: `esptool.py --chip esp32 write_flash 0x0 DeskBuddy-flash.bin`
- **`DeskBuddy.ino.bin`**: update-urile de după, prin WiFi: din aplicația de Windows (cu un click) sau din pagina plăcii → **Actualizare firmware**. Setările rămân.

### Setup din browser
- **Prima pornire** (sau WiFi greșit / indisponibil 20 s): pe ecran apare **SETUP**. De pe telefon te conectezi la rețeaua **DeskBuddy-Setup**; pagina se deschide singură, sau intri pe **192.168.4.1**. Alegi WiFi-ul (butonul **Caută**), completezi imprimanta și apeși **Salvează și repornește**.
- **După aceea**, pagina e la **http://deskbuddy.local** sau la IP-ul afișat pe ecranul PC. Acolo schimbi oricând WiFi-ul, imprimanta, ecranele, vremea, orele de noapte, culoarea LED-ului și altele.
- **Forțezi setup-ul** ținând butonul apăsat **3 secunde** când alimentezi placa.
- **Parolă pentru pagină**: opțional, din secțiunea Securitate (utilizator `admin`). E recomandată dacă folosești imprimanta, pentru că pagina permite schimbarea token-ului Bambu. Token-ul și parolele nu sunt afișate niciodată în pagină.

### Bambu Lab
**Cloud (implicit)**: imprimanta rămâne normală, iar Bambu Handy și Bambu Studio merg ca până acum. DeskBuddy doar citește statusul.
În pagină, la **Imprimantă → Cloud → Conectare cu contul Bambu / MakerWorld**: scrii emailul, primești un cod pe email, DeskBuddy ia singur token-ul și lista imprimantelor.
Conexiunea spre serverele Bambu verifică certificatul serverului. Token-ul expiră după ~3 luni: atunci te conectezi din nou din pagină.

**LAN**: direct la imprimantă, fără cloud (trebuie **LAN Only** + **Developer Mode** pe imprimantă; Bambu Handy nu mai merge). Completezi IP-ul și access code-ul.

Codurile `rc` afișate: `-2` = nu ajunge la server / port 8883, `-4` timeout, `4`/`5` user/parolă (token) greșite.

## 3. PC → DeskBuddy

### Windows: aplicația DeskBuddy
Descarci `DeskBuddy-Windows.zip` din [Releases](../../releases/latest) și pornești `DeskBuddy.exe` (nu cere .NET instalat).
Se deschide fereastra DeskBuddy, plus o iconiță în tray (punct verde = placa găsită în rețea, galben = negăsită, dar trimite oricum broadcast).
- **Dispozitiv**: pagina de setări a plăcii, direct în fereastră
- **PC**: ce trimite acum (CPU / GPU / RAM), dacă placa e găsită, IP-ul plăcii (doar dacă routerul blochează broadcast-ul), starea temperaturii CPU
- **Claude Code**: integrarea cu Claude Code, usage-ul 5h / 7 zile, test pentru ecranul CLAUDE
- **Aplicație**: pornește cu Windows (ca admin, fără prompt UAC), pornire direct în tray, **update-uri** (aplicația se actualizează singură; firmware-ul plăcii se urcă prin WiFi cu un click)

Temperatura CPU (Ryzen / Intel) cere **drepturi de admin** și driverul **PawnIO** (`winget install namazso.PawnIO` sau https://pawnio.eu).
Fără ele merg load-ul CPU, RAM-ul și GPU-ul NVIDIA, iar pe ecran temperatura CPU apare `--`. Pagina PC arată ce lipsește.
Cu „Pornește cu Windows” bifat, și dublu-click pe exe pornește aplicația ca admin.

Linie de comandă: `DeskBuddy.exe claude-install` / `claude-uninstall`; `DESKBUDDY_HOST=192.168.1.77` trimite doar la IP-ul ăla, `DESKBUDDY_DEBUG=1` scrie în stderr ce trimite.
Build din sursă (`pc/windows/DeskBuddy/`, .NET 10 SDK): `dotnet publish -c Release -o ..`

### Linux
```
pip install psutil
python3 pc/pc_stats_sender.py --verbose
```
Merge direct (k10temp / coretemp + nvidia-smi). Implicit trimite **broadcast** pe portul 4210; dacă rețeaua blochează broadcast: `--host <IP-ul afișat pe ecranul PC>`.
Jocul și FPS-ul pe ecranul PC: numele din Steam, FPS-ul din MangoHud (în `~/.config/MangoHud/MangoHud.conf`: `autostart_log=1` și `log_duration=0`).

Serviciu user:
```ini
# ~/.config/systemd/user/deskbuddy.service
[Unit]
Description=DeskBuddy PC stats
[Service]
ExecStart=/usr/bin/python3 %h/deskbuddy/pc/pc_stats_sender.py
Restart=always
[Install]
WantedBy=default.target
```
`systemctl --user enable --now deskbuddy`

## 4. Ecranul CLAUDE (Claude Code)

Pe ecran apar ce face Claude Code acum (**lucrează** cu cronometru și unealta folosită, **te așteaptă**, **gata**, **eroare**), usage-ul pe **5 ore** și **7 zile** cu ora de reset, și Bit, care se animă după stare.

- **Windows**: în aplicație, pagina Claude Code → bifezi integrarea
- **Linux**: `python3 pc/claude_bridge.py install` (test: `python3 pc/claude_bridge.py test`, dezinstalare: `uninstall`)

Se adaugă în `~/.claude/settings.json` hook-uri **async** (nu încetinesc Claude Code) și un status line care trimite usage-ul; un status line pe care îl aveai rămâne și se afișează ca înainte. Backup: `settings.json.deskbuddy.bak`.
- usage-ul apare doar cu abonament claude.ai **Pro/Max**, după primul răspuns din sesiune
- usage-ul vine **doar din status line**, pe care îl rulează numai `claude` din terminal; aplicația desktop trimite doar starea

Când Claude cere permisiune sau termină un răspuns lung, DeskBuddy sare pe ecranul CLAUDE, iar LED-ul clipește galben, respectiv verde (se poate opri din pagină).

## 5. Printare

Dimensiuni: **42 × 70 × 50 mm**. Corpul stă pe 4 picioruțe din colțuri, la 3.5 mm de birou, iar LED-ul bate în jos printr-un disc alb subțire.

### Întâi: piesa de test (`fit_test.stl`, ~15 min)
Are bucățile de prindere exact ca în carcasă: OLED-ul pe 4 știfturi (imaginea trebuie să fie centrată în fereastră), ESP32 pe 4 stâlpi (face clic la capătul cu USB-ul), LED-ul pe 2 știfturi și butonul între șine.
`fit_test_esp_led.stl` = doar bucățile ESP32 + LED.

Dacă ceva nu se potrivește, corectezi cotele sus în `cad/deskbuddy_cad.py` și rulezi scriptul (`pip install cadquery`, apoi `python cad/deskbuddy_cad.py`). Scriptul afișează `probleme: niciuna` dacă totul încape.

| Problemă | Parametru |
|---|---|
| imaginea OLED nu e centrată pe verticală | `OLED_AA_OFF` (+ = mută fereastra spre pini) |
| știfturile OLED nu intră / găurile nu se potrivesc | `OLED_HOLE`, `OLED_HOLE_D` |
| ESP32 nu intră pe stâlpi | `ESP_HOLE`, `ESP_PCB` |
| știfturile ESP sunt prea strânse / prea largi | `ESP_HOLE_D` (mai mic = mai lejer) |
| știfturile LED nu intră | `MOD_HOLE_D` (mai mic = știft mai subțire) |
| vrei prindere definitivă | `PEG_STYLE = "heat"` și topești vârful știftului cu letconul |
| switch-ul intră greu / stă lejer | `SW_FIT` |
| găurile KY-016 nu se potrivesc | `MOD_HOLE_SPACING`, `MOD_HOLE_EDGE`, `MOD_LED_EDGE` |
| LED-ul atinge discul alb / e prea sus | `LED_LEN` |

### Piesele
| Piesă | Filament | Orientare | Note |
|---|---|---|---|
| `body` | culoarea ta | **răsturnat, topul pe pat** | fără suporturi. Textura: *Fuzzy skin* → Outer walls, 0.2–0.3 mm |
| `lid` | **aceeași culoare cu corpul** (opac) | fața plată pe pat | stâlpii pentru ESP32 și LED sunt pe el |
| `diffuser` | **alb** | **flanșa pe pat** | pereți 0.8 mm. Se pune de **sus** în capac și iese 2 mm pe dedesubt |
| `oled_clamp` | orice | fața lungă pe pat | bara care ține OLED-ul |
| `button_cap` | culoarea corpului | **capul pe pat** (tija în sus) | se pune din exterior, la final |

Sau importă `step/*.step` direct în Fusion 360.

## 6. Asamblare

1. **OLED**: lipește firele pe pini. Cu corpul răsturnat, pui OLED-ul cu sticla spre fereastră și pinii spre topul carcasei, cu găurile pe cele 4 știfturi. Bagi bara `oled_clamp` de jos, peste spatele PCB-ului, și o strângi cu 2 M3×14: bara urcă și apasă PCB-ul pe stâlpișori.
2. **Buton**: lipești 2 fire pe două picioare opuse pe diagonală. Switch-ul intră între cele 2 șine **din față**, până în opritor. **Capacul butonului îl pui la final, din exterior**, peste tija pătrată.
3. **Difuzorul + modulul LED**: pui difuzorul alb de sus în gaura din capac, apoi modulul cu LED-ul în jos pe cele 2 știfturi.
4. **ESP32** cu **pinii în jos**: pe cei 4 stâlpi, cu USB-ul spre spate, peste modulul LED. Apeși la capătul cu USB-ul până face clic.
5. Capacul intră în corp de jos, 4× M3×14.
