# HpglViewer

A WinForms harness that renders an HP-GL/2 file with the
[`Hpgl.Rendering`](../../src/Hpgl.Rendering/) library and shows it **side by side with an independent
reference render** — a quick visual cross-check that our renderer matches a third party.

- **Left pane:** our render (`Hpgl.Rendering`).
- **Right pane:** the same file rendered by **hp2xx** (run live), or any image opened via
  *File → Open reference image*.

## Getting the hp2xx reference

The viewer looks for `hp2xx.exe` via the `HP2XX_EXE` environment variable, then the `PATH`, then
common install locations (incl. Cygwin's `C:\cygwin64\bin\hp2xx.exe`). If it isn't found the right
pane just stays empty and you can load a reference image by hand.

> Note: the 2005 **GnuWin32** hp2xx binary fails on modern Windows ("opening temporary file: Invalid
> argument") and produces no output — use a **Cygwin** hp2xx (`apt-cyg install hp2xx` / the Cygwin
> setup), which handles temp files correctly, or point `HP2XX_EXE` at any working build.

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

- **File → Open HP-GL / Open reference image / Save comparison PNG**, **View → Black/White
  background (white matches hp2xx) / Reload**.
- Both panes scale to fit (aspect preserved). *Save comparison PNG* writes the two renders side by side.

## Headless smoke mode

Render to a PNG without opening a window (handy for CI / quick checks):

```powershell
HpglViewer.exe --out out.png "C:\path\to\plot.plt"
```

---
*Hpgl.Rendering's plotter-emulation/render technique is derived from the HP7470A Plotter Emulator
(`7470.cpp`) by John Miles, KE5FX — http://www.ke5fx.com/*
