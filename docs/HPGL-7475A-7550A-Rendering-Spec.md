# HP-GL Rendering Engine Specification — HP 7475A & HP 7550A

A definitive reference for what a rendering engine must implement to faithfully reproduce the
plotted output of the **HP 7475A** and **HP 7550A** pen plotters from their HP-GL command
streams. Derived from the two plotters' programming manuals.

The goal of an emulator is to consume a byte stream of HP-GL (and, for the 7550A,
device-control escape sequences), maintain the plotter's internal graphics state, and produce
identical geometry — same pen paths, same coordinate mapping, same clipping, same fill
patterns, same character glyphs and placement.

---

## 1. Scope and the two-target problem

The 7475A and 7550A share a common HP-GL core but differ substantially in capability. A single
engine can cover both if it treats the 7550A as a superset and gates the extra instructions and
wider numeric ranges behind a "model" flag.

| Aspect | HP 7475A | HP 7550A |
|---|---|---|
| Coordinate / parameter range | **16-bit**: integer −32 768 … +32 767; scaled-decimal −32 768.0000 … +32 767.9999 | **24-bit**: −2²³ … 2²³−1 (−8 388 608 … 8 388 607) |
| Plotter-unit size | 0.02488 mm (≈40.2 units/mm) | 0.025 mm (exactly 40 units/mm; 1016 units/in) |
| Pens | 6 | 8 |
| Polygon mode / polygon fill | **No** | **Yes** (PM/EP/FP) |
| User-defined fill (UF) | No | Yes |
| Buffered labels (BL/PB/OL) | No | Yes |
| Replot / page handling (RP/PG/AF/AH/NR) | No | Yes |
| Front-panel display & keys (WD/KY/OK) | No | Yes |
| Downloadable characters (DL) | No | Yes |
| Configurable memory (GM, ESC.R/.S/.T) | No | Yes |
| Group count (GC/OG) | No | Yes |
| Device-control escape sequences (ESC .) | Minimal | Full set |
| Carousel / pen-velocity / force / accel control | Limited | Full (VS/FS/AS/AP/CV) |

Everything in §3 (Core HP-GL) applies to **both** unless marked. §4 lists **7550A-only**
instructions.

---

## 2. The coordinate system and rendering pipeline

The engine must implement this transform chain. It is the heart of correct output; most
real-world rendering bugs are coordinate-mapping bugs.

```
user units --(scaling: SC)--> plotter units --(rotation: RO)--> device frame
            --(clip: hard-clip limits ∩ soft-clip window IW)--> output
```

### 2.1 Plotter units (device space)

- The absolute device coordinate system. 1 plotter unit = 0.025 mm on the 7550A,
  0.02488 mm on the 7475A.
- Origin and axis orientation are fixed by paper size and the **hard-clip limits** (the
  mechanical plotting boundary). Query via `OH`. P1/P2 default near the lower-left/upper-right
  of this area.
- All `OA`/`OC`/`OH`/`OP`/`OW` outputs are in plotter units (or current units, per the
  instruction).

### 2.2 Scaling points P1 and P2

- Two reference points in plotter units, defaulting to fixed positions tied to paper size; set
  with `IP` (and `IW`-related queries `OP`).
- P1/P2 define the user-unit coordinate frame when scaling is on.

### 2.3 User units (SC)

- `SC Xmin,Xmax,Ymin,Ymax` maps the user coordinate range onto P1→P2. After `SC`, all
  X,Y parameters in plotting/arc/edge instructions are interpreted as user units and may be
  given in **scaled-decimal** format.
- `SC;` with no parameters turns scaling **off** → parameters are plotter units (integer only).
- The engine must track a "scaling on/off" flag because it changes both the unit interpretation
  and which numeric format is legal.

### 2.4 Rotation (RO)

- 7475A: rotation is fixed/limited per the plotter's behaviour.
- 7550A: `RO 0` or `RO 90` rotates the entire coordinate system. The engine must rotate P1/P2,
  hard-clip limits, and all subsequent geometry accordingly.

### 2.5 Clipping

Two clip regions, intersected:
- **Hard-clip limits** — physical plotting area (from paper size; query `OH`). Nothing is ever
  drawn outside these.
- **Soft-clip window** — `IW X1,Y1,X2,Y2` sets a rectangular window; `IW;` resets it to the
  hard-clip limits. Query `OW`.

The engine clips every drawn vector, arc, fill span, and character stroke to the intersection.
Clipping is geometric (partial vectors are drawn up to the boundary), not all-or-nothing.

### 2.6 Pen state and current position

Persistent state the engine must maintain:
- **Current pen position** (in plotter units internally).
- **Pen up/down** status — `PU`/`PD`. Only pen-down movement draws.
- **Selected pen** (`SP`), which maps to a colour/width for rendering.
- **Carry-over (CP) cursor** for text — see §3.6.

---

## 3. Core HP-GL instruction set (both plotters)

General syntax: a two-character uppercase **mnemonic**, an optional comma-separated
**parameter field**, and a **terminator** (`;`, or the next mnemonic; LF is also valid on
HP-IB). Whitespace is permitted around parameters. Label/symbol instructions consume text
until their own terminator. Parameter formats: integer `[i]`, scaled-decimal `[sd]` (scaling on
only), character `[c]`.

### 3.1 Configuration / state

| Mn. | Name | Parameters | Notes for the engine |
|---|---|---|---|
| `IN` | Initialize | — | Reset all HP-GL state to power-on defaults. |
| `DF` | Default | — | Reset most modal state but **not** P1/P2 or pen position the way `IN` does. Implement the exact default table. |
| `IP` | Input P1/P2 | P1x,P1y[,P2x,P2y] | Set scaling points (plotter units). |
| `SC` | Scale | Xmin,Xmax,Ymin,Ymax | Establish user units; empty = scaling off. |
| `RO` | Rotate | n | Coordinate-system rotation (see §2.4). |
| `IW` | Input Window | X1,Y1,X2,Y2 | Soft-clip rectangle; empty = reset. |
| `PS` | Paper Size | size | Selects media; affects hard-clip limits. 7475A: ISO A4/A3, ANSI A/B. |

### 3.2 Pen movement / vectors

| Mn. | Name | Parameters | Notes |
|---|---|---|---|
| `PU` | Pen Up | [X,Y(,…)] | Lift pen; optional coordinates move without drawing. |
| `PD` | Pen Down | [X,Y(,…)] | Lower pen; optional coordinates draw polyline. |
| `PA` | Plot Absolute | X,Y(,…) | Set absolute mode; listed points drawn/moved per current pen state. |
| `PR` | Plot Relative | X,Y(,…) | Set relative mode; offsets from current position. |

Movement model: `PA`/`PR` set the **plotting mode** (absolute/relative) that persists; `PU`/`PD`
set pen state that persists. Coordinate lists after any of them are processed pairwise using the
current mode + pen state.

### 3.3 Arcs, circles, wedges

| Mn. | Name | Parameters |
|---|---|---|
| `AA` | Arc Absolute | X,Y,arc-angle[,chord] |
| `AR` | Arc Relative | X,Y,arc-angle[,chord] |
| `CI` | Circle | radius[,chord] |
| `EW` | Edge Wedge | radius,start,sweep[,chord] |
| `WG` | Fill Wedge (7550A: "Fill Wedge"; 7475A: "Shade Wedge") | radius,start,sweep[,chord] |

The **chord angle/tolerance** controls polygonal approximation of curves. To match output, the
engine must subdivide arcs into chords using the same rule (default 5°). Smaller chord angle =
smoother curve = more line segments. Reproducing the *exact* vertex set matters if comparing
pen paths byte-for-byte.

### 3.4 Rectangles and area fill

| Mn. | Name | Parameters | Both? |
|---|---|---|---|
| `EA` | Edge Rectangle Absolute | X,Y | Yes |
| `ER` | Edge Rectangle Relative | X,Y | Yes |
| `RA` | Fill Rectangle Absolute | X,Y | Yes |
| `RR` | Fill Rectangle Relative | X,Y | Yes |
| `FT` | Fill Type | type[,spacing[,angle]] | Yes |
| `PT` | Pen Thickness | thickness (mm) | Yes (7550A 0.1–5.0, default 0.3) |

**Fill rendering** is where a naïve engine diverges from the hardware. `RA`/`RR`/`FP`/`WG` fill
by drawing parallel hatch lines, not by flooding pixels. The engine must:
- Generate hatch line spans across the region.
- Honour `FT` type (solid bidirectional, parallel lines, cross-hatch), `spacing`, and `angle`.
- Honour `PT` pen thickness to set line spacing for "solid" fills (overlapping strokes).
A pixel-fill shortcut is acceptable for a raster preview but will not reproduce the characteristic
hatched look or pen-path output.

### 3.5 Line attributes

| Mn. | Name | Parameters | Notes |
|---|---|---|---|
| `LT` | Line Type | pattern[,length] | Dashed/dotted patterns; no-param = solid. Adaptive (length as % of P1–P2 diagonal) vs fixed types. Default length 4% of diagonal. |
| `SP` | Select Pen | n | 7475A 0–6, 7550A 0–8; 0 = pen to carousel (no pen). Maps to colour/width. |
| `SM` | Symbol Mode | char | Plots the given character at each subsequent PA/PR point. Empty = off. |
| `TL` | Tick Length | tp[,tn] | Length of axis ticks (% of P1–P2). |
| `XT`/`YT` | X-tick / Y-tick | — | Draw a tick at current position. |

The engine must implement `LT` as a path-length-parameterised dash generator: the pattern phase
advances along the drawn path, including across arc chords, so dashes are continuous along curves.

### 3.6 Characters and labels

This is the largest subsystem. The engine needs a built-in **vector stroke font** matching the HP
character sets, plus full text-layout state.

| Mn. | Name | Parameters | Notes |
|---|---|---|---|
| `LB` | Label | text…`term` | Draw string starting at current position; advances CP cursor. Terminator default ETX (dec 3), set by `DT`. |
| `DT` | Define Label Terminator | char | Sets the LB/SM-string terminator. |
| `SI` | Absolute Character Size | width,height (cm) | Fixed physical size. |
| `SR` | Relative Character Size | width,height (% of P1–P2) | Size relative to scaling points. |
| `SL` | Character Slant | tan θ | Italic slant. |
| `DI` | Absolute Direction | run,rise | Text direction vector (absolute). |
| `DR` | Relative Direction | run,rise | Text direction (relative to P1–P2). |
| `CP` | Character Plot | spaces,lines | Move cursor by N character cells (no draw). |
| `CS` | Designate Standard Set | set | Select standard charset. |
| `CA` | Designate Alternate Set | set | Select alternate charset. |
| `SS` | Select Standard Set | — | Switch active set to standard. |
| `SA` | Select Alternate Set | — | Switch active set to alternate. |
| `ES` | Extra Space | spaces[,lines] | Adjust inter-character / line spacing. |
| `UC` | User-Defined Character | [pen,]X,Y… | Draw a glyph from inline pen-up/down increments. |

Text-layout state to maintain: current character set (standard + alternate, which is active),
size (SI/SR), slant (SL), direction (DI/DR), and the **CP cell** dimensions that define how the
cursor advances after each glyph, space, CR, and LF embedded in the label. Carriage return
returns X to the label-line origin; line feed advances by one cell height in the current
direction.

**Character sets.** The engine needs the HP stroke-font glyph tables. Set 0 is standard ASCII
and is the power-on default for both standard and alternate. 7475A valid set numbers: 0–4, 6–9,
30–39 (international/ISO variants). 7550A: −1, 0–19, 30–49 (a larger catalogue, plus `DS`/`IV`
slot mechanism and downloadable set −1). Designating an unsupported set raises HP-GL error 5.

### 3.7 Digitizing and status/output

These produce ASCII responses on the I/O channel. An offline renderer can stub them, but a
faithful interactive emulator must answer with correctly formatted strings (the manuals specify
each response's field layout and terminator `[TERM]`).

| Mn. | Output | Response format (7550A) |
|---|---|---|
| `OA` | Actual position + pen | X,Y,P integers |
| `OC` | Commanded position + pen | X,Y (decimals), P |
| `OD` | Digitized point + pen | X,Y,P |
| `OE` | Error | error number 0–7 |
| `OF` | Factors | 40,40 |
| `OH` | Hard-clip limits | XLL,YLL,XUR,YUR |
| `OI` | Identification | model string, e.g. `7550A` |
| `OO` | Options | capability bits |
| `OP` | P1/P2 | P1x,P1y,P2x,P2y |
| `OS` | Status | status byte 0–255 |
| `OW` | Window | XLL,YLL,XUR,YUR |
| `DC`/`DP` | Digitize clear / point | — / sets digitized point |

The `OI` response is the cleanest way for the engine to self-identify which model it is emulating.

---

## 4. HP 7550A-only instructions

Gate these behind the model flag; on a 7475A target they should raise "unrecognised command"
(HP-GL error 1) rather than execute.

### 4.1 Polygons (true filled polygons)

| Mn. | Name | Parameters |
|---|---|---|
| `PM` | Polygon Mode | n (0 = enter/clear, 1 = close subpolygon, 2 = exit) |
| `EP` | Edge Polygon | — |
| `FP` | Fill Polygon | — |

Polygon mode records subsequent PA/PR/PU/PD/CI/arc moves into a **polygon buffer** instead of
drawing. `PM0` opens it, `PM1` closes a subpolygon (enabling holes / multi-contour), `PM2` exits.
`EP` strokes the stored polygon outline; `FP` fills it with the current fill type. The engine
needs an even-odd / non-zero fill of arbitrary multi-contour polygons, then renders the fill as
hatch lines per `FT` (see §3.4). The polygon buffer is size-limited (configurable via `GM`).

### 4.2 User-defined fill

| Mn. | Name | Parameters |
|---|---|---|
| `UF` | User-Defined Fill Type | gap1[,gap2…gap20] | Custom hatch spacing sequence used by fill type. |

### 4.3 Buffered labels & label metrics

| Mn. | Name | Notes |
|---|---|---|
| `BL` | Buffer Label (up to 150 chars) | Stage a label without drawing. |
| `PB` | Print Buffered Label | Draw the staged label. |
| `OL` | Output Label Length | Return metrics of buffered label (length, char count, line feeds). |

### 4.4 Pen dynamics / carousel

| Mn. | Name | Parameters | Default |
|---|---|---|---|
| `VS` | Velocity Select | speed[,pen] | carousel-dependent |
| `FS` | Force Select | force(1–8)[,pen] | carousel-dependent |
| `AS` | Acceleration Select | accel[,pen] | — |
| `AP` | Automatic Pen Operations | n (0–15) | 7 |
| `CV` | Curved Line Generator | n[,delay] | — |
| `CT` | Chord Tolerance | 0/1 | 0 (chord param = degrees vs deviation) |
| `CC` | Character Chord Angle | degrees | 5 |

These affect physical pen behaviour (speed/force) and don't change geometry, so a pure-geometry
renderer can accept-and-ignore them — **except** `CT`, which changes how the chord parameter on
arcs/circles is interpreted (angle vs deviation), and `CC`, which sets the curve smoothness of
characters. Both affect rendered vertex sets and must be honoured for exact output.

### 4.5 Page / replot / display / keys / memory / groups

| Mn. | Name | Notes |
|---|---|---|
| `PG` | Page Feed | Advance media; ends a page. |
| `AF`/`AH` | Advance Page (full/half) | Media advance. |
| `NR` | Not Ready | Pause for media change. |
| `RP` | Replot | n (1–99): replot from replot buffer. |
| `WD` | Write to Display | Up to 32 chars to front-panel LCD. |
| `KY`/`OK` | Define Key / Output Key | Front-panel key assignment & readback. |
| `DL` | Define Downloadable Character | Build a glyph into set −1. |
| `DS`/`IV` | Designate/Invoke Character Slot | Multi-slot charset management. |
| `GM` | Graphics Memory | Allocate polygon/downloadable/replot/vector buffers. |
| `GC`/`OG` | Group Count / Output Group Count | I/O grouping. |

For an offline renderer, `WD`/`KY`/`OK`/`NR` are no-ops; `PG`/`AF`/`AH` should finalize the
current page and start a fresh one (important for multi-page streams); `RP` requires retaining the
replot buffer (the recorded vector list) to re-emit it; `DL`/`DS`/`IV` require the downloadable
glyph machinery.

### 4.6 Device-control escape sequences (7550A)

Distinct from HP-GL: these begin with `ESC .` and control the I/O channel, handshaking, buffer
sizing, and on/off state. They terminate with `:` and use `<DEC>`/`<ASC>` parameters. An engine
that only renders graphics can parse-and-discard these, but a full emulator must implement the
handshake and buffer semantics (especially over RS-232-C). Key members:

`ESC .@` set plotter configuration / buffer size · `ESC .A` output identification ·
`ESC .B` output buffer space · `ESC .E` output extended error · `ESC .H/.I` set handshake mode
1/2 · `ESC .J` abort device control · `ESC .K` abort graphics · `ESC .L` output buffer size ·
`ESC .M` set output mode · `ESC .N` set extended output/handshake · `ESC .O` output extended
status · `ESC .P` set handshake mode · `ESC .Q` set monitor mode · `ESC .R` reset ·
`ESC .S` output configurable memory size · `ESC .T` allocate configurable memory ·
`ESC .U` end flush mode · `ESC .Y/.(` plotter-on · `ESC .Z/.)` plotter-off.

---

## 4.7 Same-command behavioural differences (shared mnemonics that differ)

These mnemonics exist on **both** plotters but behave differently. An engine that hard-codes one
plotter's behaviour will silently mis-render the other. (Commands that are simply absent on the
7475A are covered in §1 and §4; this table is only about *divergent behaviour on a shared
command*.)

| Cmd | 7475A behaviour | 7550A behaviour | Why it matters |
|---|---|---|---|
| **All coordinate params** (`PA`,`PR`,`PU`,`PD`,`AA`,`AR`,`CI`,`EA`,`ER`,`RA`,`RR`,`IP`,`IW`,`SC`,`UC`…) | Integer range **−32 768…+32 767**; scaled-decimal **−32 768.0000…+32 767.9999** | Range **−2²³…2²³−1** (−8 388 608…8 388 607) | A stream legal on the 7550A can overflow on the 7475A (error 6 / position overflow). Clamp/validate per model. |
| **Plotter unit size** | 0.02488 mm (≈40.2 units/mm) | 0.025 mm (exactly 40 units/mm, 1016/in) | Absolute physical placement and `OF` factors differ; same plotter-unit coordinate lands at a slightly different mm position. |
| `SP` (Select Pen) | Valid pens **0–6** | Valid pens **0–8** | Pen 7/8 on a 7475A is out of range; colour/width map size differs. |
| `RO` (Rotate) | Limited rotation support | `RO 0` / `RO 90` full coordinate-system rotation | Engine must rotate P1/P2, clip limits, geometry on 7550A; treat 7475A per its narrower behaviour. |
| `FT` (Fill Type) | Fill types 1–? with fixed hatch options | Fill types 1–6 **plus** user-defined via `UF`; `PT` interaction | 7550A fills can reference a user-defined gap sequence; 7475A cannot. |
| `UC` (User-Defined Char) | Single 6×16 grid; decimal increments ±98.9999; pen-down >+99 | Grid 6×16 / 48×64 / 42×72 by STANDARD/ENHANCED + font; **integer** increments ±98 or ±9998; pen-down threshold mode-dependent | Same mnemonic, different grid resolution **and** different numeric format. See Character-Set reference §2. |
| `CS`/`CA` (designate set) | Set range 0–4, 6–9, 30–39 (19 sets) | −1, 0–19, 30–49 (40 sets + downloadable) | Different valid-set validation and error-5 boundaries. |
| `SI` (Absolute Char Size) | Default A/A4 0.187×0.269 cm, B/A3 0.285×0.375 cm | Default 0.187 (A4/A) / 0.285 (A3/B) cm classes | Different default glyph metrics when `SI;` is issued. |
| `OI` (Output Identification) | Returns `7475A` | Returns `7550A` | The clean way for the engine to self-identify the target model. |
| `OO` / `OS` / `OE` (status/options) | Status bits and option set reflect 6-pen, single-font, no-polygon hardware | Reflect 8-pen, dual-font, polygon, buffered hardware | Status responses must match the emulated model or host software mis-detects capabilities. |
| `PS` (Paper Size) | ISO A4/A3, ANSI A/B selection | media handling tied to page-feed (`PG`/`AF`/`AH`) | Hard-clip limits derive from this; 7550A adds page-advance semantics the 7475A lacks. |
| Label terminators / encoding | HP 7-bit only; shift-in/out within label | HP 7/8-bit + ISO 7/8-bit; GL/GR; linked sets | Same `LB`/`DT` mnemonics, very different character routing. See Character-Set reference §4. |

---

## 5. Parser requirements

1. **Tokeniser.** Read two-letter mnemonics case-insensitively-or-uppercase per spec; collect
   the parameter field until a terminator (`;`, LF on HP-IB, or the start of the next mnemonic).
   Labels (`LB`, `SM` char, `WD`, `BL`) consume raw text until their specific terminator, *not* a
   semicolon — handle these before generic parameter parsing.
2. **Number formats.** Accept integer and (scaling-on) scaled-decimal; reject decimals where only
   integers are allowed. Out-of-range values → error 3 and the instruction is ignored. Model the
   range per target (±32 767 vs ±2²³−1).
3. **Parameter-count errors.** Too few → ignore (error 2); too many → execute with the correct
   count and ignore the rest. Implement the exact count rule per instruction.
4. **Error register.** Maintain the HP-GL error number (0–7) readable by `OE`, and (7550A) the
   device-control error readable by `ESC .E`. Errors don't halt the stream; they're latched.
5. **Modal persistence.** Pen state, plotting mode, scaling, rotation, line type, fill type, pen
   selection, character state all persist until changed or reset by `IN`/`DF`.

### HP-GL error codes (both, readable via OE)

| # | Meaning |
|---|---|
| 0 | No error |
| 1 | Command not recognised (bad/missing mnemonic, alpha where numeric expected) |
| 2 | Wrong number of parameters |
| 3 | Bad (out-of-range) parameter |
| 4 | (unused) |
| 5 | Unknown character set |
| 6 | Position overflow (value exceeds numeric range) |
| 7 | Buffer overflow (7550A: graphics buffer too small) |

---

## 6. Default-state table (implement for IN / DF)

The engine must initialise to these on `IN`, and to the modal subset on `DF`:

- Pen: up, pen 0 (no pen) after `SP;`/`IN`; plotting mode absolute.
- Scaling: off (parameters = plotter units).
- P1/P2: paper-size-dependent defaults.
- Soft-clip window (`IW`): equal to hard-clip limits.
- Line type: solid; line pattern length 4% of P1–P2 diagonal.
- Fill type: type 1 (solid), angle 0°.
- Character set: standard = alternate = set 0; size, slant, direction at defaults
  (slant 0; direction +X). 7550A default char size 0.285 cm (A3/B) or 0.187 cm (A4/A).
- Chord angle: 5°.
- Label terminator: ETX (decimal 3).
- Rotation: 0°.
- Pen thickness (7550A): 0.3 mm.

---

## 7. Minimum viable vs faithful

**Minimum viable renderer** (static plot image from a file):
`IN/DF/IP/SC/RO/IW`, `PU/PD/PA/PR`, `AA/AR/CI`, `EA/ER/RA/RR/FT/PT`, `LT/SP/SM`,
`LB/DT/SI/SR/SL/DI/DR/CP/CS/CA/SS/SA/ES` + the stroke font, clipping, and the coordinate
pipeline of §2. Plus, for 7550A, `PM/EP/FP/UF`. Status/output and device-control can be stubbed.

**Faithful emulator** (byte-identical pen paths and live I/O): add exact chord subdivision
(`CT`/`CC`), the full output/digitize instruction set with correctly formatted responses, the
error register, buffered labels and metrics, downloadable/slot character machinery, replot/page
handling, memory allocation, and (7550A) the `ESC .` device-control and handshake layer.

---

*Sources: HP 7475A Programming Manual; HP 7550A Programming Manual (Appendix C Instruction
Summary, Appendix B Error Messages). Numeric ranges, defaults, and parameter formats are taken
from those manuals' instruction summaries.*
