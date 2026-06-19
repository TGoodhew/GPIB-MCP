# Hpgl.Rendering

A small, standalone C# class library that rasterizes **HP-GL/2 vector plots** to a
`System.Drawing.Bitmap` or PNG bytes. It has no GPIB/VISA or MCP dependencies and is
intended to be reusable anywhere a .NET Framework app needs to turn an HP-GL/2 stream
(e.g. a captured spectrum-analyzer plot) into an image.

- **Target:** .NET Framework 4.7.2, `System.Drawing` (no external NuGet dependencies)
- **Input:** HP-GL/2 text (or raw bytes, decoded as Latin-1)
- **Output:** `Bitmap` or PNG `byte[]`

## Credit

The HP-GL plotter-emulation **capture-and-render technique** that this library supports
is derived from the **HP7470A Plotter Emulator (`7470.cpp`) by John Miles, KE5FX**.

> Original C++ author: **John Miles (KE5FX)** — <http://www.ke5fx.com/>

This C# library is an independent adaptation and carries no warranty from KE5FX. Please
keep this attribution in derivative work.

## Usage

```csharp
using Hpgl.Rendering;

byte[] png = HpglRenderer.RenderToPng(hpglText, new HpglRenderOptions
{
    Width = 1280,
    Height = 960,
    Background = HpglBackground.Black,
});

// or work with the bitmap directly
using (Bitmap bmp = HpglRenderer.RenderToBitmap(hpglText))
{
    // ...
}
```

## Scope

This is a deliberately **clean, general** HP-GL/2 vector renderer — unlike `7470.cpp` it
contains no per-instrument fix-ups (those belong in the caller's capture profile). It
covers the primitive set emitted by HP 8560/8566-class spectrum analyzers:

- `IN` / `DF` (reset/default), `IP` (input points), `SC` (scaling), `IW` (ignored in v1)
- `SP` (pen select → colour), `PU` / `PD` / `PA` / `PR` (pen up/down, absolute/relative)
- `LB` (label) with `DT` (terminator), `SI` / `SR` (char size), `DI` (label direction)

Geometry is auto-fit (aspect-preserving) to the output canvas, so any HP-GL stream renders
without needing to know the source plot bounds.

### Not yet supported (see issues)

Arcs/circles (`AA`/`CI`), polygons/fill (`PM`/`FP`/`EP`), encoded polylines (`PE`),
patterned line types (`LT` renders solid), user-defined characters (`UC`), and PCL raster.
A cross-platform backend (ImageSharp/SkiaSharp) to drop the `System.Drawing`/net472
coupling is also tracked as future work.
