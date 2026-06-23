# Instrument unit tokens — the value-setting contract (issue #46)

To set a value over the bus you need the instrument's **literal suffix token**, which is
device-specific (megahertz is `MZ` on one HP box, `MH` on another; a SCPI box wants `MHZ`). A
human says "1 GHz"; the server must turn that into the exact thing to send — converting when the
instrument has no token for the spoken unit (a box whose top frequency token is `MZ` needs
`FR 1000 MZ`).

## The contract

A **settable** command parameter declares its accepted tokens as `{token, unit}` objects:

```json
"set": "FR <value> <unit>",
"parameters": [
  { "name": "frequency",
    "units": [
      { "token": "HZ", "unit": "Hz" },
      { "token": "KZ", "unit": "kHz" },
      { "token": "MZ", "unit": "MHz" }
    ],
    "range": "100 kHz to 2060 MHz" }
]
```

- **`token`** — the literal wire suffix, exactly as the manual documents it (case included). This is
  the **device-specific** part.
- **`unit`** — the physical unit the token means, drawn from **one canonical vocabulary** (below). The
  human unit is **consistent across every file**; only the token varies. This is what lets the
  resolver convert: it supplies token→unit, the resolver supplies unit→scale.

A bare-string entry (`"units": ["HZ"]`) is a **legacy / not-yet-audited** token: it parses (so no
file breaks during migration) but its `unit` is null, so the resolver can't use it. The
`AuditedUnitTokens_UseTheCanonicalUnitVocabulary` consistency guard checks every *audited* token uses
a canonical unit; the migration backlog is the bare-string tokens.

## Canonical unit vocabulary

Use these exact spellings in `unit` (micro is ASCII `u` for latin-1 safety). The set lives in
[`UnitResolver`](../src/GpibMcp.Core/Instruments/UnitResolver.cs) and is the single source of truth;
extend it (with a quantity + scale) when an audited instrument needs a unit not yet listed.

| Quantity | Canonical units |
|---|---|
| Frequency | `Hz` `kHz` `MHz` `GHz` `THz` |
| Time | `s` `ms` `us` `ns` `ps` |
| Voltage | `V` `kV` `mV` `uV` `nV` |
| Current | `A` `mA` `uA` `nA` |
| Resistance | `Ohm` `kOhm` `MOhm` |
| Power / level | `dBm` `dBuV` *(log — exact match only, never converted)* |
| Ratio | `dB` *(log — exact match only)* |
| Angle / ratio | `deg` `%` |

Linear units convert within their quantity (`1 GHz` → `1000 MZ`); log/standalone units (`dBm`, `dB`)
only match exactly and are never numerically converted. Scope/DMM units still to be added as their
families are audited: `Vpp`, `V/div`, `s/div`, `PLC`, `digits`, `bps`, … — each needs a quantity+scale
in `UnitResolver` first.

## How Claude uses it

The **`resolve_setting`** tool (`model`, `command`, `value`, `unit`) maps the human value to the exact
string to send — e.g. `resolve_setting(8657B, FR, 1, GHz)` → `Send: FR 1000 MZ`. Claude should call it
to build any set-value command rather than guessing the suffix.

## Migration status

Manual-driven, per model — best done family by family (like the #41 split), with the guard catching
omissions. Done so far: **8657B** (frequency / amplitude / AM / FM). The remaining ~6,700 unit entries
are the backlog; each migrates a parameter's `units` from bare strings to `{token, unit}` against its
manual.
