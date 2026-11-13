"""Randează v2 în PNG (VTK, fără fereastră): ansamblul și o secțiune prin buton.
   python render_v2.py   (după deskbuddy_v2_cad.py, care scrie preview/v2/*.stl)"""
import os
import sys

import vtk

HERE = os.path.dirname(os.path.abspath(__file__))
PREV = os.path.join(HERE, "..", "preview", "v2")
OUT = sys.argv[1] if len(sys.argv) > 1 else PREV

COLORS = {
    "body": (0.62, 0.83, 0.42), "lid": (0.45, 0.65, 0.30), "oled_clamp": (0.35, 0.55, 0.25),
    "button_cap": (0.93, 0.88, 1.0), "button_mount": (0.95, 0.75, 0.35),
    "esp32": (0.12, 0.37, 0.55), "oled": (0.10, 0.10, 0.12), "led": (0.75, 0.35, 0.95), "switch": (0.25, 0.27, 0.30),
    "dupont": (0.95, 0.30, 0.25),
}


def actor(name, clip=None, opacity=1.0):
    r = vtk.vtkSTLReader()
    r.SetFileName(os.path.join(PREV, f"{name}.stl"))
    src = r
    if clip is not None:                       # taie jumătatea din față (x < 0) pentru secțiune
        plane = vtk.vtkPlane()
        plane.SetOrigin(*clip[0])
        plane.SetNormal(*clip[1])
        c = vtk.vtkClipClosedSurface()
        planes = vtk.vtkPlaneCollection()
        planes.AddItem(plane)
        c.SetClippingPlanes(planes)
        c.SetInputConnection(r.GetOutputPort())
        src = c
    m = vtk.vtkPolyDataMapper()
    m.SetInputConnection(src.GetOutputPort())
    a = vtk.vtkActor()
    a.SetMapper(m)
    a.GetProperty().SetColor(*COLORS.get(name, (0.7, 0.7, 0.7)))
    a.GetProperty().SetOpacity(opacity)
    a.GetProperty().SetSpecular(0.2)
    return a


def shot(names, cam, path, clip=None, size=(1200, 900), opacity=None):
    ren = vtk.vtkRenderer()
    ren.SetBackground(0.08, 0.09, 0.10)
    for n in names:
        ren.AddActor(actor(n, clip, (opacity or {}).get(n, 1.0)))
    win = vtk.vtkRenderWindow()
    win.SetOffScreenRendering(1)
    win.SetSize(*size)
    win.AddRenderer(ren)
    c = ren.GetActiveCamera()
    c.SetFocalPoint(*cam[0])
    c.SetPosition(*cam[1])
    c.SetViewUp(0, 0, 1)
    ren.ResetCameraClippingRange()
    c.Zoom(cam[2])
    win.Render()
    w2i = vtk.vtkWindowToImageFilter()
    w2i.SetInput(win)
    w2i.Update()
    wr = vtk.vtkPNGWriter()
    wr.SetFileName(path)
    wr.SetInputConnection(w2i.GetOutputPort())
    wr.Write()
    print("scris", path)


ALL = ["body", "lid", "oled_clamp", "button_cap", "button_mount", "esp32", "oled", "led", "switch", "dupont"]
os.makedirs(OUT, exist_ok=True)
# 1. ansamblul, din față-stânga-sus
shot(["body", "lid", "button_cap", "oled"], ((0, 39, 30), (-150, -120, 130), 1.25), os.path.join(OUT, "v2_ansamblu.png"))
shot(["body", "lid", "button_cap", "oled"], ((0, 39, 30), (170, -90, 60), 1.25), os.path.join(OUT, "v2_lateral.png"))
# 2. interiorul: corpul transparent, cu tot ce e înăuntru
shot(ALL, ((0, 39, 30), (160, -110, 110), 1.25), os.path.join(OUT, "v2_interior.png"),
     opacity={"body": 0.22, "lid": 0.6})
# 3. secțiune prin buton (plan x = 0.01, privit din dreapta), aproape
shot(ALL, ((0, 45, 32), (200, 45, 40), 1.6), os.path.join(OUT, "v2_buton_sectiune.png"),
     clip=((0.01, 0, 0), (-1, 0, 0)))
# 4. secțiune prin LED (plan y = 47, privit din față)
shot(ALL, ((0, 47, 32), (0, -160, 45), 1.6), os.path.join(OUT, "v2_led_sectiune.png"),
     clip=((0, 47.01, 0), (0, 1, 0)))
