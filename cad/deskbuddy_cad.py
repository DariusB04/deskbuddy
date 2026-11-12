"""
DeskBuddy v3 – carcasă compactă, ridicată pe picioruțe, lumină în jos (CadQuery)
===============================================================================
Rulează:  python deskbuddy_cad.py      ->  ../stl/*.stl , ../step/*.step , ../preview/*.stl

Coordonate: X = lățime (centrat), Y = adâncime (0 = față jos), Z = sus (0 = biroul).

Piese:
  body        – corpul cu 4 picioruțe în colțuri. Se printează RĂSTURNAT (topul pe pat).
                Pe interiorul feței: 4 stâlpișori cu știfturi pentru găurile OLED-ului.
  oled_clamp  – bara care apasă OLED-ul pe stâlpișori (2× M3x14 în coloanele din lateral).
  lid         – capacul de jos (culoarea corpului) cu stâlpii pentru ESP32 și modulul LED.
  diffuser    – căpăcel ALB (pereți 0.8 mm) pus de SUS în capac; iese 2 mm sub capac, LED-ul intră în el.
  button_cap  – capacul butonului (microswitch 6x6x5).
  fit_test    – piesă de test (~15 min): fereastra + stâlpii OLED, ESP32 și LED, ca să verifici cotele
                înainte să printezi corpul.

Șuruburi: 6× M3x14 (4 capac + 2 bară OLED). ESP32 și modulul LED NU au șuruburi: stau pe știfturi cu clic
(știft despicat cu cârlig) – le apeși până fac clic. Dacă vrei fix definitiv: PEG_STYLE = "heat" și topești vârful
știftului cu letconul (îl lați ca o ciupercă).

MĂSOARĂ-ȚI PLĂCUȚELE și corectează secțiunile OLED / ESP32 / LED de mai jos. Modelul se recalculează singur.
"""
import math
import os
import cadquery as cq

# ================================================================== CARCASA
W = 42.0                # lățime exterioară
D = 70.0                # adâncime la bază
H = 50.0                # înălțime totală, de la birou
GAP = 3.5               # cât de ridicat e corpul (loc pentru lumină)
LEG = 7.0               # lungimea picioruțelor din colțuri
ANGLE = 18.0            # înclinarea feței din față
T = 2.0                 # pereți / top
R = 2.5                 # rotunjire exterioară
FIT = 0.25
MIN_SKIN = 1.2          # perete minim între o gaură de șurub și exterior

LID_T = 1.6
DIFF_T = 0.8            # grosimea difuzorului (4 straturi la 0.2)
DIFF_D = 14.0           # flanșa difuzorului (stă în locașul de sus al capacului)
CUP_OD = 9.0            # căpăcelul care iese sub capac
CUP_DROP = 2.0          # cât iese sub capac (sub el rămân GAP - CUP_DROP mm până la birou)

# șuruburi M3x14 autofiletante
SCREW_LEN = 14.0
SCREW_HOLE_D = 2.6      # în plastic (se autofiletează)
SCREW_CLEAR_D = 3.4     # prin piesa strânsă
BOSS_R = 3.2

# ================================================================== OLED 0.96" (SSD1306, 4 pini)
#  Privit din față, pinii SUS. Cotele sunt de la centrul PCB-ului.
OLED_PCB = (27.3, 27.8)         # lățime, înălțime PCB
OLED_PCB_T = 1.2
OLED_HOLE = (23.5, 23.8)        # distanța între centrele găurilor (orizontal, vertical)
OLED_HOLE_D = 2.0               # diametrul găurilor din PCB (știftul iese cu 0.25 mai subțire)
OLED_GLASS = (26.7, 19.3)
OLED_GLASS_T = 1.7              # sticla + banda de sub ea (= înălțimea stâlpișorilor)
OLED_GLASS_OFF = -0.3           # centrul sticlei față de centrul PCB (+ = spre pini)
OLED_AA = (21.74, 10.86)        # zona care se aprinde
OLED_AA_OFF = 2.0               # centrul zonei aprinse față de centrul PCB (+ = spre pini)
WIN_MARGIN = 0.7                # cât e fereastra mai mare decât zona aprinsă, pe fiecare parte
WIN_CHAMFER = 0.8
OLED_S = 28.5                   # centrul PCB-ului OLED, măsurat pe pantă de la birou (mm)
OLED_STANDOFF_R = 1.7

# ================================================================== ESP32 (DevKit V1 30 pini)
ESP_PCB = (28.2, 51.8)          # lățime, lungime
ESP_HOLE = (23.8, 46.3)         # distanța între centrele găurilor (pe lățime, pe lungime) – măsurat din poză
ESP_HOLE_D = 2.8                # diametrul găurilor din placă
ESP_PIN_ROWS = 25.4             # distanța între rândurile de pini
ESP_PINS = 15                   # pini pe rând
ESP_PIN_LEN = 8.5               # cât ies pinii sub placă (+ fire lipite)
ESP_STANDOFF_R = 2.5

# ================================================================== modul LED KY-016
MOD = (15.0, 20.0)              # lățime (marginea cu găurile), lungime (spre pini)
MOD_T = 1.6
MOD_HOLE_SPACING = 10.0         # între centrele celor 2 găuri
MOD_HOLE_EDGE = 2.5             # marginea cu găurile -> centrul găurilor
MOD_LED_EDGE = 8.5              # aceeași margine -> centrul LED-ului
LED_LEN = 14.0                  # de la fața plăcuței până în vârful LED-ului (al tău ~13-14)
MOD_TOP_PARTS = 1.5             # cât ies lipiturile pe spatele plăcuței
MOD_HOLE_D = 3.0
LED_PIN_D = 1.8                 # știfturile pe care stă modulul LED
PEG_STYLE = "snap"              # "snap" = știft despicat cu cârlig (clic) | "heat" = știft lung, îl topești cu letconul
LED_Y = 24.0                    # poziția LED-ului (y)

# ================================================================== buton
# buton tactil 12x12x7.3 cu 4 picioare (contactul e pe diagonală)
BTN_Y = 50.0
SW = 12.0                       # corpul switch-ului
SW_H = 7.3                      # de la fundul corpului până în vârful butonașului
SW_BODY_H = 3.6                 # doar corpul pătrat
SW_FIT = 0.3
BTN_HOLE_D = 8.4
CAP_STEM_D = 8.0
CAP_PROTRUDE = 1.2
CAP_FLANGE_D = 11.0
CAP_FLANGE_T = 1.2
CAP_NUB = (6.5, 0.6)            # pastila care apasă pe tija pătrată a butonașului
SW_STEM = 3.6                   # tija pătrată din vârful butonașului
# capacul butonului se pune DIN EXTERIOR, presat pe tija pătrată (ca un capac de tastă)
KNOB_D = 11.0                   # capul rotund de deasupra carcasei
KNOB_T = 2.2
KNOB_GAP = 0.8                  # cât stă capul deasupra carcasei (cursa)
KNOB_SHAFT_D = 7.4              # trece prin gaura de 8.4 din top
SOCKET = 3.65                   # locașul pătrat (tija 3.6 -> strâns). Dacă intră prea greu: 3.75, prea lejer: 3.55
SOCKET_DEPTH = 1.6
SW_GAP = 0.2

# ================================================================== derivate
ta, ca, sa = (math.tan(math.radians(ANGLE)), math.cos(math.radians(ANGLE)), math.sin(math.radians(ANGLE)))
FRONT_IN = T / ca
Z_LID_TOP = GAP + LID_T
Z_TOP_IN = H - T
BOSS_H = SCREW_LEN - LID_T + 1.0

LED_TIP_Z = GAP - CUP_DROP + DIFF_T + 0.3          # vârful LED-ului, în căpăcelul alb
MOD_Z = LED_TIP_Z + LED_LEN                        # fața de jos a plăcuței LED
ESP_Z = 22.3                                       # fața de jos a plăcii ESP32 (fix: corpul e deja printat, USB-ul e la înălțimea asta)
assert MOD_Z + MOD_T + MOD_TOP_PARTS + 1.0 <= ESP_Z, "LED-ul e prea lung pentru ESP_Z"
USB_CUT = (12.0, 10.0)
USB_Z = ESP_Z + 1.6 + 1.6
esp_y0 = D - T - ESP_PCB[1] - 0.2                  # capătul din față al plăcii ESP32 (USB lipit de spate)
esp_yc = esp_y0 + ESP_PCB[1] / 2
ESP_HOLES = [(sx * ESP_HOLE[0] / 2, esp_yc + sy * ESP_HOLE[1] / 2) for sx in (-1, 1) for sy in (-1, 1)]
mod_y0 = LED_Y - MOD_LED_EDGE
MOD_HOLES = [(sx * MOD_HOLE_SPACING / 2, mod_y0 + MOD_HOLE_EDGE) for sx in (-1, 1)]

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
HOLES_IN_BODY = []      # (x, y, z0, z1, r) – pentru verificarea grosimii peretelui


def prof(y1, z0, z1, front_off):
    return [(front_off + z0 * ta, z0), (y1, z0), (y1, z1), (front_off + z1 * ta, z1)]


def ext(pts, width):
    return cq.Workplane("YZ").polyline(pts).close().extrude(width / 2.0, both=True)


def front_plane(s, depth=0.0):
    """plan paralel cu fața din față; s = distanța pe pantă de la birou; depth>0 = spre interior.
    local +x = dreapta, local +y = în sus pe pantă, normala = spre exterior."""
    return cq.Plane(origin=(0, s * sa + depth * ca, s * ca - depth * sa), xDir=(1, 0, 0), normal=(0, -ca, sa))


def box(x0, x1, y0, y1, z0, z1):
    x0, x1 = sorted((x0, x1)); y0, y1 = sorted((y0, y1)); z0, z1 = sorted((z0, z1))
    return cq.Workplane("XY").box(x1 - x0, y1 - y0, z1 - z0).translate(((x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2))


def cyl(x, y, z0, z1, r):
    return cq.Workplane("XY", origin=(x, y, z0)).circle(r).extrude(z1 - z0)


def slab(front_off, thick, z0, z1, width):
    return ext([(front_off + z0 * ta, z0), (front_off + z0 * ta + thick, z0),
                (front_off + z1 * ta + thick, z1), (front_off + z1 * ta, z1)], width)


# ================================================================== BODY
outer_sharp = ext(prof(D, 0, H, 0.0), W)
outer = outer_sharp.edges("not(<Z)").fillet(R)
body = outer.cut(ext(prof(D - T, -1, Z_TOP_IN, FRONT_IN), W - 2 * T))

# picioruțe: tai pereții sub GAP, mai puțin colțurile
under = box(-W, W, -5, D + 5, -1, GAP)
for sx in (-1, 1):
    for y0, y1 in ((-5, LEG), (D - LEG, D + 5)):
        under = under.cut(box(sx * (W / 2 - LEG), sx * W, y0, y1, -2, GAP + 1))
body = body.cut(under)

# bosaje pentru capac – cele din față mutate în spate cât să nu iasă gaura prin fața înclinată
hole_top = Z_LID_TOP - 0.5 + BOSS_H
bx = W / 2 - T - 1.5
by_f = max(FRONT_IN + Z_LID_TOP * ta + BOSS_R + 0.4,
           hole_top * ta + (MIN_SKIN + 0.2) / ca + SCREW_HOLE_D / 2)
by_r = D - T - 1.5
SCREWS = [(-bx, by_f), (bx, by_f), (-bx, by_r), (bx, by_r)]
for (x, y) in SCREWS:
    cx = math.copysign(W / 2 - T + 0.5, x)
    cy = (D - T + 0.5) if y > D / 2 else y - BOSS_R
    boss = cyl(x, y, Z_LID_TOP, Z_LID_TOP + BOSS_H, BOSS_R)
    gus = (cq.Workplane("XY", origin=(x, y, Z_LID_TOP + BOSS_H)).circle(BOSS_R)
           .workplane(offset=BOSS_R * 1.4).center(cx - x, cy - y).circle(0.4).loft())
    body = body.union(boss.union(gus).intersect(outer_sharp))
    body = body.cut(cyl(x, y, Z_LID_TOP - 0.5, hole_top, SCREW_HOLE_D / 2))
    HOLES_IN_BODY.append((x, y, Z_LID_TOP, hole_top, SCREW_HOLE_D / 2))

# ---- OLED: fereastră centrată pe zona aprinsă
s_win = OLED_S + OLED_AA_OFF
WIN = (OLED_AA[0] + 2 * WIN_MARGIN, OLED_AA[1] + 2 * WIN_MARGIN)
win = (cq.Workplane(front_plane(s_win, -0.01)).rect(WIN[0] + 2 * WIN_CHAMFER, WIN[1] + 2 * WIN_CHAMFER)
       .workplane(offset=-WIN_CHAMFER).rect(WIN[0], WIN[1]).loft())
body = body.cut(win).cut(cq.Workplane(front_plane(s_win, WIN_CHAMFER - 0.01)).rect(*WIN).extrude(-(T + 1)))

# ---- OLED: 4 stâlpișori pe interiorul feței + știfturi prin găurile PCB-ului
PEG_D = OLED_HOLE_D - 0.25
OLED_HOLES = [(sx * OLED_HOLE[0] / 2, sy * OLED_HOLE[1] / 2) for sx in (-1, 1) for sy in (-1, 1)]
for (hx, hy) in OLED_HOLES:
    so = cq.Workplane(front_plane(OLED_S + hy, T - 0.2)).center(hx, 0).circle(OLED_STANDOFF_R).extrude(-(OLED_GLASS_T + 0.2))
    peg = (cq.Workplane(front_plane(OLED_S + hy, T + OLED_GLASS_T - 0.01)).center(hx, 0).circle(PEG_D / 2)
           .extrude(-(OLED_PCB_T + 0.8)))
    body = body.union(so).union(peg)

# ---- bara OLED (pană): urcă când strângi șuruburile și apasă PCB-ul pe stâlpișori
OLED_BACK = T + OLED_GLASS_T + OLED_PCB_T      # spatele PCB-ului, de la fața exterioară
PRELOAD = 0.3
s_bar = (OLED_S + OLED_HOLE[1] / 2 - 9.0, OLED_S + OLED_HOLE[1] / 2 - 3.5)   # sub pini și găurile de sus
CLAMP_Z = (s_bar[0] * ca - OLED_BACK * sa, s_bar[1] * ca - OLED_BACK * sa)
CLAMP_D = 7.0
clamp_fo = (OLED_BACK - PRELOAD) / ca
clamp = slab(clamp_fo, CLAMP_D, CLAMP_Z[0], CLAMP_Z[1], W - 2 * T - 2 * FIT)
clamp = clamp.cut(slab(clamp_fo - 1, 1 + 1.2 / ca, CLAMP_Z[0] - 1, CLAMP_Z[1] + 1, 17.0))  # nu apasă pe mijloc
CL_X = W / 2 - T - 2.4
CL_Y = clamp_fo + CLAMP_Z[1] * ta + CLAMP_D / 2 + 0.3
clamp_h = CLAMP_Z[1] - CLAMP_Z[0]
for sx in (-1, 1):
    clamp = clamp.cut(cyl(sx * CL_X, CL_Y, CLAMP_Z[0] - 1, CLAMP_Z[1] + 1, SCREW_CLEAR_D / 2))
    col = box(sx * (OLED_PCB[0] / 2 + 0.7), sx * (W / 2 - T + 0.5), CL_Y - 3, CL_Y + 3, CLAMP_Z[1], Z_TOP_IN + 0.01)
    body = body.union(col.intersect(outer_sharp))
    z1 = CLAMP_Z[1] + (SCREW_LEN - clamp_h) + 0.5
    body = body.cut(cyl(sx * CL_X, CL_Y, CLAMP_Z[1] - 0.5, z1, SCREW_HOLE_D / 2))
    HOLES_IN_BODY.append((sx * CL_X, CL_Y, CLAMP_Z[1], z1, SCREW_HOLE_D / 2))

# ---- USB în spate
usb = cq.Workplane("XZ", origin=(0, D + 1, USB_Z)).rect(*USB_CUT).extrude(T + 2).edges("|Y").fillet(2.0)
body = body.cut(usb)

# ---- buton sus + locaș pentru switch-ul 12x12: 2 șine laterale; switch-ul intră glisat din FAȚĂ până în opritor.
#      Picioarele (față/spate) trec liber printre șine.
body = body.cut(cyl(0, BTN_Y, Z_TOP_IN - 1, H + 1, BTN_HOLE_D / 2))
sw_top = Z_TOP_IN - CAP_FLANGE_T - CAP_NUB[1] - SW_GAP          # vârful butonașului
sw_bot = sw_top - SW_H                                          # fundul corpului switch-ului
RAIL_T, LEDGE_W, LEDGE_T = 1.6, 1.8, 1.4
half = SW / 2 + SW_FIT
for sx in (-1, 1):
    body = body.union(box(sx * half, sx * (half + RAIL_T), BTN_Y - half - 2, BTN_Y + half + RAIL_T,
                          sw_bot - LEDGE_T, Z_TOP_IN + 0.01))                                 # șina
    body = body.union(box(sx * (half - LEDGE_W), sx * (half + 0.01), BTN_Y - half - 2, BTN_Y + half + RAIL_T,
                          sw_bot - LEDGE_T, sw_bot))                                         # raftul de sub switch
body = body.union(box(-half - RAIL_T, half + RAIL_T, BTN_Y + half, BTN_Y + half + RAIL_T,
                      sw_bot - LEDGE_T, Z_TOP_IN + 0.01)
                  .cut(box(-half + LEDGE_W, half - LEDGE_W, BTN_Y + half - 1, BTN_Y + half + RAIL_T + 1,
                           sw_bot - LEDGE_T - 1, sw_bot + SW_BODY_H)))                       # opritor spate (fereastră pt. picioare)
plat_top = sw_bot

# ================================================================== LID
lid_front = FRONT_IN + Z_LID_TOP * ta + FIT
lid = box(-(W / 2 - T - FIT), W / 2 - T - FIT, lid_front, D - T - FIT, GAP, Z_LID_TOP).edges("|Z").fillet(1.0)
for (x, y) in SCREWS:
    lid = lid.cut(cyl(x, y, GAP - 1, Z_LID_TOP + 1, SCREW_CLEAR_D / 2))


def peg(x, y, z, hole_d, pcb_t=1.6):
    """știft pe un stâlp care se termină la z (fața de jos a plăcii)."""
    d = hole_d - 0.25
    if PEG_STYLE == "heat":
        return cyl(x, y, z - 0.01, z + pcb_t + 2.0, d / 2)
    p = cyl(x, y, z - 0.01, z + pcb_t + 0.15, d / 2)
    barb = (cq.Workplane("XY", origin=(x, y, z + pcb_t + 0.15)).circle((hole_d + 0.35) / 2)
            .workplane(offset=1.3).circle((hole_d - 0.8) / 2).loft())
    p = p.union(barb)
    return p.cut(box(x - 0.45, x + 0.45, y - 3, y + 3, z - 2.0, z + pcb_t + 2))            # fanta = arc


def pin(x, y, z, d, h):
    return cyl(x, y, z - 0.01, z + h, d / 2).faces(">Z").edges().chamfer(0.3)


def standoffs(part, holes, z_top, r, hole_d, snap):
    for (x, y), sn in zip(holes, snap):
        part = part.union(cyl(x, y, Z_LID_TOP - 0.01, z_top, r))
        if sn:
            part = part.cut(box(x - 0.45, x + 0.45, y - r - 1, y + r + 1, z_top - 2.0, z_top + 0.1))   # fanta continuă în stâlp
            part = part.union(peg(x, y, z_top, hole_d))
        else:
            part = part.union(pin(x, y, z_top, hole_d - 0.6, 1.6 + 2.5))
    return part


# ESP32: clic la capătul cu USB (spate), știfturi simple în față (iartă ±0.3 mm la distanța dintre găuri)
lid = standoffs(lid, ESP_HOLES, ESP_Z, ESP_STANDOFF_R, ESP_HOLE_D, [y > esp_yc for (x, y) in ESP_HOLES])
# LED: știfturi simple care urcă până sub ESP32 -> plăcuța nu poate ieși (ESP-ul o ține de sus)
for (x, y) in MOD_HOLES:
    lid = lid.union(cyl(x, y, Z_LID_TOP - 0.01, MOD_Z, 2.8))
    lid = lid.union(cyl(x, y, MOD_Z - 0.01, ESP_Z - 0.5, LED_PIN_D / 2).faces(">Z").edges().chamfer(0.3))

# lumina: gaură prin capac pentru căpăcelul alb + locaș sus pentru flanșa lui (stă singur, nu cade)
lid = lid.cut(cyl(0, LED_Y, GAP - 1, Z_LID_TOP + 1, (CUP_OD + 0.3) / 2))
lid = lid.cut(cyl(0, LED_Y, Z_LID_TOP - DIFF_T, Z_LID_TOP + 1, (DIFF_D + 0.3) / 2))

diffuser = cyl(0, LED_Y, Z_LID_TOP - DIFF_T, Z_LID_TOP, DIFF_D / 2)
diffuser = diffuser.union(cyl(0, LED_Y, GAP - CUP_DROP, Z_LID_TOP, CUP_OD / 2))
diffuser = diffuser.cut(cyl(0, LED_Y, GAP - CUP_DROP + DIFF_T, Z_LID_TOP + 1, CUP_OD / 2 - DIFF_T))

# ================================================================== BUTTON CAP
# coordonate locale: z = 0 = vârful tijei pătrate a switch-ului
knob_bot = (H + KNOB_GAP) - sw_top
cap = (cq.Workplane("XY", origin=(0, 0, knob_bot)).circle(KNOB_D / 2).extrude(KNOB_T)
       .faces(">Z").edges().fillet(0.9))
cap = cap.union(cyl(0, 0, -SOCKET_DEPTH, knob_bot + 0.01, KNOB_SHAFT_D / 2))
cap = cap.cut(box(-SOCKET / 2, SOCKET / 2, -SOCKET / 2, SOCKET / 2, -SOCKET_DEPTH - 1, 0))
cap_placed = cap.translate((0, BTN_Y, sw_top))

# ================================================================== componente (doar pt. verificare / preview)
esp = box(-ESP_PCB[0] / 2, ESP_PCB[0] / 2, esp_y0, esp_y0 + ESP_PCB[1], ESP_Z, ESP_Z + 1.6)
for (x, y) in ESP_HOLES:
    esp = esp.cut(cyl(x, y, ESP_Z - 1, ESP_Z + 3, ESP_HOLE_D / 2 + 0.2))
esp = esp.union(box(-9, 9, esp_y0, esp_y0 + 25.5, ESP_Z + 1.6, ESP_Z + 4.8))              # modul WROOM (antena în față)
esp = esp.union(box(-4, 4, D - T - 5, D - T + 0.6, ESP_Z + 1.6, ESP_Z + 4.8))             # mufa USB
pin_len = ESP_PINS * 2.54
for sx in (-1, 1):                                                                       # pinii + fire lipite
    esp = esp.union(box(sx * ESP_PIN_ROWS / 2 - 1.27, sx * ESP_PIN_ROWS / 2 + 1.27,
                        esp_yc - pin_len / 2, esp_yc + pin_len / 2, ESP_Z - ESP_PIN_LEN - 2.0, ESP_Z))

oled = (cq.Workplane(front_plane(OLED_S + OLED_GLASS_OFF, T)).rect(OLED_GLASS[0] - 0.3, OLED_GLASS[1] - 0.3)
        .extrude(-(OLED_GLASS_T - 0.05)))
pcb = cq.Workplane(front_plane(OLED_S, T + OLED_GLASS_T)).rect(*OLED_PCB).extrude(-OLED_PCB_T)
for (hx, hy) in OLED_HOLES:
    pcb = pcb.cut(cq.Workplane(front_plane(OLED_S + hy, T)).center(hx, 0).circle(OLED_HOLE_D / 2).extrude(-6))
oled = oled.union(pcb)
oled = oled.union(cq.Workplane(front_plane(OLED_S - 2.0, OLED_BACK)).rect(13.0, 12.0).extrude(-1.2))   # componente
oled = oled.union(cq.Workplane(front_plane(OLED_S + OLED_PCB[1] / 2 - 1.8, OLED_BACK))
                  .rect(10.5, 2.6).extrude(-10))                                                       # pini + fire
oled_view = cq.Workplane(front_plane(OLED_S + OLED_AA_OFF, T - 0.05)).rect(*OLED_AA).extrude(-0.1)   # zona aprinsă

mod_y1 = mod_y0 + MOD[1]
led = box(-MOD[0] / 2, MOD[0] / 2, mod_y0, mod_y1, MOD_Z, MOD_Z + MOD_T)
for (x, y) in MOD_HOLES:
    led = led.cut(cyl(x, y, MOD_Z - 1, MOD_Z + 3, MOD_HOLE_D / 2 + 0.25))
led = led.union(cyl(0, LED_Y, LED_TIP_Z + 2.5, MOD_Z, 2.5))
led = led.union(cq.Workplane("XY", origin=(0, LED_Y, LED_TIP_Z + 2.5)).sphere(2.5).intersect(
    box(-3, 3, LED_Y - 3, LED_Y + 3, LED_TIP_Z, LED_TIP_Z + 2.5)))
led = led.union(cyl(0, LED_Y, MOD_Z - 2.0, MOD_Z - 1.0, 2.9))                                 # gulerul LED-ului
led = led.union(box(-6, 6, mod_y0 + 5.5, mod_y0 + 15, MOD_Z + MOD_T, MOD_Z + MOD_T + MOD_TOP_PARTS))  # lipituri
led = led.union(box(-5.5, 5.5, mod_y1 - 2.5, mod_y1, MOD_Z - 2.5, MOD_Z))                     # header în unghi
led = led.union(box(-5.5, 5.5, mod_y1, mod_y1 + 8.0, MOD_Z - 2.0, MOD_Z - 0.5))              # pini + fire

switch = box(-SW / 2, SW / 2, BTN_Y - SW / 2, BTN_Y + SW / 2, sw_bot, sw_bot + SW_BODY_H)
switch = switch.union(cyl(0, BTN_Y, sw_bot + SW_BODY_H - 0.01, sw_top - 2.0, 3.4))           # butonașul (rotund)
switch = switch.union(box(-SW_STEM / 2, SW_STEM / 2, BTN_Y - SW_STEM / 2, BTN_Y + SW_STEM / 2,
                          sw_top - 2.01, sw_top))                                            # tija pătrată
for sy in (-1, 1):
    for sx in (-1, 1):                                                                       # picioarele (față/spate) + fire
        switch = switch.union(box(sx * 2.5 - 0.4, sx * 2.5 + 0.4, BTN_Y + sy * 6.25 - 0.4, BTN_Y + sy * 6.25 + 0.4,
                                  sw_bot - 6.0, sw_bot + 0.5))
components = {"esp32": esp, "oled": oled, "led": led, "switch": switch}


def vol(a, b):
    try:
        return a.intersect(b).val().Volume()
    except Exception:
        return 0.0


def check():
    solids = {"body": body, "lid": lid, "cap": cap_placed, "clamp": clamp, **components}
    allowed = ({"oled", "clamp"}, {"oled", "body"})   # bara apasă 0.3 mm; știfturile trec prin găurile PCB
    names = list(solids)
    bad = []
    for i, a in enumerate(names):
        for b in names[i + 1:]:
            v = vol(solids[a], solids[b])
            if v > 0.3 and {a, b} not in allowed:
                bad.append((a, b, round(v, 1)))
    # OLED vs corp: doar știfturile au voie să treacă prin PCB, iar ele trec prin găuri -> volum ~0
    v = vol(body, oled)
    if v > 0.3:
        bad.append(("body", "oled", round(v, 1)))
    for (x, y, z0, z1, r) in HOLES_IN_BODY:
        out = cyl(x, y, z0, z1, r + MIN_SKIN).cut(outer_sharp)
        try:
            v = out.val().Volume()
        except Exception:
            v = 0.0
        if v > 0.05:
            bad.append(("perete subțire la gaura", (round(x, 1), round(y, 1)), round(v, 2)))
    if vol(clamp.translate((0, 0, -1.5)), oled) > 0.01:
        bad.append(("bara nu intră liberă", "", 0))
    if vol(clamp.translate((0, 0, -1.5)), body) > 0.01:
        bad.append(("bara se lovește de corp când intră", "", 0))
    hole = cq.Workplane(front_plane(s_win, -0.5)).rect(*WIN).extrude(-(T + 2.0))
    if oled_view.cut(hole).val().Volume() > 0.01:
        bad.append(("zona aprinsă iese din fereastră", "", 0))
    return bad


# ================================================================== FIT TEST (printezi plat, ~15 min)
def fit_test_esp_led():
    pl = 2.0
    ex, ey = ESP_HOLE[0] + 8, ESP_HOLE[1] + 8
    e = box(-ex / 2, ex / 2, -ey / 2, ey / 2, 0, pl).cut(box(-ex / 2 + 5, ex / 2 - 5, -ey / 2 + 5, ey / 2 - 5, -1, pl + 1))
    e = e.union(box(-3, 3, ey / 2, ey / 2 + 4, 0, 0.6))                  # limbă = capătul cu USB-ul
    for sx in (-1, 1):
        for sy in (-1, 1):
            x, y = sx * ESP_HOLE[0] / 2, sy * ESP_HOLE[1] / 2
            e = e.union(cyl(x, y, pl - 0.01, pl + 4, ESP_STANDOFF_R))
            if sy > 0:
                e = e.cut(box(x - 0.45, x + 0.45, y - 4, y + 4, pl + 2, pl + 4.1))
                e = e.union(peg(x, y, pl + 4, ESP_HOLE_D))
            else:
                e = e.union(pin(x, y, pl + 4, ESP_HOLE_D - 0.6, 1.6 + 2.5))
    m = box(-MOD_HOLE_SPACING / 2 - 5, MOD_HOLE_SPACING / 2 + 5, -5, 5, 0, pl)
    for sx in (-1, 1):
        x = sx * MOD_HOLE_SPACING / 2
        m = m.union(cyl(x, 0, pl - 0.01, pl + 4, 2.8))
        m = m.union(cyl(x, 0, pl + 4 - 0.01, pl + 4 + 3.5, LED_PIN_D / 2))
    return e, m


def fit_test():
    pl = 2.0
    ow, oh = OLED_PCB[0] + 6, OLED_PCB[1] + 6
    t = box(-ow / 2, ow / 2, -oh / 2, oh / 2, 0, pl)
    t = t.cut(box(-WIN[0] / 2, WIN[0] / 2, OLED_AA_OFF - WIN[1] / 2, OLED_AA_OFF + WIN[1] / 2, -1, pl + 1))
    for (hx, hy) in OLED_HOLES:
        t = t.union(cyl(hx, hy, pl - 0.01, pl + OLED_GLASS_T, OLED_STANDOFF_R))
        t = t.union(cyl(hx, hy, pl + OLED_GLASS_T - 0.01, pl + OLED_GLASS_T + OLED_PCB_T + 0.8, PEG_D / 2))
    t = t.union(box(-3, 3, oh / 2, oh / 2 + 4, 0, 0.6))                  # limbă = partea cu pinii (sus)
    e, m = fit_test_esp_led()
    e = e.translate((ow / 2 + (ESP_HOLE[0] + 8) / 2 + 4, 0, 0))
    m = m.translate((0, -(oh / 2 + 10), 0))
    # buton: bucată din top (2 mm) cu gaura + șinele, exact ca în corp (se printează la fel, cu topul pe pat)
    z_off = Z_TOP_IN - pl
    bt = box(-half - RAIL_T - 3, half + RAIL_T + 3, BTN_Y - half - 5, BTN_Y + half + RAIL_T + 3, Z_TOP_IN, Z_TOP_IN + pl)
    bt = bt.cut(cyl(0, BTN_Y, Z_TOP_IN - 1, Z_TOP_IN + pl + 1, BTN_HOLE_D / 2))
    bt = bt.union(body.intersect(box(-half - RAIL_T - 0.5, half + RAIL_T + 0.5, BTN_Y - half - 2.5, BTN_Y + half + RAIL_T + 0.5,
                                     sw_bot - LEDGE_T - 0.1, Z_TOP_IN + 0.01)))
    bt = bt.mirror("XY").translate((0, -BTN_Y, Z_TOP_IN + pl)).translate((-(ow / 2 + 14), -(oh / 2 + 8), 0))
    return t.union(e).union(m).union(bt)


parts = {"body": body, "lid": lid, "diffuser": diffuser, "button_cap": cap, "oled_clamp": clamp}

if __name__ == "__main__":
    os.makedirs(os.path.join(OUT, "stl"), exist_ok=True)
    os.makedirs(os.path.join(OUT, "step"), exist_ok=True)
    os.makedirs(os.path.join(OUT, "preview"), exist_ok=True)
    parts["fit_test"] = fit_test()
    _e, _m = fit_test_esp_led()
    parts["fit_test_esp_led"] = _e.union(_m.translate((0, -(ESP_HOLE[1] / 2 + 12), 0)))
    for name, p in parts.items():
        cq.exporters.export(p, os.path.join(OUT, "stl", f"{name}.stl"), tolerance=0.02, angularTolerance=0.1)
        cq.exporters.export(p, os.path.join(OUT, "step", f"{name}.step"))
        bb = p.val().BoundingBox()
        print(f"{name:11s} {bb.xlen:6.1f} x {bb.ylen:6.1f} x {bb.zlen:6.1f}  valid={p.val().isValid()}")
    for name, p in {**components, "oled_view": oled_view, "cap_placed": cap_placed}.items():
        cq.exporters.export(p, os.path.join(OUT, "preview", f"{name}.stl"))
    print("probleme:", check() or "niciuna")
    print(f"ESP_Z={ESP_Z:.1f}  MOD_Z={MOD_Z:.1f}  USB_Z={USB_Z:.1f}  clamp z={CLAMP_Z[0]:.1f}..{CLAMP_Z[1]:.1f}  "
          f"bosaje față y={by_f:.1f}  ESP y={esp_y0:.1f}..{esp_y0 + ESP_PCB[1]:.1f}")
