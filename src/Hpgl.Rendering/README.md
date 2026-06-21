# Hpgl.Rendering

A small, standalone C# class library that rasterizes **HP-GL/2 vector plots** to a
`System.Drawing.Bitmap` or PNG bytes. It has no GPIB/VISA or MCP dependencies and is
intended to be reusable anywhere a .NET Framework app needs to turn an HP-GL/2 stream
(e.g. a captured spectrum-analyzer plot) into an image.

- **Target:** .NET Framework 4.7.2, `System.Drawing` (no external NuGet dependencies)
- **Input:** HP-GL/2 text (or raw bytes, decoded as Latin-1)
- **Output:** `Bitmap`, PNG `byte[]`, or a self-contained **SVG** document string

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
contains no per-instrument fix-ups (those belong in the caller's capture profile). It now
covers the spec's "minimum-viable" instruction set (see
[`docs/HPGL-7475A-7550A-Rendering-Spec.md`](../../docs/HPGL-7475A-7550A-Rendering-Spec.md)
and `docs/HPGL-CharacterSet-Font-Reference.md`):

- **Configuration / coordinates:** `IN` / `DF` (reset/default), `IP` (input points),
  `SC` (scaling), `RO` (0/90/180/270 rotation), `IW` (soft-clip window — geometrically
  clips vectors, fills, and label strokes).
- **Vectors:** `SP` (pen select → colour), `PU` / `PD` / `PA` / `PR`.
- **Curves & rectangles:** `CI` (circle), `AA` / `AR` (arcs), `EW` (edge wedge),
  `EA` / `ER` (edge rectangles) — chord-subdivided to the `CT`/chord parameter.
- **Area fill:** `RA` / `RR` (fill rectangles), `WG` (fill wedge), `FT` (fill type:
  solid, parallel hatch, cross-hatch), `PT` (pen thickness), `UF` (user-defined
  variable-spacing hatch). Solid fills use a native polygon fill; hatch/cross are
  emitted as scanline line-spans.
- **7550A polygons:** `PM` / `EP` / `FP` with even-odd multi-contour fill (holes).
- **Line types & ticks:** `LT` rendered as dash/dot patterns (4 % of the diagonal by
  default); `TL` / `XT` / `YT` tick marks.
- **Encoded polylines:** `PE` (HP-GL/2 base-64/32 relative-coordinate decoding, incl.
  the `7` / `&lt;` / `=` / `&gt;` / `:` flags) — newer instruments only.
- **Labels / text** (drawn from a built-in single-stroke vector font, so text honours
  size, slant, direction, rotation, clipping, pen colour and line type): `LB`, `DT`
  (terminator), `SI` / `SR` (absolute/relative size, incl. mirroring via negative size),
  `SL` (slant), `DI` / `DR` (absolute/relative direction), `CP` (cursor move),
  `ES` (extra space), `SM` (symbol mode), `CS` / `CA` / `SS` / `SA` and in-label
  shift-in/out for character-set selection, plus embedded CR/LF for multi-line labels.

Geometry is auto-fit (aspect-preserving) to the output canvas, so any HP-GL stream renders
without needing to know the source plot bounds.

`Test/feature-exercise.plt` is a hand-authored plot that drives every one of the above; it
is rendered by a smoke test and makes a good visual sanity check.

### Built-in font

HP's internal glyph outlines are not published as coordinate tables, so labels use a
metric-matched **single-stroke ("Hershey"-style) font** for ASCII Set 0
([`StrokeFont.cs`](StrokeFont.cs)). Geometry is faithful; exact glyph shapes are an
approximation, so label text will not match a real plotter (or another renderer)
glyph-for-glyph.

The font is **fixed-pitch (monospaced)**, like a real HP plotter: every glyph advances by
the same cell. The cell metrics are tuned to the KE5FX reference rendering the same 8563E
capture (#29): the per-character pitch is `~1.25 ×` the HP-GL character width (`SI`/`SR`)
and the glyph ink fills `~0.73` of that pitch, leaving a small uniform inter-character gap.
This keeps long fields (e.g. `460.000kHz`) aligned with KE5FX. (Earlier revisions mis-set
this: first the character width was treated as the whole cell — text too narrow and
cramped; then the pitch was `1.5 ×` — text too wide, so fields drifted out of alignment.)

### Parsed and ignored (not a renderer's job)

The interactive / live-bus instructions return data to the controller or drive hardware and
produce **no geometry**, so they are parsed and skipped (a stream containing them still
renders): the output / digitize set (`OA`/`OC`/`OH`/`OI`/`OS`/`OW`/`OE`…, `DC`/`DP`),
`ESC .` device-control escapes, pen dynamics (`VS`/`FS`/`AS`/`AP`/`CV`), and page / replot /
memory (`PG`/`AF`/`AH`/`RP`/`WD`/`GM`/`KY`). A *faithful interactive emulator* (answering
`OE`/`OS`, an error register, exact per-model numeric ranges) would be a separate effort.

### Not yet supported (see issues)

`UC` user-defined characters and `DL` downloadable glyphs; the 7550A slot/encoding model
(`DS`/`IV`/`CM`, 7/8-bit + ISO, linked Roman8/Katakana8 sets) and non-ASCII international
sets; buffered labels (`BL`/`PB`/`OL`); and PCL raster. A cross-platform backend
(ImageSharp/SkiaSharp) to drop the `System.Drawing`/net472 coupling is also tracked as
future work (#9).
