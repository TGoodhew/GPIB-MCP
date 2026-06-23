# Instrument unit tokens — the value-setting contract (issue #46)

To set a value over the bus you need the instrument's **literal suffix token**, which is
device-specific (megahertz is `MZ` on one HP box, `MH` on another; a SCPI box wants `MHZ`). A
human says "1 GHz"; the server must turn that into the exact thing to send — converting when the
instrument has no token for the spoken unit (a box whose top frequency token is `MZ` needs
`FR 1000 MZ`).

## The contract

Each entry in a parameter's `units` array is in one of **three** states:

```json
"set": "FR <value> <unit>",
"parameters": [
  { "name": "frequency",
    "units": [
      { "token": "HZ", "unit": "Hz" },     // tokenful  — a literal suffix goes on the wire
      { "token": "KZ", "unit": "kHz" },
      { "token": "MZ", "unit": "MHz" }
    ] }
]
```

| Shape | Meaning |
|---|---|
| `{ "token": "MZ", "unit": "MHz" }` | **Tokenful, audited** — a literal suffix (`MZ`) is appended after the value on the wire (`FR 1000 MZ`). |
| `{ "unit": "V" }` | **Tokenless, audited** — the instrument takes a **bare number** with no suffix; the value's unit is still recorded so the resolver can convert/validate (it emits just the number). |
| `"HZ"` (bare string) | **Not audited** — legacy shape; parses so nothing breaks, but `unit` is null and the resolver can't use it. |

- **`token`** — the literal wire suffix, exactly as the manual documents it (case included). Omitted
  for tokenless params. This is the **device-specific** part.
- **`unit`** — the physical unit the value means, drawn from **one canonical vocabulary** (below). The
  human unit is **consistent across every file**; only the token varies. This is what lets the
  resolver convert: it supplies token→unit, the resolver supplies unit→scale.

Two consistency guards enforce the contract:
- `AuditedUnitTokens_UseTheCanonicalUnitVocabulary` — every *audited* entry (tokenful or tokenless)
  uses a canonical unit spelling.
- `NoBareUnitString_LooksLikeAKnownUnit` — no **leftover bare string** may normalise to a canonical
  unit. A bare `"V"` is an un-recorded gap (is it audited-tokenless or not-yet-audited?), so it must be
  classified as `{token,unit}` or `{unit}`. Genuine non-units (`div`, `digits`, `code`, `dBm or W`)
  stay bare freely. This blocks new instrument files from re-introducing the ambiguity.

## Canonical unit vocabulary

Use these exact spellings in `unit` (micro is ASCII `u` for latin-1 safety). The set lives in
[`UnitResolver`](../src/GpibMcp.Core/Instruments/UnitResolver.cs) and is the single source of truth;
extend it (with a quantity + scale) when an audited instrument needs a unit not yet listed.

| Quantity | Canonical units |
|---|---|
| Frequency | `Hz` `kHz` `MHz` `GHz` `THz` |
| Time | `s` `ms` `us` `ns` `ps` |
| Voltage | `V` `kV` `mV` `uV` `nV` |
| Voltage (p-p / RMS / peak) | `Vpp` `mVpp` `uVpp` `kVpp` · `Vrms` `mVrms` `uVrms` · `Vpeak` `mVpeak` |
| Current | `A` `mA` `uA` `nA` · `App` `mApp` (p-p) |
| Resistance | `Ohm` `kOhm` `MOhm` |
| Power (linear) | `W` `mW` `uW` `nW` `kW` |
| Power / level (log) | `dBm` `dBuV` `dBW` `dBmV` `dBV` `dBf` *(exact match only, never converted)* |
| Ratio | `dB` `dBc` *(log — exact match only)* |
| Angle | `deg` `rad` `pirad` |
| Percent | `%` |
| Scope per-division | `s/div` `ms/div` `us/div` `ns/div` `ps/div` · `V/div` `mV/div` `uV/div` |
| Sample / bit rate | `sps` `ksps` `Msps` `Gsps` · `bps` `kbps` `Mbps` |
| Standalone (exact only) | `baud` `PLC` `dB/div` `dB/GHz` |

Linear units convert within their quantity (`1 GHz` → `1000 MZ`; `1 V` → `1000 mV`). Peak-to-peak /
RMS / peak voltage convert only **within their own family** — never to/from plain `V` (the relationship
is waveform-shape dependent). Log/standalone units (`dBm`, `dB`, `baud`, `PLC`, …) only match exactly
and are never numerically converted. The set lives in
[`UnitResolver`](../src/GpibMcp.Core/Instruments/UnitResolver.cs); extend it (quantity + scale) when an
audited instrument needs a unit not yet listed.

**Known gaps** (left bare, flagged on #46): `mHz` (millihertz, 8116A) collides with `MHz` under the
resolver's case-folded lookup — needs case-sensitive handling; `K`/`C` (temperature) need a new
Temperature quantity; capacitance `F` / inductance `H` (LCR, VNA cal coefficients) are out of scope.

## How Claude uses it

The **`resolve_setting`** tool (`model`, `command`, `value`, `unit`) maps the human value to the exact
string to send — e.g. `resolve_setting(8657B, FR, 1, GHz)` → `Send: FR 1000 MZ`. Claude should call it
to build any set-value command rather than guessing the suffix.

## Migration status

The fleet-wide audit is **complete** (front-loaded across waves 1–4 + a deterministic tokenless pass).
Every parameter that emits a unit token on the wire carries `{token, unit}`, and every parameter that
takes a bare number carries the tokenless `{unit}` marker. As of #46 Phase 0b:

- **3,058** tokenful `{token, unit}` entries — a literal suffix is sent.
- **2,837** tokenless `{unit}` entries — bare number, unit recorded.
- **420** still bare — genuine non-units (`div`, `digits`, `code`, dual-mode `dBm or W`) and
  needs-vocab tokens (`K`, `C`, `mHz`, farad coeffs); plus **457** empty-string units
  (registers/masks/channel IDs with no unit by nature).

The `NoBareUnitString_LooksLikeAKnownUnit` guard keeps it that way — any new file that ships a bare
unit-looking string fails the build until it's classified.
