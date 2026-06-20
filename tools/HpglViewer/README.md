# HpglViewer

A tiny WinForms harness that renders an HP-GL/2 file with the
[`Hpgl.Rendering`](../../src/Hpgl.Rendering/) library and shows it in a window — a quick visual
verification that the renderer works on real captures.

## Run

```powershell
dotnet build tools/HpglViewer/HpglViewer.csproj -c Release
tools/HpglViewer/bin/Release/net472/HpglViewer.exe
```

With no arguments it loads the bundled [`Test/test.plt`](../../Test/test.plt) (a real 8563E
capture). Pass a path to view another file:

```powershell
HpglViewer.exe "C:\path\to\plot.plt"
```

- **File → Open / Save PNG**, **View → Black/White background / Reload**.
- The window scales the rendered image to fit (aspect preserved).

## Headless smoke mode

Render to a PNG without opening a window (handy for CI / quick checks):

```powershell
HpglViewer.exe --out out.png "C:\path\to\plot.plt"
```

---
*Hpgl.Rendering's plotter-emulation/render technique is derived from the HP7470A Plotter Emulator
(`7470.cpp`) by John Miles, KE5FX — http://www.ke5fx.com/*
