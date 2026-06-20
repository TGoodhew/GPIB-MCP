# HP-GL Character Set & Font Geometry Reference — HP 7475A & HP 7550A

Companion to the rendering-engine spec. This document covers everything a rendering engine needs
to reproduce **text and user-defined glyphs**: the character-set catalogues, the addressable
glyph coordinate system (the "primitive grid"), the character-cell / layout model, and the
encoding/selection modes. It ends with the per-command 7475A↔7550A differences for the labeling
subsystem.

> **Important note on the built-in glyphs.** The internal stroke fonts (the actual vector shapes
> for `A`, `B`, `?`, etc.) are **not published as coordinate tables** in either manual — both
> manuals are scanned documents that show each set only as a printed glyph chart (bitmap). The
> two ASCII Set 0 charts have been extracted as reference images
> (`7475A_charset0_ascii_chart.png`, `7550A_charset0_ascii_chart.png`). To reproduce the *exact*
> built-in glyph outlines you must either trace them from these charts or substitute a
> single-stroke ("Hershey"-style) font of equivalent metrics. What **is** fully specified — and
> is what you implement against — is the **primitive-grid coordinate system** below, which
> governs `UC` user-defined characters and `DL` downloadable characters and defines the cell
> geometry every glyph is laid out in.

---

## 1. Character-set catalogues

### 1.1 HP 7475A — 19 internal sets, single font

The 7475A has **19 internal character sets** (one font). Set 0 is ANSI ASCII and is the
power-on default for both the standard and alternate set. Valid set numbers for `CS`/`CA`:
**0–4, 6–9, 30–39**. Designating any other number raises HP-GL **error 5** (unknown character
set). There is no second font, no downloadable set, and no slot mechanism.

### 1.2 HP 7550A — 20 sets × 2 fonts (40) + downloadable

The 7550A has **20 character sets, each available in two fonts**, plus a downloadable set (−1).
Each font is addressed as a *separate set number*: the **fixed-space** font is sets 0–9 and
30–39; the **variable-space** font is the same characters in sets 10–19 and 40–49. Sets on the
same row (e.g. 0 ↔ 10, 8 ↔ 18) hold identical characters per decimal code.

| Fixed | Variable | Set name | ISO reg. |
|---|---|---|---|
| 0 | 10 | ANSI ASCII | 006 |
| 1 | 11 | HP 9825 HPL Character Set | — |
| 2 | 12 | French/German | — |
| 3 | 13 | Scandinavian | — |
| 4 | 14 | Spanish/Latin American | — |
| 5 | 15 | Special Symbols | — |
| 6 | 16 | JIS ASCII | 014 |
| 7 | 17 | Roman Extensions | — |
| 8 | 18 | Katakana | 013 |
| 9 | 19 | ISO IRV (International Reference Version) | 002 |
| 30 | 40 | ISO Swedish | 010 |
| 31 | 41 | ISO Swedish for Names | 011 |
| 32 | 42 | ISO Norwegian, Version 1 | 060 |
| 33 | 43 | ISO German | 021 |
| 34 | 44 | ISO French | 025 |
| 35 | 45 | ISO British | 004 |
| 36 | 46 | ISO Italian | 015 |
| 37 | 47 | ISO Spanish | 017 |
| 38 | 48 | ISO Portuguese | 016 |
| 39 | 49 | ISO Norwegian, Version 2 | 061 |
| −1 | — | Downloadable | — |

Two fonts:
- **Fixed-space** — every character occupies equal horizontal space and is drawn with a fixed
  vector count.
- **Variable-space** — each character occupies a width proportional to its shape, and contour
  smoothness is programmable via `CC` (Character Chord Angle).

Sets 5/15 (Special Symbols), 7/17 (Roman Extensions), and 8/18 (Katakana) have distinct
upper/lower glyphs; all other sets share identical uppercase, lowercase, and digits and differ
only in the language-specific extra characters.

---

## 2. The primitive grid — the addressable glyph coordinate system

Every character is laid out on a **character plot cell**. Superimposed on that cell is the
**primitive grid**, a relative coordinate system in which `UC` and `DL` define glyph strokes.
This is the geometry the engine must implement exactly; the cell scales with `SI`/`SR` but the
grid-unit *count* across the cell is fixed per font/mode.

### 2.1 HP 7475A primitive grid (single mode)

- Cell divided into **6 horizontal units × 16 vertical units**.
- The cell is always **2 × current character height** tall and **1.5 × current character width**
  wide (so the drawable cell is larger than one nominal character).
- To match the size of a normal labeled character, draw within a **4 (wide) × 8 (high)** grid
  region anchored at the lower-left.
- `UC` increments: decimal, range **−98.9999 to +98.9999** grid units per move (the spec states
  "> −99 and < +99").
- Pen control values inside `UC`: integer **> +99 = pen down**, **< −99 = pen up**; values
  **> +127.9999 or < −128** raise error 3.

### 2.2 HP 7550A primitive grid (resolution depends on front-panel mode + font)

The 7550A's grid resolution switches with the **STANDARD/ENHANCED** front-panel key and the
selected font:

| Mode / font | Grid units (W × H per cell) | "Normal-size" region |
|---|---|---|
| STANDARD (either font) | 6 × 16 | 4 × 8 |
| ENHANCED, fixed-space | 48 × 64 | 32 × 32 |
| ENHANCED, variable-space | 42 × 72 | 28 × 36 |

- `UC` X,Y increments are **integers in primitive grid units**, ranged by mode:
  STANDARD **−98…98**, ENHANCED **−9998…9998**. They may not exceed the plotter's overall
  range (−2²³…2²³−1).
- `UC` pen control: STANDARD **≥ +99 = pen down, ≤ −99 = pen up**; ENHANCED **≥ +9999 = pen down,
  ≤ −9999 = pen up**.
- `DL` (Define Downloadable Character) writes a glyph into set −1 using the same primitive-grid
  system; character number range **33–126**, pen-control parameter **−128**, X,Y in primitive
  grid units (−127…127).

### 2.3 UC drawing semantics (both models)

1. On entry, `UC` forces pen **up** and sets the grid origin to **0,0** (regardless of prior pen
   state).
2. X,Y increment pairs move the pen relative to the current grid position; right/up positive,
   left/down negative, **relative to the current label direction** (`DI`/`DR`).
3. A pen-down control parameter lowers the pen for subsequent increments until a pen-up control
   or end of instruction.
4. Unmatched X,Y increments → error 2; the rest of the character still draws.
5. On completion the pen is raised and advances one character-space field to the right; the
   prior `PU`/`PD` status is then restored.
6. `UC;` with no parameters moves to the carriage-return point.
7. UC glyphs honour current size (`SI`/`SR`), slant (`SL`), direction (`DI`/`DR`), and mirror
   with negative size parameters exactly like labeled text.

---

## 3. Character-cell layout & text advance (both models)

The engine maintains a text cursor and a CP cell. Glyph advance, spacing, and newlines all work
in CP-cell units:

- **Character space width (W)** and **character space height (H)** define the cell. A nominal
  character is drawn at roughly **0.67 × W** wide and **0.5 × H** tall, leaving inter-character
  and inter-line gaps.
- `CP spaces,lines` moves the cursor by N cell widths / heights **without drawing** and without
  changing pen up/down state. `CP;` with no parameters performs a CR+LF to the carriage-return
  point.
- Embedded **CR** (decimal 13) returns the X cursor to the label-line origin; **LF** (decimal 10)
  advances one cell height along the current direction.
- `ES spaces[,lines]` (Extra Space) adds/removes spacing between characters and lines.
- Label direction (`DI`/`DR`) rotates the entire advance system; CR/LF/CP move relative to the
  label baseline, not the page axes.

### Size instructions

| | `SI` width,height (cm, absolute) | `SR` width,height (% of P1–P2) |
|---|---|---|
| Format | decimal | decimal |
| `SI;`/`SR;` default | reverts to size default (below) | width 0.75, height 1.5 |
| Negative values | mirror the label (−width = right-to-left, −height = top-to-bottom) | same |

Default nominal character size when no `SI`/`SR` is in force:

| Paper | 7475A (W × H) | 7550A (W × H) |
|---|---|---|
| A / A4 | 0.187 × 0.269 cm | 0.187 cm height class |
| B / A3 | 0.285 × 0.375 cm | 0.285 cm height class |

(7550A default reported in its instruction summary as 0.285 cm for A3/B-size and 0.187 cm for
A4/A-size; the 7475A manual gives the full W×H pair shown above.)

---

## 4. Character-set selection / encoding modes

### 4.1 HP 7475A — single 7-bit model

Only the HP 7-bit scheme exists. Designate sets with `CS` (standard) and `CA` (alternate);
select the active one with `SS` (standard) / `SA` (alternate). Within a label string, **shift-out
(decimal 14)** switches to the alternate set and **shift-in (decimal 15)** back to standard.
There are no slots, no GR (high half), no ISO/8-bit modes.

### 4.2 HP 7550A — four selection modes + slots

The 7550A adds a slot system (GO–G3) and four selection modes, set up via `DS` (Designate set
into slot) and `IV` (Invoke slot), controlled by `CM` (Character Selection Mode):

- **HP 7-bit compatibility (default):** like the 7475A — `CS`/`CA`/`SS`/`SA` plus shift-in/out.
  Eighth bit ignored; all sets are 128 characters in GL.
- **HP 8-bit:** links two 128-char sets into a 256-char set; GL holds 0–127, GR holds 128–255.
  Only the linked sets **Roman8** (ANSI ASCII + Roman Extensions) and **Katakana8** (JIS ASCII +
  Katakana) exist. Access GR characters by adding 128 to the decimal code.
- **ISO 7-bit:** eighth bit ignored; on init, set 0 → G0/G1 and set 7 → G2/G3.
- **ISO 8-bit:** full slot/GL/GR addressing.

`CM switch_mode[,fallback_mode]` selects the mode and the fallback behaviour for undefined
printing characters (codes 33–126 and 161–254): default is to ignore them; fallback can instead
draw a box (▯). The downloadable set is undefined until `DL` defines characters into it.

An engine that only targets plain ASCII can implement just HP 7-bit + Set 0 and treat shift-in/out;
full fidelity requires the slot model and the linked-set lifting rules.

---

## 5. Labeling-subsystem command differences (7475A vs 7550A)

| Cmd | 7475A | 7550A | Engine impact |
|---|---|---|---|
| `CS`/`CA` set range | 0–4, 6–9, 30–39 (19 sets, 1 font) | −1, 0–19, 30–49 (20 sets × 2 fonts + downloadable) | Different valid-set validation; error 5 boundaries differ |
| `SS`/`SA` | present | present | same |
| `DS`/`IV` (slots) | **absent** | present | 7550A-only slot routing |
| `CM` (selection mode) | **absent** | present | 7550A-only 7/8-bit & ISO modes |
| `CC` (char chord angle) | **absent** | present (variable-space smoothness) | affects rendered curve vertices on variable font |
| `DL` (downloadable char) | **absent** | present (set −1) | 7550A-only glyph upload |
| `UC` grid | fixed 6×16; incr ±98.9999 (decimal) | 6×16 / 48×64 / 42×72 by mode; incr integer ±98 or ±9998 | different grid resolution & numeric format per mode |
| `UC`/`DL` pen-down threshold | >+99 / <−99 | STANDARD >+99/<−99, ENHANCED >+9999/<−9999 | mode-dependent on 7550A |
| `BL`/`PB`/`OL` (buffered label + metrics) | **absent** | present (≤150 chars) | 7550A-only staged labels |
| `WD` (write to front-panel display) | **absent** | present (≤32 chars) | 7550A-only; no-op for offline render |
| `SI` default size | A/A4 .187×.269, B/A3 .285×.375 cm | .187 (A4/A) / .285 (A3/B) cm classes | different default metrics |
| `SR` default | 0.75 / 1.5 | 0.75 / 1.5 | same |
| `SL`, `DI`, `DR`, `CP`, `ES`, `DT`, `LB` | present, identical semantics | present, identical semantics | shared core |
| Numeric range (all coords) | ±32 767 (16-bit) | ±2²³−1 (24-bit) | global range difference (see main spec) |
| Encoding | HP 7-bit only; shift-in/out | HP 7/8-bit + ISO 7/8-bit; GL/GR; linked sets | major 7550A superset |

---

## 6. What to implement, in order

1. **CP-cell layout + advance** (W/H, CR/LF, `CP`, `ES`, `DI`/`DR`, `SI`/`SR`, `SL`, mirroring).
   This alone places text correctly even before glyph shapes are right.
2. **A stroke font for Set 0** (trace the reference charts or use a metric-matched single-stroke
   font), drawn within the primitive-grid cell.
3. **`UC`/`DL` primitive-grid renderer** (the exactly-specified part) — both the 6×16 base grid
   and, for the 7550A, the ENHANCED 48×64 / 42×72 grids.
4. **Set selection**: 7475A → `CS/CA/SS/SA` + shift-in/out. 7550A → add `DS/IV/CM` slots and the
   7/8-bit + ISO modes, plus the Roman8/Katakana8 linked-set lifting.
5. **Additional international sets** as glyph tables only if you need non-ASCII labels.

---

*Reference images included: `7475A_charset0_ascii_chart.png` (printed p. 5-2, Set 0),
`7550A_charset0_ascii_chart.png` (printed p. 11-4, Set 0 fixed-space + Set 10 variable-space).
Sources: HP 7475A Programming Manual Ch. 5; HP 7550A Programming Manual Ch. 11 and Appendix C.*
