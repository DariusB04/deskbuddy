"""
DeskBuddy v2 – carcasă mai încăpătoare, cu LED-ul în buton.  (v1 rămâne în deskbuddy_cad.py, neschimbată)

Ce e nou față de v1:
  - mai mult loc înăuntru: 50 x 78 x 60 mm (v1: 42 x 70 x 50), ca firele să nu mai forțeze capacul
  - fără lipituri: totul se leagă cu fire Dupont mamă-mamă. ESP32 stă întors (pinii în sus), deci mufele
    se pun de sus și au ~20 mm liberi deasupra. Spațiul pentru mufe + cotul firului e modelat („dupont”)
    și verificat să nu atingă nimic.
  - firele butonului: mufele intră pe cele 2 picioare cu contact (pe diagonală) prin ferestrele din suport și
    coboară spre ESP32; plăcuța LED are pinii spre spate. Celelalte 2 picioare ale switch-ului nu se folosesc.
  - nimic nu mai iese în afară pe laterale (bosajele și stâlpii barei OLED sunt tăiați după colțurile rotunjite)
  - butonul = difuzorul LED-ului: un „puc” translucid de Ø28 deasupra; LED-ul stă dedesubt și luminează prin el
    (lumina de sub birou dispare: capacul de jos e plin)
  - butonul nu mai poate sări (flanșă sub top) și nu se mai poate înfunda (inel-opritor la 0.6 mm după clic);
    e ghidat pe 6 mm (topul + un guler), deci nu se înclină
  - switch-ul 12x12 și modulul LED stau pe o piesă separată (button_mount), prinsă cu 2 șuruburi pe 2 bosaje
    din top; ea ține capacul captiv. Nimic nu mai stă presat pe tija switch-ului.

Piese (stl/v2, step/v2):
  body          corpul (printat cu topul pe pat, fără suporturi)
  lid           capacul de jos, cu stâlpii ESP32 (clic la capătul cu USB)
  oled_clamp    bara care apasă OLED-ul pe stâlpișori (ca în v1)
  button_cap    pucul: ALB / translucid (PETG). Se printează cu fața de sus pe pat
  button_mount  suportul pentru switch + LED, sub top (2x M3)
  fit_test_button  bucată din top cu gaura, gulerul și bosajele: testezi butonul (~20 min) înainte de corp

Șuruburi: 8x M3x14 (4 capac, 2 bară OLED, 2 suportul butonului).
Rulează:  python deskbuddy_v2_cad.py
"""
import math
import os

import cadquery as cq

# ================================================================== CARCASA
W = 50.0                # lățime exterioară (v1: 42)
D = 78.0                # adâncime la bază (v1: 70)
H = 60.0                # înălțime totală, de la birou (v1: 50) – loc pentru mufele Dupont
GAP = 3.5               # cât de ridicat e corpul pe picioruțe
LEG = 7.0
ANGLE = 18.0            # înclinarea feței din față
T = 2.0
R = 2.5
FIT = 0.25
MIN_SKIN = 1.2
LID_T = 1.6

SCREW_LEN = 14.0
SCREW_HOLE_D = 2.6
SCREW_CLEAR_D = 3.4
BOSS_R = 3.2

# ================================================================== OLED 0.96" (ca în v1)
OLED_PCB = (27.3, 27.8)
OLED_PCB_T = 1.2
OLED_HOLE = (23.5, 23.8)
OLED_HOLE_D = 2.0
OLED_GLASS = (26.7, 19.3)
OLED_GLASS_T = 1.7
OLED_GLASS_OFF = -0.3
OLED_AA = (21.74, 10.86)
OLED_AA_OFF = 2.0
WIN_MARGIN = 0.7
WIN_CHAMFER = 0.8
OLED_S = 31.0                   # centrul PCB-ului pe pantă, de la birou (corpul e mai înalt)
OLED_STANDOFF_R = 1.7

# ================================================================== ESP32 (DevKit V1 30 pini)
ESP_PCB = (28.2, 51.8)
ESP_HOLE = (23.8, 46.3)
ESP_HOLE_D = 2.8
ESP_PIN_ROWS = 25.4
ESP_PINS = 15
ESP_STANDOFF_R = 2.5
# placa stă ÎNTOARSĂ: componentele (modulul, USB-ul) în jos, pinii în sus -> mufele Dupont se pun de sus,
# cu placa deja pe capac, și au tot spațiul de deasupra
ESP_UNDER = 6.5                 # capac -> fața de jos a plăcii (modulul, USB-ul și butoanele stau aici)
ESP_HDR = 2.5                   # plasticul barettei, deasupra plăcii
# mufă Dupont mamă (fire mamă-mamă, fără lipituri)
DUP_L = 14.0                    # lungimea carcasei mufei
DUP_W = 2.6
WIRE_BEND = 6.0                 # cât îi trebuie firului ca să se îndoaie după mufă

# ================================================================== modul LED KY-016
MOD = (15.0, 20.0)              # lățime (marginea cu găurile), lungime
MOD_T = 1.6
MOD_HOLE_SPACING = 10.0
MOD_HOLE_EDGE = 2.5
MOD_LED_EDGE = 8.5
LED_LEN = 14.0                  # fața plăcuței -> vârful LED-ului
MOD_HOLE_D = 2.4               # găurile plăcuței (3.0 era prea gros: știftul nu intra)
LED_PIN_D = 1.8

# ================================================================== butonul cu LED
BTN_Y = 50.0                    # centrul butonului (pe adâncime)
CAP_D = 28.0                    # pucul
CAP_RISE = 3.0                  # cât iese deasupra topului
CAP_WALL = 1.2                  # subțire: lasă lumina să treacă (PETG alb / natural)
CAP_TOP_T = 1.4
CAP_FILLET = 1.8
CAP_CLEAR = 0.25                # joc pe rază în gaura topului
FLANGE_W = 2.0                  # cât iese flanșa în afara pucului
FLANGE_T = 1.4
COLLAR = 4.0                    # gulerul de sub top: ghidaj total = T + COLLAR = 6 mm
COLLAR_T = 1.6
POST_D = 4.0                    # tija din mijlocul pucului care apasă switch-ul
STOP_GAP = 0.6                  # cât merge pucul în jos până la opritor (switch-ul face clic la ~0.25)
PRELOAD = 0.15                  # switch-ul împinge ușor pucul în flanșă: nu zdrăngăne
# switch tactil 12x12x7.3 (4 picioare; picioarele față/spate, la x = ±2.5, y = ±6.25)
SW = 12.0
SW_H = 7.3
SW_BODY_H = 3.6
SW_STEM = 3.6
SW_FIT = 0.3
SW_LEG = 3.5                    # cât ies picioarele sub corpul switch-ului
# contactul e pe diagonală: picioarele (+2.5,+6.25) și (-2.5,-6.25). Switch-ul e rotit așa încât diagonala
# asta să stea pe axa y (x = 0): mufele de pe ele coboară departe de plăcuța LED
SW_ROT = 90.0 - math.degrees(math.atan2(6.25, 2.5))
LED_OFF = -9.25                 # LED-ul, lângă switch (spre stânga, -x), tot sub puc
MOD_DIR = 1                     # pinii plăcuței LED spre spate (+y): în față e bara OLED-ului
MOUNT_T = 2.0
LED_BELOW = 2.5                 # plăcuța LED stă cu atât sub suport (sub vârfurile picioarelor nefolosite)
MOUNT_BOSSES = [(0.0, BTN_Y - 20.0), (0.0, BTN_Y + 20.0)]     # față / spate: departe de mufe

# ================================================================== derivate
ta, ca, sa = (math.tan(math.radians(ANGLE)), math.cos(math.radians(ANGLE)), math.sin(math.radians(ANGLE)))
FRONT_IN = T / ca
Z_LID_TOP = GAP + LID_T
Z_TOP_IN = H - T
BOSS_H = SCREW_LEN - LID_T + 1.0

ESP_Z = Z_LID_TOP + ESP_UNDER                             # fața de jos a plăcii ESP32 (întoarsă)
ESP_DUP_Z = ESP_Z + 1.6 + ESP_HDR                         # de aici încep mufele de pe pinii ESP32
USB_CUT = (12.0, 10.0)
USB_Z = ESP_Z - 1.6                                       # mufa USB e acum sub placă
esp_y0 = D - T - ESP_PCB[1] - 0.2
esp_yc = esp_y0 + ESP_PCB[1] / 2
ESP_HOLES = [(sx * ESP_HOLE[0] / 2, esp_yc + sy * ESP_HOLE[1] / 2) for sx in (-1, 1) for sy in (-1, 1)]

CAP_HOLE_R = CAP_D / 2 + CAP_CLEAR
Z_COLLAR_BOT = Z_TOP_IN - COLLAR                         # flanșa se sprijină aici (în repaus)
Z_FLANGE_BOT = Z_COLLAR_BOT - FLANGE_T
SW_TOP = Z_FLANGE_BOT + PRELOAD                          # vârful tijei switch-ului, liber (tija pucului e la Z_FLANGE_BOT)
SW_BOT = SW_TOP - SW_H
Z_MOUNT_TOP = SW_BOT                                     # switch-ul stă pe suport
Z_MOUNT_BOT = Z_MOUNT_TOP - MOUNT_T
Z_STOP = Z_FLANGE_BOT - STOP_GAP                         # vârful inelului-opritor
MB_LEN = Z_TOP_IN - Z_MOUNT_TOP                          # bosajele suportului, din top în jos
assert SCREW_LEN - MOUNT_T <= MB_LEN + 0.5, "șurubul M3x14 iese prin top"
MOD_Z = Z_MOUNT_BOT - LED_BELOW - MOD_T                  # fața de jos a plăcuței LED (LED-ul în sus)
LED_TIP_Z = MOD_Z + MOD_T + LED_LEN
LED_X = LED_OFF
LED_Y = BTN_Y - 3.0                                       # puțin spre față: mufele plăcuței încap până la spate
# plăcuța LED: lungă pe y, pinii (mufele) spre spate, marginea cu găurile spre față
MOD_Y0 = LED_Y + MOD_DIR * (MOD[1] - MOD_LED_EDGE)       # capătul cu pinii
MOD_Y1 = LED_Y - MOD_DIR * MOD_LED_EDGE                  # marginea cu găurile
MOD_HOLES = [(LED_X + sx * MOD_HOLE_SPACING / 2, MOD_Y1 + MOD_DIR * MOD_HOLE_EDGE) for sx in (-1, 1)]


def rot(x, y, deg=None):
    """rotește (x, y) în jurul centrului butonului."""
    a = math.radians(SW_ROT if deg is None else deg)
    return (x * math.cos(a) - y * math.sin(a), BTN_Y + x * math.sin(a) + y * math.cos(a))


SW_LEGS = [rot(sx * 2.5, sy * 6.25) for sx in (-1, 1) for sy in (-1, 1)]
SW_LEGS_USED = [rot(2.5, 6.25), rot(-2.5, -6.25)]          # diagonala cu contactul (ajung la x = 0)

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
HOLES_IN_BODY = []


def prof(y1, z0, z1, front_off):
    return [(front_off + z0 * ta, z0), (y1, z0), (y1, z1), (front_off + z1 * ta, z1)]


def ext(pts, width):
    return cq.Workplane("YZ").polyline(pts).close().extrude(width / 2.0, both=True)


def front_plane(s, depth=0.0):
    return cq.Plane(origin=(0, s * sa + depth * ca, s * ca - depth * sa), xDir=(1, 0, 0), normal=(0, -ca, sa))


def box(x0, x1, y0, y1, z0, z1):
    x0, x1 = sorted((x0, x1)); y0, y1 = sorted((y0, y1)); z0, z1 = sorted((z0, z1))
    return cq.Workplane("XY").box(x1 - x0, y1 - y0, z1 - z0).translate(((x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2))


def cyl(x, y, z0, z1, r):
    return cq.Workplane("XY", origin=(x, y, z0)).circle(r).extrude(z1 - z0)


def tube(x, y, z0, z1, r_in, r_out):
    return cyl(x, y, z0, z1, r_out).cut(cyl(x, y, z0 - 1, z1 + 1, r_in))


def slab(front_off, thick, z0, z1, width):
    return ext([(front_off + z0 * ta, z0), (front_off + z0 * ta + thick, z0),
                (front_off + z1 * ta + thick, z1), (front_off + z1 * ta, z1)], width)


# ================================================================== BODY
outer_sharp = ext(prof(D, 0, H, 0.0), W)
outer = outer_sharp.edges("not(<Z)").fillet(R)
body = outer.cut(ext(prof(D - T, -1, Z_TOP_IN, FRONT_IN), W - 2 * T))

under = box(-W, W, -5, D + 5, -1, GAP)
for sx in (-1, 1):
    for y0, y1 in ((-5, LEG), (D - LEG, D + 5)):
        under = under.cut(box(sx * (W / 2 - LEG), sx * W, y0, y1, -2, GAP + 1))
body = body.cut(under)

hole_top = Z_LID_TOP - 0.5 + BOSS_H
bx = W / 2 - T - 1.5
by_f = max(FRONT_IN + Z_LID_TOP * ta + BOSS_R + 0.4, hole_top * ta + (MIN_SKIN + 0.2) / ca + SCREW_HOLE_D / 2)
by_r = D - T - 1.5
SCREWS = [(-bx, by_f), (bx, by_f), (-bx, by_r), (bx, by_r)]
for (x, y) in SCREWS:
    cx = math.copysign(W / 2 - T + 0.5, x)
    cy = (D - T + 0.5) if y > D / 2 else y - BOSS_R
    boss = cyl(x, y, Z_LID_TOP, Z_LID_TOP + BOSS_H, BOSS_R)
    gus = (cq.Workplane("XY", origin=(x, y, Z_LID_TOP + BOSS_H)).circle(BOSS_R)
           .workplane(offset=BOSS_R * 1.4).center(cx - x, cy - y).circle(0.4).loft())
    body = body.union(boss.union(gus).intersect(outer))          # tăiat după forma rotunjită: nu iese nimic afară
    body = body.cut(cyl(x, y, Z_LID_TOP - 0.5, hole_top, SCREW_HOLE_D / 2))
    HOLES_IN_BODY.append((x, y, Z_LID_TOP, hole_top, SCREW_HOLE_D / 2))

# ---- OLED: fereastră + stâlpișori cu știfturi (ca în v1)
s_win = OLED_S + OLED_AA_OFF
WIN = (OLED_AA[0] + 2 * WIN_MARGIN, OLED_AA[1] + 2 * WIN_MARGIN)
win = (cq.Workplane(front_plane(s_win, -0.01)).rect(WIN[0] + 2 * WIN_CHAMFER, WIN[1] + 2 * WIN_CHAMFER)
       .workplane(offset=-WIN_CHAMFER).rect(WIN[0], WIN[1]).loft())
body = body.cut(win).cut(cq.Workplane(front_plane(s_win, WIN_CHAMFER - 0.01)).rect(*WIN).extrude(-(T + 1)))
PEG_D = OLED_HOLE_D - 0.25
OLED_HOLES = [(sx * OLED_HOLE[0] / 2, sy * OLED_HOLE[1] / 2) for sx in (-1, 1) for sy in (-1, 1)]
for (hx, hy) in OLED_HOLES:
    so = cq.Workplane(front_plane(OLED_S + hy, T - 0.2)).center(hx, 0).circle(OLED_STANDOFF_R).extrude(-(OLED_GLASS_T + 0.2))
    pg = (cq.Workplane(front_plane(OLED_S + hy, T + OLED_GLASS_T - 0.01)).center(hx, 0).circle(PEG_D / 2)
          .extrude(-(OLED_PCB_T + 0.8)))
    body = body.union(so).union(pg)

# ---- bara OLED
OLED_BACK = T + OLED_GLASS_T + OLED_PCB_T
CLAMP_PRELOAD = 0.3
s_bar = (OLED_S + OLED_HOLE[1] / 2 - 9.0, OLED_S + OLED_HOLE[1] / 2 - 3.5)
CLAMP_Z = (s_bar[0] * ca - OLED_BACK * sa, s_bar[1] * ca - OLED_BACK * sa)
CLAMP_D = 7.0
clamp_fo = (OLED_BACK - CLAMP_PRELOAD) / ca
clamp = slab(clamp_fo, CLAMP_D, CLAMP_Z[0], CLAMP_Z[1], W - 2 * T - 2 * FIT)
clamp = clamp.cut(slab(clamp_fo - 1, 1 + 1.2 / ca, CLAMP_Z[0] - 1, CLAMP_Z[1] + 1, 17.0))
CL_X = W / 2 - T - 2.4
CL_Y = clamp_fo + CLAMP_Z[1] * ta + CLAMP_D / 2 + 0.3
clamp_h = CLAMP_Z[1] - CLAMP_Z[0]
for sx in (-1, 1):
    clamp = clamp.cut(cyl(sx * CL_X, CL_Y, CLAMP_Z[0] - 1, CLAMP_Z[1] + 1, SCREW_CLEAR_D / 2))
    col = box(sx * (OLED_PCB[0] / 2 + 0.7), sx * (W / 2 - T + 0.5), CL_Y - 3, CL_Y + 3, CLAMP_Z[1], Z_TOP_IN + 0.01)
    body = body.union(col.intersect(outer))
    z1 = CLAMP_Z[1] + (SCREW_LEN - clamp_h) + 0.5
    body = body.cut(cyl(sx * CL_X, CL_Y, CLAMP_Z[1] - 0.5, z1, SCREW_HOLE_D / 2))
    HOLES_IN_BODY.append((sx * CL_X, CL_Y, CLAMP_Z[1], z1, SCREW_HOLE_D / 2))

# ---- USB în spate
usb = cq.Workplane("XZ", origin=(0, D + 1, USB_Z)).rect(*USB_CUT).extrude(T + 2).edges("|Y").fillet(2.0)
body = body.cut(usb)

# ---- butonul: gaura din top + gulerul (ghidaj 6 mm) + 2 bosaje pentru suport
body = body.union(tube(0, BTN_Y, Z_COLLAR_BOT, Z_TOP_IN + 0.01, CAP_HOLE_R, CAP_HOLE_R + COLLAR_T))
body = body.cut(cyl(0, BTN_Y, Z_COLLAR_BOT - 1, H + 1, CAP_HOLE_R))
for (x, y) in MOUNT_BOSSES:
    body = body.union(cyl(x, y, Z_MOUNT_TOP, Z_TOP_IN + 0.01, BOSS_R))
    hz = Z_MOUNT_TOP + SCREW_LEN - MOUNT_T + 0.5
    body = body.cut(cyl(x, y, Z_MOUNT_TOP - 0.5, hz, SCREW_HOLE_D / 2))
    HOLES_IN_BODY.append((x, y, Z_MOUNT_TOP, hz, SCREW_HOLE_D / 2))

# ================================================================== LID (plin: lumina e acum în buton)
lid_front = FRONT_IN + Z_LID_TOP * ta + FIT
lid = box(-(W / 2 - T - FIT), W / 2 - T - FIT, lid_front, D - T - FIT, GAP, Z_LID_TOP).edges("|Z").fillet(1.0)
for (x, y) in SCREWS:
    lid = lid.cut(cyl(x, y, GAP - 1, Z_LID_TOP + 1, SCREW_CLEAR_D / 2))


def peg(x, y, z, hole_d, pcb_t=1.6):
    d = hole_d - 0.25
    p = cyl(x, y, z - 0.01, z + pcb_t + 0.15, d / 2)
    barb = (cq.Workplane("XY", origin=(x, y, z + pcb_t + 0.15)).circle((hole_d + 0.35) / 2)
            .workplane(offset=1.3).circle((hole_d - 0.8) / 2).loft())
    p = p.union(barb)
    return p.cut(box(x - 0.45, x + 0.45, y - 3, y + 3, z - 2.0, z + pcb_t + 2))


def pin(x, y, z, d, h):
    return cyl(x, y, z - 0.01, z + h, d / 2).faces(">Z").edges().chamfer(0.3)


for (x, y) in ESP_HOLES:
    lid = lid.union(cyl(x, y, Z_LID_TOP - 0.01, ESP_Z, ESP_STANDOFF_R))
    if y > esp_yc:                                                     # clic la capătul cu USB
        lid = lid.cut(box(x - 0.45, x + 0.45, y - ESP_STANDOFF_R - 1, y + ESP_STANDOFF_R + 1, ESP_Z - 2.0, ESP_Z + 0.1))
        lid = lid.union(peg(x, y, ESP_Z, ESP_HOLE_D))
    else:
        lid = lid.union(pin(x, y, ESP_Z, ESP_HOLE_D - 0.6, 1.6 + 2.5))

# ================================================================== BUTTON CAP (pucul)
# coordonate reale (pus în corp, în repaus). Se printează răsturnat (fața de sus pe pat).
cap_top = H + CAP_RISE
cap = cyl(0, BTN_Y, Z_FLANGE_BOT, cap_top, CAP_D / 2).edges(">Z").fillet(CAP_FILLET)
cap = cap.cut(cyl(0, BTN_Y, Z_FLANGE_BOT - 1, cap_top - CAP_TOP_T, CAP_D / 2 - CAP_WALL))
cap = cap.union(tube(0, BTN_Y, Z_FLANGE_BOT, Z_COLLAR_BOT, CAP_D / 2 - CAP_WALL, CAP_D / 2 + FLANGE_W))
cap = cap.union(cyl(0, BTN_Y, Z_FLANGE_BOT, cap_top - CAP_TOP_T + 0.01, POST_D / 2))       # apasă tija switch-ului

# ================================================================== BUTTON MOUNT (sub top)
MOUNT_R = CAP_D / 2 + FLANGE_W + 0.5
mount = cyl(0, BTN_Y, Z_MOUNT_BOT, Z_MOUNT_TOP, MOUNT_R)
for (x, y) in MOUNT_BOSSES:
    ang = math.atan2(y - BTN_Y, x)
    L = math.hypot(x, y - BTN_Y)
    arm = (cq.Workplane("XY", origin=(x / 2, (y + BTN_Y) / 2, Z_MOUNT_BOT)).transformed(rotate=(0, 0, math.degrees(ang)))
           .rect(L, 2 * BOSS_R).extrude(MOUNT_T))
    mount = mount.union(arm).union(cyl(x, y, Z_MOUNT_BOT, Z_MOUNT_TOP, BOSS_R + 0.3))
    mount = mount.cut(cyl(x, y, Z_MOUNT_BOT - 1, Z_MOUNT_TOP + 1, SCREW_CLEAR_D / 2))
# inelul-opritor: flanșa pucului se oprește pe el după STOP_GAP
mount = mount.union(tube(0, BTN_Y, Z_MOUNT_TOP - 0.01, Z_STOP, CAP_D / 2 - CAP_WALL + 0.4, CAP_D / 2 + FLANGE_W - 0.3))
# locașul switch-ului: 4 colțare îl țin pe loc (rotite cu switch-ul)
half = SW / 2 + SW_FIT
for sx in (-1, 1):
    for sy in (-1, 1):
        c = box(sx * (half - 2.5), sx * (half + 1.2), BTN_Y + sy * (half - 2.5), BTN_Y + sy * (half + 1.2),
                Z_MOUNT_TOP - 0.01, Z_MOUNT_TOP + 2.2)
        c = c.cut(box(-half, half, BTN_Y - half, BTN_Y + half, Z_MOUNT_TOP - 1, Z_MOUNT_TOP + 3))
        mount = mount.union(c.rotate((0, BTN_Y, 0), (0, BTN_Y, 1), SW_ROT))
# tălpi pentru știfturile plăcuței LED (unul iese din disc)
for (x, y) in MOD_HOLES:
    mount = mount.union(cyl(x, y, Z_MOUNT_BOT, Z_MOUNT_TOP, 3.3))
# ferestre: pe cele 2 picioare cu contact intră mufele Dupont până la corpul switch-ului;
# celelalte 2 picioare doar trec prin suport
for (x, y) in SW_LEGS:
    w = DUP_W + 0.8 if (x, y) in SW_LEGS_USED else 1.8
    mount = mount.cut(box(x - w / 2, x + w / 2, y - w / 2, y + w / 2, Z_MOUNT_BOT - 1, Z_MOUNT_TOP + 1))
mount = mount.cut(cyl(LED_X, LED_Y, Z_MOUNT_BOT - 1, Z_MOUNT_TOP + 1, 3.1))
# 2 știfturi în jos pentru plăcuța LED (prin găurile ei), cu clic
for (x, y) in MOD_HOLES:
    mount = mount.union(cyl(x, y, MOD_Z + MOD_T, Z_MOUNT_BOT + 0.01, 2.6))
    p = cyl(x, y, MOD_Z - 1.4, MOD_Z + MOD_T + 0.01, (MOD_HOLE_D - 0.25) / 2)
    barb = (cq.Workplane("XY", origin=(x, y, MOD_Z - 0.1)).circle((MOD_HOLE_D + 0.35) / 2)
            .workplane(offset=-1.3).circle((MOD_HOLE_D - 0.8) / 2).loft())
    p = p.union(barb).cut(box(x - 0.3, x + 0.3, y - 3, y + 3, MOD_Z - 2, MOD_Z + MOD_T + 1.5))
    mount = mount.union(p)

# ================================================================== componente (verificare / preview)
esp = box(-ESP_PCB[0] / 2, ESP_PCB[0] / 2, esp_y0, esp_y0 + ESP_PCB[1], ESP_Z, ESP_Z + 1.6)
for (x, y) in ESP_HOLES:
    esp = esp.cut(cyl(x, y, ESP_Z - 1, ESP_Z + 3, ESP_HOLE_D / 2 + 0.2))
esp = esp.union(box(-9, 9, esp_y0, esp_y0 + 25.5, ESP_Z - 3.2, ESP_Z))               # modulul (dedesubt)
esp = esp.union(box(-4, 4, D - T - 5, D - T + 0.6, ESP_Z - 3.2, ESP_Z))              # USB (dedesubt)
pin_len = ESP_PINS * 2.54
for sx in (-1, 1):
    esp = esp.union(box(sx * ESP_PIN_ROWS / 2 - 1.27, sx * ESP_PIN_ROWS / 2 + 1.27,
                        esp_yc - pin_len / 2, esp_yc + pin_len / 2, ESP_Z + 1.6, ESP_DUP_Z))

# mufele Dupont + cotul firului: tot spațiul ăsta trebuie să rămână liber
dupont = box(-1, 1, 0, 1, 0, 1).translate((0, 0, -100))      # placeholder, scos la final
for sx in (-1, 1):                                            # pe toți pinii ESP32
    dupont = dupont.union(box(sx * ESP_PIN_ROWS / 2 - DUP_W / 2, sx * ESP_PIN_ROWS / 2 + DUP_W / 2,
                              esp_yc - pin_len / 2, esp_yc + pin_len / 2, ESP_DUP_Z, ESP_DUP_Z + DUP_L + WIRE_BEND))
for (x, y) in SW_LEGS_USED:                                   # pe picioarele switch-ului, în sus până la corp
    dupont = dupont.union(box(x - DUP_W / 2, x + DUP_W / 2, y - DUP_W / 2, y + DUP_W / 2,
                              SW_BOT - DUP_L - WIRE_BEND, SW_BOT))
dupont = dupont.union(box(LED_X - 5.1, LED_X + 5.1, MOD_Y0 + MOD_DIR * DUP_L, MOD_Y0,                     # pinii plăcuței LED
                          MOD_Z + MOD_T / 2 - DUP_W / 2, MOD_Z + MOD_T / 2 + DUP_W / 2))
dupont = dupont.union(box(LED_X - 5.1, LED_X + 5.1, MOD_Y0 + MOD_DIR * (DUP_L + DUP_W), MOD_Y0 + MOD_DIR * DUP_L,      # firele cotesc în jos
                          MOD_Z + MOD_T / 2 - DUP_W / 2 - WIRE_BEND, MOD_Z + MOD_T / 2 + DUP_W / 2))
dupont = dupont.union(cq.Workplane(front_plane(OLED_S + OLED_PCB[1] / 2 - 1.8, OLED_BACK + 2.5))  # pinii OLED
                      .rect(10.5, DUP_W).extrude(-(DUP_L + WIRE_BEND)))
dupont = dupont.cut(box(-5, 5, -5, 5, -110, -90))

oled = (cq.Workplane(front_plane(OLED_S + OLED_GLASS_OFF, T)).rect(OLED_GLASS[0] - 0.3, OLED_GLASS[1] - 0.3)
        .extrude(-(OLED_GLASS_T - 0.05)))
pcb = cq.Workplane(front_plane(OLED_S, T + OLED_GLASS_T)).rect(*OLED_PCB).extrude(-OLED_PCB_T)
for (hx, hy) in OLED_HOLES:
    pcb = pcb.cut(cq.Workplane(front_plane(OLED_S + hy, T)).center(hx, 0).circle(OLED_HOLE_D / 2).extrude(-6))
oled = oled.union(pcb)
oled = oled.union(cq.Workplane(front_plane(OLED_S - 2.0, OLED_BACK)).rect(13.0, 12.0).extrude(-1.2))
oled = oled.union(cq.Workplane(front_plane(OLED_S + OLED_PCB[1] / 2 - 1.8, OLED_BACK)).rect(10.5, 2.6).extrude(-2.5))
oled_view = cq.Workplane(front_plane(OLED_S + OLED_AA_OFF, T - 0.05)).rect(*OLED_AA).extrude(-0.1)

led = box(LED_X - MOD[0] / 2, LED_X + MOD[0] / 2, MOD_Y0, MOD_Y1, MOD_Z, MOD_Z + MOD_T)
for (x, y) in MOD_HOLES:
    led = led.cut(cyl(x, y, MOD_Z - 1, MOD_Z + 3, MOD_HOLE_D / 2 + 0.25))
led = led.union(cyl(LED_X, LED_Y, MOD_Z + MOD_T, LED_TIP_Z - 2.5, 2.5))
led = led.union(cq.Workplane("XY", origin=(LED_X, LED_Y, LED_TIP_Z - 2.5)).sphere(2.5)
                .intersect(box(LED_X - 3, LED_X + 3, LED_Y - 3, LED_Y + 3, LED_TIP_Z - 2.5, LED_TIP_Z)))
led = led.union(box(LED_X - 5.1, LED_X + 5.1, MOD_Y0 + MOD_DIR * 6.0, MOD_Y0 - MOD_DIR * 0.01,      # pinii
                    MOD_Z + MOD_T / 2 - 0.4, MOD_Z + MOD_T / 2 + 0.4))

switch = box(-SW / 2, SW / 2, BTN_Y - SW / 2, BTN_Y + SW / 2, SW_BOT, SW_BOT + SW_BODY_H)
switch = switch.rotate((0, BTN_Y, 0), (0, BTN_Y, 1), SW_ROT)
switch = switch.union(cyl(0, BTN_Y, SW_BOT + SW_BODY_H - 0.01, SW_TOP - 2.0, 3.4))
switch = switch.union(box(-SW_STEM / 2, SW_STEM / 2, BTN_Y - SW_STEM / 2, BTN_Y + SW_STEM / 2, SW_TOP - 2.01, SW_TOP - PRELOAD))
for (x, y) in SW_LEGS:
    switch = switch.union(box(x - 0.4, x + 0.4, y - 0.4, y + 0.4, SW_BOT - SW_LEG, SW_BOT + 0.5))
components = {"esp32": esp, "oled": oled, "led": led, "switch": switch, "dupont": dupont}


def vol(a, b):
    try:
        return a.intersect(b).val().Volume()
    except Exception:
        return 0.0


def check():
    solids = {"body": body, "lid": lid, "cap": cap, "mount": mount, "clamp": clamp, **components}
    allowed = ({"oled", "clamp"}, {"oled", "body"}, {"mount", "led"}, {"mount", "switch"}, {"cap", "switch"},
               {"dupont", "switch"}, {"dupont", "led"}, {"dupont", "oled"}, {"dupont", "esp32"})
    names = list(solids)
    bad = []
    for i, a in enumerate(names):
        for b in names[i + 1:]:
            v = vol(solids[a], solids[b])
            if v > 0.3 and {a, b} not in allowed:
                bad.append((a, b, round(v, 1)))
    # contactele permise trebuie să fie mici (știfturi prin găuri, tija pe switch)
    for a, b, lim in (("mount", "led", 2.0), ("mount", "switch", 1.0), ("cap", "switch", 2.5)):
        v = vol(solids[a], solids[b])
        if v > lim:
            bad.append((a, b, round(v, 1), "prea mult"))
    for (x, y, z0, z1, r) in HOLES_IN_BODY:
        try:
            v = cyl(x, y, z0, z1, r + MIN_SKIN).cut(outer).val().Volume()
        except Exception:
            v = 0.0
        if v > 0.05:
            bad.append(("perete subțire la gaura", (round(x, 1), round(y, 1)), round(v, 2)))
    if vol(clamp.translate((0, 0, -1.5)), body) > 0.01:
        bad.append(("bara OLED se lovește de corp când intră", "", 0))
    hole = cq.Workplane(front_plane(s_win, -0.5)).rect(*WIN).extrude(-(T + 2.0))
    if oled_view.cut(hole).val().Volume() > 0.01:
        bad.append(("zona aprinsă iese din fereastră", "", 0))
    # pucul apăsat până la opritor: nu atinge corpul, iar flanșa ajunge pe inel
    pressed = cap.translate((0, 0, -STOP_GAP))
    if vol(pressed, body) > 0.05:
        bad.append(("pucul apăsat atinge corpul", "", round(vol(pressed, body), 2)))
    if vol(cap.translate((0, 0, -STOP_GAP - 0.05)), mount) < 0.01:
        bad.append(("opritorul nu oprește pucul", "", 0))
    return bad


def fit_test_button():
    """bucata de top cu gaura, gulerul și bosajele (printată tot cu topul pe pat)."""
    pad = 6.0
    xs = [x for x, _ in MOUNT_BOSSES] + [-MOUNT_R, MOUNT_R]
    ys = [y for _, y in MOUNT_BOSSES] + [BTN_Y - MOUNT_R, BTN_Y + MOUNT_R]
    plate = box(min(xs) - pad, max(xs) + pad, min(ys) - pad, max(ys) + pad, Z_TOP_IN, H)
    keep = box(min(xs) - pad, max(xs) + pad, min(ys) - pad, max(ys) + pad, Z_MOUNT_TOP - 0.5, H + 0.01)
    piece = plate.union(body.intersect(keep).cut(box(-W, W, -10, D + 10, H - 0.01, H + 5)))
    piece = piece.cut(cyl(0, BTN_Y, Z_COLLAR_BOT - 1, H + 1, CAP_HOLE_R))
    return piece.mirror("XY").translate((0, -BTN_Y, H))


parts = {"body": body, "lid": lid, "oled_clamp": clamp,
         "button_cap": cap.mirror("XY").translate((0, -BTN_Y, cap_top)),     # răsturnat: fața de sus pe pat
         "button_mount": mount.translate((0, -BTN_Y, -Z_MOUNT_BOT))}

if __name__ == "__main__":
    for d in ("stl/v2", "step/v2", "preview/v2"):
        os.makedirs(os.path.join(OUT, d), exist_ok=True)
    parts["fit_test_button"] = fit_test_button()
    for name, p in parts.items():
        cq.exporters.export(p, os.path.join(OUT, "stl/v2", f"{name}.stl"), tolerance=0.02, angularTolerance=0.1)
        cq.exporters.export(p, os.path.join(OUT, "step/v2", f"{name}.step"))
        bb = p.val().BoundingBox()
        print(f"{name:16s} {bb.xlen:6.1f} x {bb.ylen:6.1f} x {bb.zlen:6.1f}  valid={p.val().isValid()}")
    placed = {"body": body, "lid": lid, "oled_clamp": clamp, "button_cap": cap, "button_mount": mount, **components}
    for name, p in placed.items():
        cq.exporters.export(p, os.path.join(OUT, "preview/v2", f"{name}.stl"))
    print("probleme:", check() or "niciuna")
    print(f"ESP_Z={ESP_Z:.1f}  LED: placa z={MOD_Z:.1f} vârf={LED_TIP_Z:.1f}  switch {SW_BOT:.1f}..{SW_TOP:.1f}  "
          f"flanșă {Z_FLANGE_BOT:.1f}  opritor {Z_STOP:.1f}  suport {Z_MOUNT_BOT:.1f}..{Z_MOUNT_TOP:.1f}")
