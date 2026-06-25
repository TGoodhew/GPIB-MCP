# GPIB-MCP

An [MCP](https://modelcontextprotocol.io) (Model Context Protocol) server that
connects Claude — or any MCP client — directly to your test-and-measurement
instruments over **GPIB, USB-TMC, LXI/TCPIP, and serial**, using the **NI-VISA**
and **NI-488.2** .NET libraries.

It speaks JSON-RPC 2.0 over a stdio transport and exposes a set of tools the model
can call to discover instruments and exchange SCPI / IEEE-488.2 commands with them.

| | |
|---|---|
| **Language / runtime** | C#, .NET Framework 4.7.2 |
| **Platform** | `x86` — see [Why x86?](#why-x86) |
| **Primary path** | NI-VISA (`Ivi.Visa` + `NationalInstruments.Visa`) — works across every bus |
| **Native path** | NI-488.2 (`NationalInstruments.NI4882`) — address GPIB board/primary/secondary directly |
| **Transport** | JSON-RPC 2.0 over newline-delimited stdio |
| **License** | [MIT](LICENSE) |

---

## Contents

- [Features](#features)
- [Prerequisites](#prerequisites)
- [Install](#install)
  - [1. Clone the repository](#1-clone-the-repository)
  - [2. Point the project at your NI assemblies](#2-point-the-project-at-your-ni-assemblies)
  - [3. Build](#3-build)
  - [4. Verify the build](#4-verify-the-build)
- [Configure an MCP client](#configure-an-mcp-client)
  - [Claude Desktop](#claude-desktop)
  - [Other MCP clients](#other-mcp-clients)
- [Usage](#usage)
  - [Tool reference](#tool-reference)
  - [Typical workflow](#typical-workflow)
  - [Error reporting](#error-reporting)
  - [Discovery and bus extenders (HP 37204A)](#discovery-and-bus-extenders-hp-37204a)
  - [Instrument command database](#instrument-command-database)
  - [Screen capture (plot & print)](#screen-capture-plot--print)
  - [Waiting for operations to complete (SRQ)](#waiting-for-operations-to-complete-srq)
  - [Manual test from a terminal](#manual-test-from-a-terminal)
- [Logging](#logging)
- [Why x86?](#why-x86)
- [Project layout](#project-layout)
- [Troubleshooting](#troubleshooting)
- [Extending](#extending)
- [License](#license)

---

## Features

- **Auto-discovery** of every connected VISA resource (GPIB, USB-TMC, TCPIP/LXI, serial).
- **Message-based I/O**: query (write + read), write-only, read, and device-clear.
- **Cached sessions** — an instrument stays open, addressed, and configured across
  multiple tool calls until you explicitly close it.
- **Native NI-488.2 path** to address a GPIB instrument by board / primary / secondary
  without needing a VISA resource alias.
- **User-extensible instrument command database** — tell Claude which model sits at an
  address and it looks up the command reference, confirms identity, and drives it.
- **Screen capture — plot or print** — grab an instrument's actual screen (graticule, trace,
  markers, annotation) as an HP-GL **plot** (vector plotter emulation) or an HP **PCL print**
  (raster printer hardcopy), and show it inline in the chat as an SVG, with a PNG saved to disk.
  Rendering is a standalone, reusable [`Hpgl.Rendering`](src/Hpgl.Rendering/) library (HP-GL/2 **and**
  PCL).
- **SRQ-based operation completion** — wait for an operation to *truly* finish via the bus
  service-request event (data-driven from the model's `statusModel`), instead of guessing with
  a fixed timeout.
- **Single, self-contained executable** — no external MCP SDK dependency; protocol
  handling is implemented directly so it runs cleanly on .NET Framework.

## Prerequisites

You need the following installed on a **Windows** machine:

1. **NI-VISA** runtime, including the **VISA.NET** components
   (provides `Ivi.Visa.dll` and `NationalInstruments.Visa.dll`).
   Download: <https://www.ni.com/en/support/downloads/drivers/download.ni-visa.html>
2. **NI-488.2** driver, including its **.NET** support
   (provides `NationalInstruments.NI4882.dll` and `NationalInstruments.Common.dll`).
   Download: <https://www.ni.com/en/support/downloads/drivers/download.ni-488-2.html>
3. A way to build a .NET Framework 4.7.2 project, **either**:
   - **Visual Studio 2019+** with the *.NET desktop development* workload, **or**
   - the **.NET SDK** (`dotnet` CLI) — the project pulls in
     `Microsoft.NETFramework.ReferenceAssemblies` so the SDK can target net472
     without a full Visual Studio install.

> The NI drivers must be installed regardless of how you build, because the server
> calls into the live NI runtime to talk to hardware.

## Install

### 1. Clone the repository

```bash
git clone https://github.com/TGoodhew/GPIB-MCP.git
cd GPIB-MCP
```

### 2. Point the project at your NI assemblies

The project references four NI driver assemblies by path. Because NI installs them
to versioned folders that differ between machines and driver releases, **verify the
`HintPath` for each reference** in
[`src/GpibMcp/GpibMcp.csproj`](src/GpibMcp/GpibMcp.csproj) and adjust if needed.

| Reference | Typical location |
|-----------|------------------|
| `Ivi.Visa` | `C:\Program Files (x86)\IVI Foundation\VISA\Microsoft.NET\Framework32\v4.0.30319\VISA.NET Shared Components <ver>\Ivi.Visa.dll` |
| `NationalInstruments.Visa` | `C:\Program Files (x86)\IVI Foundation\VISA\Microsoft.NET\Framework32\v4.0.30319\NI VISA.NET <ver>\NationalInstruments.Visa.dll` |
| `NationalInstruments.Common` | `C:\Program Files (x86)\National Instruments\Measurement Studio\DotNET\v4.0\AnyCPU\NationalInstruments.Common <ver>\NationalInstruments.Common.dll` |
| `NationalInstruments.NI4882` | `C:\Program Files (x86)\National Instruments\MeasurementStudioVS2012\DotNET\Assemblies\Current\NationalInstruments.NI4882.dll` |

To find the exact paths on your machine (PowerShell):

```powershell
Get-ChildItem "C:\Program Files (x86)\IVI Foundation\VISA\Microsoft.NET" -Recurse `
  -Include Ivi.Visa.dll, NationalInstruments.Visa.dll | Select-Object FullName
Get-ChildItem "C:\Program Files (x86)\National Instruments" -Recurse `
  -Include NationalInstruments.NI4882.dll, NationalInstruments.Common.dll | Select-Object FullName
```

> These assemblies are typically also registered in the GAC, so in many cases the
> build resolves them even if a `HintPath` is slightly off — but setting the paths
> correctly is the reliable option.

### 3. Build

Build the **solution** (not just the exe project): the GPIB backend (`GpibMcp.NiVisa`) is a separate
assembly that the solution build deploys next to the server exe, so the exe can load it at runtime.

With the **.NET SDK**:

```bash
dotnet build GPIB-MCP.sln -c Release -p:Platform=x86
```

Or with **MSBuild** (from a *Developer Command Prompt* / *Developer PowerShell*):

```powershell
msbuild GPIB-MCP.sln /p:Configuration=Release /p:Platform=x86
```

The resulting executable is:

```
src\GpibMcp\bin\x86\Release\net472\GpibMcp.exe
```

…with `GpibMcp.Core.dll` and `GpibMcp.NiVisa.dll` (the default NI backend) deployed alongside it.

### 4. Verify the build

A clean build should report **0 warnings, 0 errors**.

**Run the unit tests** (no hardware required — they exercise the protocol layer,
tool handlers, and helpers against an in-memory fake instrument manager):

```bash
dotnet test tests/GpibMcp.Tests/GpibMcp.Tests.csproj -c Release
```

All tests should pass. The suite runs in a 32-bit host (configured via
`tests/GpibMcp.Tests/test.runsettings`) to match the x86 server assembly.

Then confirm the server actually starts and speaks the protocol by driving it
directly — again, no instruments or MCP client required.

**a. Confirm the executable was produced:**

```powershell
Test-Path src\GpibMcp\bin\x86\Release\net472\GpibMcp.exe   # -> True
```

**b. Run a protocol smoke test** (PowerShell). This sends `initialize` and `tools/list`
and prints the server's responses; `2>$null` discards the stderr log so you see only the
JSON-RPC traffic on stdout:

```powershell
$exe = "src\GpibMcp\bin\x86\Release\net472\GpibMcp.exe"
@(
  '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{}}}'
  '{"jsonrpc":"2.0","id":2,"method":"tools/list"}'
) -join "`n" | & $exe 2>$null
```

Expected: two JSON lines — an `initialize` result advertising `serverInfo`, followed by
a `tools/list` result listing the available tools (see the [Tool reference](#tool-reference)).
If you get those, the build is good.

**c. Exercise real hardware** (optional, requires connected instruments) — list resources
and read an instrument's identity:

```powershell
$exe = "src\GpibMcp\bin\x86\Release\net472\GpibMcp.exe"
@(
  '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{}}}'
  '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"visa_list_resources","arguments":{}}}'
) -join "`n" | & $exe 2>$null
```

To see the server's internal trace while testing, raise the log level (see [Logging](#logging)):

```powershell
$env:GPIB_MCP_LOG_LEVEL = "Debug"   # then re-run; logs appear on stderr
```

## Configure an MCP client

### Claude Desktop

Add a `gpib` server entry to Claude Desktop's `claude_desktop_config.json`. Use the
**absolute path** to `GpibMcp.exe` on your machine, with **escaped backslashes**:

```json
{
  "mcpServers": {
    "gpib": {
      "command": "C:\\path\\to\\GPIB-MCP\\src\\GpibMcp\\bin\\x86\\Release\\net472\\GpibMcp.exe",
      "env": { "GPIB_MCP_LOG_LEVEL": "Info" }
    }
  }
}
```

If the file already has content (e.g. a `preferences` block), **merge** the `mcpServers`
key into the existing JSON rather than replacing the file.

#### Where the config file lives

The location depends on **how Claude Desktop was installed** — this matters on Windows:

| Install type | Config path |
|--------------|-------------|
| **Standard installer** (downloaded `.exe`) | `%APPDATA%\Claude\claude_desktop_config.json` |
| **Microsoft Store / MSIX package** | `%LOCALAPPDATA%\Packages\Claude_<id>\LocalCache\Roaming\Claude\claude_desktop_config.json` |

The Store/MSIX build runs with a **virtualized AppData**, so it does *not* read the
standard `%APPDATA%\Claude` path — editing that file has no effect. Find the real path
for the packaged build with:

```powershell
# Lists the package roots; the folder name contains the <id>, e.g. Claude_pzs8sxrjxfjjc
Get-ChildItem "$env:LOCALAPPDATA\Packages" -Filter "Claude_*"

# Open (or create) the active config for the packaged app:
$cfg = Get-ChildItem "$env:LOCALAPPDATA\Packages\Claude_*\LocalCache\Roaming\Claude\claude_desktop_config.json"
$cfg.FullName
```

If you are unsure which build you have, check for a running process: a packaged install
runs from `C:\Program Files\WindowsApps\Claude_...`.

#### Apply the change

Claude Desktop only re-reads the config on a **full restart**: quit it from the system
tray (right-click → Quit — closing the window is not enough), then relaunch. The `gpib`
tools then appear and you can ask things like
*"List my instruments, then identify the one at GPIB0::9."*

### Other MCP clients

Any client that launches a stdio MCP server works the same way: run
`GpibMcp.exe` as the server command. The server reads JSON-RPC requests on stdin and
writes responses on stdout (one JSON object per line); all diagnostics go to stderr.

## Usage

The server is **self-describing**: the `initialize` response carries an `instructions` summary the
client loads up front, and the `gpib_overview` tool returns a detailed, structured rundown on demand.
So you can simply ask *"What can the GPIB tool do?"* and get an accurate answer (capability areas,
example asks, and the live tool/model/command counts) rather than a guess assembled from individual
tool blurbs.

### Tool reference

| Tool | Required args | Optional args | Purpose |
|------|---------------|---------------|---------|
| `gpib_overview` | — | — | Describe what the server can do in detail — capability areas, example asks, and the full tool list. Answers *"what can the GPIB tool do?"* |
| `visa_list_resources` | — | `filter` | Discover connected VISA resources |
| `gpib_batch` | `steps` | `sweep`, `on_error`, `preview`, `confirm` | Run a whole multi-step / swept measurement in **one call**: a compact `sweep` (`var`, `from`/`to`/`step`\|`count`) + ordered per-point ops (`set`/`write`/`query`+`as`/`complete`/`wait`, with `{{var}}`/`{{capture}}` interpolation across instruments). The server runs every point and returns one table `{ran, columns, rows, errors}` plus a ready-to-show `summary` line and markdown `table`. Collapses a ~200-call sweep into a single call. `preview:true` reports the plan size without touching the bus; a **large** plan (> ~50 GPIB ops) returns `needs_confirm` with a preview and runs nothing until re-called with `confirm:true` |
| `visa_query` | `resource`, `command` | `timeout_ms`, `read_bytes` | Write a command and read the response (e.g. `*IDN?`) |
| `visa_write` | `resource`, `command` | `timeout_ms` | Write a command with no response (e.g. `*RST`, `OUTP ON`) |
| `visa_write_raw` | `resource`, `data` | `chunk_bytes`, `settle_ms`, `timeout_ms`, `debug` | Write **raw bytes verbatim** (no terminator, no encoding) — `data` is base64. For control-byte-bearing payloads a text boundary would strip (HP-GL with ETX `0x03` label terminators, binary PCL). Pair with `instrument_capture_screen`'s `return_hpgl_base64` to forward a captured plot/print to a plotter/printer byte-for-byte. The send is **paced in bounded chunks** server-side (default 256 B; `timeout_ms` is **per-chunk**) so a large plot doesn't overrun/time out a slow plotter/printer |
| `visa_read` | `resource` | `timeout_ms`, `read_bytes` | Read a pending response |
| `visa_identify` | `resource` | `read_bytes` | Convenience `*IDN?` query |
| `visa_clear` | `resource` | — | IEEE 488.2 device clear (clears I/O buffers). **Caution:** on HP 8560-series analyzers a device clear also **presets** the instrument |
| `visa_list_open` | — | — | List sessions this server holds open |
| `visa_close` | `resource` | — | Close a held-open session |
| `visa_command_history` | `resource` | `max` | Show the recent command chain sent to / received from an instrument |
| `visa_last_error` | — | `resource` | Return the exact, verbatim details (codes + text) of the most recent GPIB/VISA failure |
| `visa_serial_poll` | `resource` | — | Serial-poll the instrument; return the status byte (decimal + hex) and the named bits set |
| `visa_wait_srq` | `resource` | `timeout_ms` | Block until the instrument asserts SRQ, or the backstop timeout expires |
| `instrument_wait_complete` | `resource`, `operation` | `timeout_ms`, `status_model`, `confirm` | The **WAIT** step of the arm → wait → read contract: wait for an operation to truly complete via SRQ (data-driven; no fixed-timeout guess) before reading. If the model's `statusModel` is missing, pass `status_model` to define-and-persist it (proposes first; writes on `confirm=true`, then waits) |
| `gpib488_query` | `primary_address`, `command` | `board`, `secondary_address` | Native NI-488.2 query by board / primary / secondary |
| `instrument_list_models` | — | — | List models in the command database ("what instruments do you know about?") |
| `instrument_reference` | `model` | `command`, `search`, `category` | Browse a model's commands, or (with `command=`) get a read/write **recipe**: `read.send` is the exact query string; `write` gives the template + whether to append a suffix token (→ use `resolve_setting`) or send a bare number. Model-level output also carries a `triggering` **arm → wait → read** contract for swept/triggered measurements |
| `resolve_setting` | `model`, `command`, `value` | `unit` | Map a human value+unit (e.g. 1 GHz) to the exact wire string to send, converting to a token the box accepts (→ `FR 1000 MZ`); see [unit tokens](docs/instrument-unit-tokens.md) |
| `instrument_identify` | `resource` | `read_bytes` | Query identity and match against the database |
| `set_termination` | — (`model` or `resource`) | `read_terminator`, `write_terminator`, `max_read_bytes`, `confirm` | Set a model's read/write terminators and an optional bounded read for free-running instruments (persists on `confirm=true`) |
| `assign_instrument` | `resource`, `model` | `confirm`, `verify` | Record that a model sits at a resource (persists on `confirm=true`) |
| `list_assignments` | — | — | List recorded resource→model assignments |
| `unassign_instrument` | `resource` | `confirm` | Remove an assignment (on `confirm=true`) |
| `instrument_db_save` | `definition` | `confirm` | Add/update a model definition (on `confirm=true`) |
| `instrument_db_refresh` | `model` | `confirm` | Reset a model's user copy to the bundled definition, backing up to `*.bak` (on `confirm=true`) |
| `instrument_capture_screen` | `resource` | `model`, `format` (`plot`\|`print`), `width`, `height`, `background`, `return_hpgl`, `return_hpgl_base64`, `inline_svg`, `fidelity` (`high`\|`low`), `save_dir`, `save_path`, `debug`, `timeout_ms` | Capture the instrument's screen — HP-GL `plot` (vector), PCL `print` (raster), or a direct SCPI image dump (`scpi_block` boxes); returns an SVG to show inline + saves a PNG to Pictures. `return_hpgl_base64` returns the verbatim plot/print bytes (base64) to forward to a plotter/printer via `visa_write_raw` |

Argument notes:

- `resource` — a VISA resource string such as `GPIB0::5::INSTR`,
  `TCPIP0::192.168.1.50::INSTR`, `USB0::0x0699::0x0408::C012345::INSTR`, or `ASRL1::INSTR`.
- `command` — sent verbatim; the assigned model's write terminator (default newline) is appended if you omit one.
- `timeout_ms` — I/O timeout in milliseconds (default `5000`).
- `read_bytes` — optional bounded read: read at most this many bytes instead of reading to the
  terminator/EOI. Leave it unset for normal reads; use it only to stop a **free-running** instrument
  (one that streams output continuously) from timing out — see
  [Free-running instruments and read termination](#free-running-instruments-and-read-termination).
- `board` — GPIB controller index (default `0`).
- `secondary_address` — `0` means "no secondary address" (the default).

### Free-running instruments and read termination

Each model's database record carries a `termination` block (`{ "write": "\n", "read": "\n" }`),
and the server applies it automatically to every query/read once the resource is assigned a model
(`assign_instrument`): writes use the model's write terminator, and reads stop on the model's read
terminator. An unassigned resource falls back to VISA's default (read until EOI).

Some instruments run **free**, streaming readings continuously and never asserting a normal
end-of-response. A plain read then blocks until the timeout. Two complementary levers fix this:

1. **Read terminator** — the primary lever. If the instrument delimits each reading with a known
   character, set it with `set_termination` so reads return at that delimiter.
2. **Bounded read (`max_read_bytes` / `read_bytes`)** — the backstop for instruments with no usable
   terminator. The read returns as soon as that many bytes have arrived (and keeps whatever partial
   data was received if the instrument falls silent first). Use it per-call via the `read_bytes`
   argument, or persist a per-model default with `set_termination max_read_bytes=…` so identity and
   queries for that instrument are always bounded.

`set_termination` proposes the change first and only writes (a minimal user-database override) when
called again with `confirm=true`, like the other database writers.

### Typical workflow

1. `visa_list_resources` → see what is connected.
2. `visa_identify` (or `visa_query` with `*IDN?`) → confirm which instrument is which.
3. `visa_write` / `visa_query` → configure and measure.
4. `visa_close` → release the instrument when finished.

Sessions are cached, so steps 2–3 reuse the same open connection automatically.

### Error reporting

When a GPIB/VISA operation fails, the tool result is an `isError` message that explains what
happened rather than a bare exception string. It names the failing operation, the resource, and
the command, decodes the **VISA status** to a readable name + meaning (e.g. `VI_ERROR_TMO —
Timeout: the instrument did not respond…`, `VI_ERROR_NLISTENERS — nothing acknowledged at this
address…`), and appends the **recent command chain** sent to that instrument so the cause is
visible. The server keeps a bounded per-resource history (default 20 entries, override with the
`GPIB_MCP_HISTORY_DEPTH` environment variable); fetch it any time with `visa_command_history`.

That friendly summary is the first-level response. When you want the **exact** error — the raw
numeric VISA status code (hex + decimal), the decoded name, the underlying driver exception text,
the timestamp, and the command chain — ask for it (e.g. *"tell me the exact error codes and
text"*) and the model fetches it via `visa_last_error`:

```
GPIB/VISA error detail
  Operation  : Query
  Resource   : GPIB0::29::INSTR
  Command    : *IDN?
  VISA status: VI_ERROR_TMO  (0xBFFF0015 / -1073807339)
  Meaning    : Timeout - the instrument did not respond in time. …
  Exception  : Ivi.Visa.IOTimeoutException: …
  Time       : 2026-06-19 21:46:50

Recent command chain for GPIB0::29::INSTR (-> sent / <- received):
  21:46:33.258  -> "*IDN?\n"
```

### Discovery and bus extenders (HP 37204A)

`visa_list_resources` performs **bus-level presence detection** — it does not query
instruments. An address appears because a listener acknowledges on the bus, not because
it answered `*IDN?`. (Use `visa_identify` to actually read an instrument's identity.)

This matters with **HPIB bus extenders such as the HP 37204A**, which acknowledge *every*
GPIB address whether or not an instrument is connected. Discovery then reports a
"phantom-full" bus that cannot be trusted.

The server detects this: when the number of GPIB resources reaches the phantom threshold
(default **15**; a physical GPIB segment supports at most ~15 devices), `visa_list_resources`
appends a warning instructing the assistant to ask you (1) whether a bus extender is in use
and (2) which GPIB addresses are actually in use — then to verify each with `visa_identify`.
Non-GPIB resources (USB / TCPIP / serial) are unaffected.

Tune or disable the threshold with the `GPIB_MCP_PHANTOM_GPIB_THRESHOLD` environment
variable (set it very high to suppress the warning).

### Instrument command database

The server ships with a **user-extensible database of instrument command references**, so
you can tell Claude *"an 8563E is at GPIB 18"* and it can look up what that instrument
understands, confirm its identity, and drive it — instead of you supplying raw commands.

It comes prepopulated with **165 instrument models** (HP/Agilent, Keithley, Tektronix, Rigol,
Rohde & Schwarz, Datron), each carrying its full documented command set — see the
[catalog](data/instruments/README.md). Each model is one JSON file describing its identity
query, command mnemonics, parameters, units, and examples (see
[`data/instruments/`](data/instruments/)). Sibling models that share a manual's command set
(e.g. the 5350A/5351A/5352A, or the Rigol DP800 line) each get their own definition so they
can be assigned and identified individually.

**Ask what's known:**
> *"Please tell me what GPIB instruments you know about."* → `instrument_list_models`

**A worked example** — *"Get the current frequency of my 3325A and set the center
frequency of my 8563E to that, with a span of 100 MHz":*

1. `assign_instrument` (resource `GPIB0::7::INSTR`, model `3325A`) and
   (`GPIB0::18::INSTR`, `8563E`) — Claude confirms with you, then persists on `confirm=true`.
2. `instrument_reference` for each model → Claude learns `FR?` reads the 3325A frequency and
   `CF`/`SP` set the 8563E center frequency and span.
3. `visa_query GPIB0::7::INSTR "FR?"` → reads the frequency.
4. `visa_write GPIB0::18::INSTR "CF <freq>;SP 100MHZ"` → sets it.

#### Where it lives

| Item | Location | Override |
|------|----------|----------|
| Bundled defaults | `<exe dir>\data\instruments\*.json` (ships with the build) | — |
| User database | `%LOCALAPPDATA%\GpibMcp\instruments` | `GPIB_MCP_INSTRUMENT_DB` |
| Assignments | `%LOCALAPPDATA%\GpibMcp\bindings.json` | `GPIB_MCP_BINDINGS` |

On first run the bundled defaults are **copied into the user database** (never overwriting
your edits), giving you an editable, prepopulated database. User definitions override
bundled ones with the same model name — but **per top-level block**, not whole-file: a user
copy that predates a bundled improvement (say it has no `statusModel` yet) still inherits that
new block from the bundled default, while your own blocks keep winning. So shipped *additions*
reach an existing user copy automatically, with no hand-merging. A shipped *change to a value
you already override* is not auto-merged (that would mix old and new fields); pull it in
deliberately with `instrument_db_refresh <model>`, which backs your copy up to `<file>.bak` and
restores the bundled definition.

#### Extending the database

You own the database and can grow it. Two ways to add a model:

- **Ask Claude:** *"Add a definition for my <instrument>…"* → `instrument_db_save` writes a
  `<model>.json` into the user database directory (confirming before it writes).
- **By hand:** drop a `<model>.json` into the user database directory, following the shape of
  any file in [`data/instruments/`](data/instruments/).

New or changed definitions are picked up the next time the server starts.

#### Correcting it — making the database reflect reality

The bundled definitions come from manuals, which can differ from the instrument actually on
your bench (firmware revisions, options, OCR slips, or a model the manual only partly covers).
Keep the database honest:

- **Verify identity automatically.** `assign_instrument` sends the model's identity query and
  checks the response; if it *doesn't* match, it tells Claude so before anything is saved —
  surfacing a wrong model/address or a bad `matchRegex`.
- **Instruments with no identity query.** Many legacy HP-IB units (e.g. the listen-only 8657B)
  cannot report what they are. Mark these with `"identity": { "supported": false, "description":
  "…" }`. The server then reports identity as unavailable and **skips verification with a clear
  note** instead of guessing or timing out, rather than leaving the block silently absent.
- **Override, don't edit the bundled file.** A `<model>.json` in your **user** directory
  overrides the bundled one of the same name. Fix a wrong command, add a missing one, or
  correct an identity pattern there (or via `instrument_db_save`) and your version wins —
  bundled defaults stay intact and your correction survives upgrades. New blocks added to a
  bundled definition still flow through to your copy (per-block merge, above); to discard your
  override and take a corrected bundled definition wholesale, use `instrument_db_refresh`.
- **Confirm-before-write everywhere.** `assign_instrument`, `unassign_instrument`, and
  `instrument_db_save` all report what they *would* do first and only persist when called
  again with `confirm: true`, so nothing changes on disk without your go-ahead.

> Partial / missing by design: a few instruments whose programming guide isn't in the manual
> library (e.g. N9320A, DSA800, full E4436B/E4406A) are intentionally absent or partial rather
> than guessed. Add the relevant programming guide and ask Claude to extract it, or author the
> JSON yourself.

### Screen capture (plot & print)

For instruments that can hardcopy their screen (e.g. the **HP 8563E** spectrum analyzer),
`instrument_capture_screen` grabs the **actual screen** — graticule, trace, markers, and
annotation. Ask Claude:

> *"Capture the screen of the analyzer at GPIB0::18 and show it to me."*

The tool supports **three** capture methods, selected by the model's profile:

- **`plot`** (default, HP-GL boxes) — an HP-GL **plotter** dump (vector). The server plays an HP 7470A:
  it sends the model's `plotCommand`, answers the instrument's `OS` status handshake, and collects the
  HP-GL, which [`Hpgl.Rendering`](src/Hpgl.Rendering/) renders to a PNG **and** a compact vector **SVG**.
- **`print`** (HP-GL boxes) — an HP **PCL** raster **printer** dump (the format the instrument would
  send to a ThinkJet/PaintJet/LaserJet). The server sends the model's `printCommand`, reads the raster
  stream, and [`PclRenderer`](src/Hpgl.Rendering/PclRenderer.cs) decodes it (all PCL 5 compression
  methods — unencoded, run-length, TIFF PackBits, delta-row, adaptive — plus embedded HP-GL/2) to a PNG.
  Bench-verified on a real 8563E (regression fixture `Test/test-print.pcl`).
- **SCPI image** (`method: "scpi_block"`, modern boxes like Rigol scopes) — the instrument returns the
  screen *directly as an image*. The server queries the model's `dumpCommand` (e.g. `:DISP:DATA?`),
  strips the IEEE 488.2 `#<n><len>` block header ([`Ieee4882Block`](src/GpibMcp.Core/Instruments/Ieee4882Block.cs)),
  and saves the screenshot. A full-colour screenshot is too large to paste verbatim as an inline
  artifact (base64 stalls the model above a few KB), so [`ScreenImage`](src/Hpgl.Rendering/ScreenImage.cs)
  shows a small **black & white** inline preview — 1-bit compresses like the PCL print, so a useful-size
  thumbnail (~360 px) fits the safe paste budget — while saving the **full-resolution, full-colour** PNG
  to disk (the result tells the user where). Bench-verified on a real Rigol DS1104Z (`:DISP:DATA?` →
  800×480 BMP).

**Which format (HP-GL boxes)?** Say *"**show** the screen"* → plot. Say *"**capture** the screen"* (or
leave it ambiguous) and, if the model can print, Claude asks **plot** vs **print** before capturing.
Pass `format="plot"`/`"print"` to be explicit. SCPI-image boxes have one path (no `format`).

- The model is taken from the resource's [assignment](#instrument-command-database), or pass `model=`.
- Only models with a `capture` profile in the database are supported. HP-GL: `{ "method": "hpgl",
  "plotCommand": "...", "printCommand": "...", "preRoll": "...", "postRoll": "..." }` (omit
  `printCommand` for plot-only). SCPI image: `{ "method": "scpi_block", "dumpCommand": ":DISP:DATA?" }`.
  VNA record-loop (8720/8753): `{ "method": "outpplot", "dumpCommand": "OUTPPLOT" }` — the dump command is
  sent once and the instrument streams its whole plot as many EOI-bounded HP-GL records (its IP/SC scale
  header first, then geometry), read until the bus goes quiet. The native header gives the correct
  landscape aspect and text, exactly as KE5FX does.
- **Your settings are preserved.** The capture does *not* device-clear the instrument afterward — on
  HP 8560-series analyzers a device clear also presets the box, which would wipe your setup on every
  capture. The 8563E profile's `preRoll` takes a single sweep for a clean plot and its `postRoll`
  (`CONTS;`) resumes continuous sweeping, so the display isn't left frozen.
- `return_hpgl=true` also returns the raw source as **text** (HP-GL/2 for a plot, PCL for a print); `background`,
  `width`, `height` tune the image. **`return_hpgl_base64=true`** returns the same source as **base64** — the
  exact bytes, control characters and all (HP-GL ETX `0x03` label terminators, binary PCL). Feed that to
  `visa_write_raw` to forward the plot/print to a plotter/printer **byte-for-byte** (e.g. an 8563E screen to a
  7090A plotter at addr 6). A text round-trip would strip those control bytes and run the labels together.
- Every capture is also **saved to a PNG file** — by default in your **Pictures** folder
  (`…\Pictures\GpibMcp Captures`). Say *"…and store it in `C:\path\to\folder`"* to choose where
  (`save_dir`), or pass `save_path` for a full filename. The saved path is reported in the result.
- **Forwarding a plot/print is by *reference*, not by re-sending the bytes (#79).** Every plot/print capture
  also retains its exact forwardable bytes server-side under `%LOCALAPPDATA%\GpibMcp\captures\` (overridable via
  `GPIB_MCP_CAPTURES_DIR`, pruned to the most recent 50) and returns that path as a small **handle**. To send it
  to a plotter/printer, `visa_write_raw` takes that handle as **`path=`** (mutually exclusive with `data=`):
  the server reads the file and streams it verbatim — so the plot/print **never round-trips through the model**
  as tens of KB of base64, which was the dominant multi-minute forwarding delay. This carries a binary **PCL**
  print byte-for-byte too — NUL (`0x00`), ESC framing and 8-bit raster intact — which a text boundary can't
  (#71). `return_hpgl_base64` is still there for the rare bytes-needed case, but isn't how you drive a device.
- **Print a capture to a Windows printer (#83).** `print_capture_to_windows` spools a capture handle (typically a
  `format="print"` PCL hardcopy) to a printer the PC already knows about — a local/network print queue — using
  Windows **RAW** spooling (`winspool`), so the printer's own interpreter renders it. Call it with `list=true`
  (or no `path`) to enumerate the installed printers and the default, then pass `path=<handle>` and
  `printer=<name>` (omit `printer` for the default). The bytes go disk → spooler, never through the model.
  *Caveat:* RAW needs the queue to understand the instrument's page language (older **PCL**); a modern PCL laser
  usually works, but a non-PCL / host-based (GDI-only) printer prints blank or garbled — for those, internal
  render-then-print is the route (#85, planned). To send to a plotter/printer **on the GPIB bus** instead, use
  `visa_write_raw(path=…)`.
- **Read-glitch robust.** A plot streams in timeout-bounded chunks, and a byte occasionally dropped at a
  chunk seam (the NI driver / a GPIB bus extender) shortens one trace coordinate — e.g. `995` → `95` —
  which would otherwise draw a stray pen excursion to the page edge. Two defences (#79): the capture reads
  in **fewer, larger chunks** to minimise seams, and a **repair pass** restores any single corrupted trace
  X from its neighbours (a trace's X is a strictly increasing regular grid, so an out-of-order point is
  unambiguous) — keeping the genuine amplitude sample — before the image is rendered **and** before the
  bytes are handed back, so a plot forwarded to a real plotter is clean too. Graticule lines and amplitude
  peaks are never touched. The verbatim `debug:true` dump keeps the *unrepaired* capture for diagnosis.

#### Showing it inline in the chat

Claude Desktop does **not** render MCP tool-result *image* blocks inline in the conversation — a
[known client limitation](https://github.com/anthropics/claude-ai-mcp/issues/238) (the image is
visible to the model and, at best, buried inside the expandable tool-call block). It **does**,
however, render **artifacts** inline. So the tool returns the capture as an SVG and asks Claude to
paste it verbatim into an `image/svg+xml` artifact — which appears inline. The spec-correct PNG image
block is still included (for the model's vision and for clients that do render it), and the PNG is
always saved to disk regardless.

The SVG is built to be small so the model re-emits it as an artifact quickly (#23): strokes are
coalesced into one `<path>` per pen colour and a long trace is sub-pixel-simplified — a real 8563E
capture drops from ~21 KB to ~7 KB with no visible change. The root is a pure `viewBox` (no fixed
size) so the artifact scales to fit its panel instead of clipping.

- **`fidelity`** (plot only) picks the inline label rendering: **`high`** = the exact HP single-stroke plotter
  font (most faithful to a real 7475/7440/7550; ~7 KB); **`low`** = simple text labels (~4 KB, renders
  noticeably faster — only the label font differs, the trace/graticule are identical). The PNG is
  always the exact stroke font. On the first capture the tool asks the user which they prefer and
  Claude then passes their choice on every capture; say *"use low-fidelity captures"* to switch.
- `inline_svg=false` falls back to the image-block + saved-file behaviour only.
- Preview the look by opening [`Test/test.svg`](Test/test.svg) in a browser.

> The capture/render technique is derived from the HP7470A Plotter Emulator (`7470.cpp`) by
> John Miles, KE5FX — <http://www.ke5fx.com/>.

### Waiting for operations to complete (SRQ)

A fixed timeout is a poor proxy for "done" — it reads half-complete sweeps or pads every point with
slack. GPIB instruments instead assert **SRQ** (service request) when an operation finishes, and the
server can wait on that bus event:

> *"Take a sweep on the analyzer at GPIB0::18 and wait until it's actually complete."*

`instrument_wait_complete(resource, operation)` is **data-driven** by the model's `statusModel` in the
database (so SRQ masks are never hardcoded). It resolves the assigned model, pre-clears any stale
status, arms the operation's SRQ mask, starts the operation, and confirms completion by **polling the
latched status byte** until the expected (or error) bit appears — then clears the mask. It returns the
instant the operation truly completes, with the timeout only as a backstop. (Polling the latched status
byte is the reliable read — the bits stay set until read — and avoids the race where an SRQ event can
clear the cause before a separate poll reads it; `visa_wait_srq` remains available as a pure event
primitive.) Three explicit states, no silent guessing:

- model declares `srqSupported: false` → the tool **refuses** (no timed fallback);
- `statusModel`/operation missing → it **asks** you for the definitions, rather than guessing.
  Supply them back as `instrument_wait_complete`'s `status_model` argument to **define-and-persist
  in one step**: the tool proposes the save first, writes it to the model's user-DB record on
  `confirm=true` (merged over any existing `statusModel`), and then proceeds with the wait — the
  same confirm-to-save shape as `assign_instrument`. (Editing the model via `instrument_db_save`
  works too.)
- complete → it runs the SRQ flow above.

The 8563E ships with `sweepComplete` and `sweepAndPeak` operations; the lower-level
`visa_serial_poll` and `visa_wait_srq` tools are available standalone for debugging.

The `statusModel` is **self-describing** so the waiter never guesses, and it is the **only** place
instrument-specific completion knowledge lives — the waiter ([`CompletionWaiter`](src/Srq.Completion/CompletionWaiter.cs))
contains no per-device logic, so adding a new SRQ instrument is pure data (see below).

#### The two completion strategies

The waiter picks a strategy from the model, never from the device identity:

- **SRQ-edge** — used when the model names a `requestServiceBit` (the GPIB request-service bit, `0x40`).
  The waiter disarms and drains stale status, arms `expectBit|errorBit` (**never** the request-service
  bit — arming that self-fires), **waits for the operation to go busy** (the expect bit clears) so a
  condition that is already true at arm-time can't be read as "done", then treats the next
  request-service assertion as completion and classifies by the error bit. Most robust; the 8563E uses
  it. Found the hard way on a real 8563E: the 8560 RQS mask and the read-back status byte share one
  layout (Programming Guide Table 7-266) where `0x40` is request-service — *not* an error — so a naïve
  "poll the expect bit" model misread every successful sweep as an error and could pre-fire on a
  standing end-of-sweep.
- **direct-bit** — used when there is no `requestServiceBit`. The waiter arms the mask and polls the
  `expectBit` (or error bit) directly. Use this when request-service is unavailable or unreliable — the
  3325 uses it, because its require-service bit only asserts in the unit's physical *Enhancements* mode
  (a front-panel switch). Safe here because its `stop` bit clears on a serial poll and isn't a standing
  idle condition. (`requestServiceBit` is deliberately **not** defaulted to `0x40`: that would force
  SRQ-edge onto instruments like the 3325 where `0x40` never asserts, turning a working completion into
  a timeout. It is an explicit per-instrument opt-in.)

#### Adding a new SRQ instrument (no code changes)

Add a `statusModel` block to the instrument's `data/instruments/<model>.json`:

| field | meaning |
|---|---|
| `srqSupported` | `false` → the tool refuses (no timed fallback). |
| `enableMask.setCommand` / `clearCommand` | arm/clear the SRQ mask, with a `{mask}` placeholder (e.g. `"RQS {mask}"`, `"ESTB {mask}"`). |
| `serialPoll.clearsRqs` | whether a serial poll clears RQS. |
| `errorBit` | name (in `bits`) of the failure bit (e.g. `error`, `fail`). |
| `requestServiceBit` | name of the GPIB request-service bit (usually `64`/`0x40`) → enables the robust **SRQ-edge** flow. Omit to use **direct-bit**. |
| `busyConfirmMs` | *(SRQ-edge, optional)* override the busy-confirm timeout for slow-to-start operations. |
| `bits` | named status-byte bits → decimal weights, **as read back by a serial poll** (this is what the waiter decodes). |
| `operations.<name>` | `{ arm, expectBit[, restore] }` — the commands that start the operation, the bit that signals it, and an optional restore. |

Then verify on real hardware with [`SrqHwHarness`](tools/SrqHwHarness/) (and its `raw`/`probe` mode to
characterise the status byte first) — the same code path the server uses. The 8563E (SRQ-edge) and the
3325 (direct-bit) are worked examples that share the entire implementation, differing only in JSON.

The completion state machine is a **standalone library**,
[`src/Srq.Completion/`](src/Srq.Completion/) — decoupled from VISA and the MCP server via
`IStatusChannel`, so it can be exercised headlessly. Three ways to run it:

- **`CompletionWaiterTests`** drive the real waiter against `SimulatedInstrument` (a virtual-clock
  8560 model) — deterministic regression coverage of the timing/race-sensitive logic.
- **[`SrqHarness`](tools/SrqHarness/)** is a console app that runs the headline scenarios (incl. the
  5 s sweep, a stale-bit case, an uncal error, and a timeout) against the simulator and prints a
  **live trace** of every command and status poll, so the pattern can be watched end-to-end without
  hardware. Run it with `dotnet run --project tools/SrqHarness` (exit code 0 = all scenarios passed).
- **[`SrqHwHarness`](tools/SrqHwHarness/)** drives the **same waiter against a real instrument over
  NI-VISA**, with no Claude Desktop and no MCP/stdio layer in the path. It reuses the production
  `VisaInstrumentManager`, the bundled+user instrument database (for the `statusModel`), and the same
  `IStatusChannel` adapter the server uses — so a green run here means `instrument_wait_complete` will
  behave identically. It must build/run **x86** (NI VISA.NET is 32-bit). Examples:

  ```powershell
  # discover the bus first
  dotnet run --project tools/SrqHwHarness -- --list

  # confirm a real 8563E sweep completes (dial in a slow 5 s sweep so timing is observable)
  dotnet run --project tools/SrqHwHarness -- GPIB0::18::INSTR sweepComplete --setup "CF 300MHZ;SP 100MHZ;ST 5S;"

  # 3325 stop-sweep completion
  dotnet run --project tools/SrqHwHarness -- GPIB0::10::INSTR sweepComplete --model 3325A
  ```

  The model is resolved from the saved assignment (like the server), or overridden with `--model`.
  Exit code: `0`=Completed, `2`=InstrumentError (e.g. the uncal `0x50` case), `3`=TimedOut, `4`=Refused/NeedsDefinition.

### Manual test from a terminal

You can drive the server directly without an MCP client by piping JSON-RPC frames
(one per line) into it. PowerShell:

```powershell
$exe = "src\GpibMcp\bin\x86\Release\net472\GpibMcp.exe"
@(
  '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{}}}'
  '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"visa_list_resources","arguments":{}}}'
) -join "`n" | & $exe
```

You should see an `initialize` result followed by a list of discovered resources.

## Logging

All diagnostics are written to **stderr** (stdout is reserved exclusively for MCP
JSON-RPC traffic). Each line is timestamped (UTC) and tagged with a severity level:

```
2026-06-19T19:42:10.847Z [gpib-mcp] INFO: initialize from client 'audit-test' (protocol 2025-06-18)
```

Set the minimum level with the `GPIB_MCP_LOG_LEVEL` environment variable. Valid values,
from least to most verbose: `Error`, `Warn`, `Info` (default), `Debug`.

| Level | What it shows |
|-------|---------------|
| `Error` | Fatal/unhandled failures only |
| `Warn` | + recoverable problems (failed tool calls, dispose errors) |
| `Info` | + lifecycle (startup, client connect, sessions opened/closed) |
| `Debug` | + every JSON-RPC frame in/out and every raw instrument read/write |

`Debug` is the level to use when troubleshooting instrument communication — it logs the
exact bytes sent to and received from each instrument (with control characters escaped).

When configured in an MCP client, set it alongside the command, e.g. for Claude Desktop:

```json
{
  "mcpServers": {
    "gpib": {
      "command": "C:\\path\\to\\GPIB-MCP\\src\\GpibMcp\\bin\\x86\\Release\\net472\\GpibMcp.exe",
      "env": { "GPIB_MCP_LOG_LEVEL": "Debug" }
    }
  }
}
```

### Diagnostic logs

Three best-effort logs are appended under `%LOCALAPPDATA%\GpibMcp\` for after-the-fact inspection,
**independent of `GPIB_MCP_LOG_LEVEL`** (they are always written, not gated by the stderr log level):

- **`tool-calls.log`** — one audit line per MCP tool call: timestamp, status (`ok`/`ERR`), elapsed ms, tool
  name, and a compact digest of the arguments. The always-on record of *what was called*, so a whole turn can
  be reconstructed afterwards — e.g. to count single-op calls versus one `gpib_batch`, and to total the
  non-batched time against a batched run. Overridable with `GPIB_MCP_TOOL_CALL_LOG`.
- **`batch-timing.log`** — per `gpib_batch` run: a per-op-type breakdown (`write`/`query`/`set`/`complete`/`wait`
  counts, total/mean/max ms, and each op type's share of the wall-clock), hotspot first — so a slow sweep shows
  where the time actually went (typically the SRQ completion wait, an unavoidable instrument cost) (#58).
  Overridable with `GPIB_MCP_BATCH_TIMING_LOG`.
- **`capture-timing.log`** — per screen-capture: instrument warm-up vs. streaming vs. tail, and every read (#53).
  Overridable with `GPIB_MCP_CAPTURE_TIMING_LOG`.

Opt-in raw dumps: passing `debug:true` to `instrument_capture_screen` or `visa_write_raw` (e.g. when you say
"capture/send … **with debug**") writes the **verbatim** HP-GL/PCL bytes to `%LOCALAPPDATA%\GpibMcp\debug\`
(override `GPIB_MCP_DEBUG_DIR`) so the exact stream can be inspected to diagnose plot/render glitches.

Retained captures: every plot/print capture also keeps its forwardable bytes under `%LOCALAPPDATA%\GpibMcp\captures\`
(override `GPIB_MCP_CAPTURES_DIR`, pruned to the most recent 50) so a plot can be forwarded to a plotter
**by reference** via `visa_write_raw(path=…)` — see the screen-capture section (#79).

## GPIB backends

Wire-level I/O sits behind a single abstraction, **`IGpibTransport`**, so the adapter is pluggable.
The default backend is **NI-VISA / NI-488.2** (`GpibMcp.NiVisa`), selected unless you say otherwise:

| `GPIB_MCP_BACKEND` | Backend |
|--------------------|---------|
| *(unset)* / `nivisa` | NI-VISA + NI-488.2 (default) |
| `prologix`, `ar488` | reserved — abstraction is in place; backends are a follow-up |

The NI dependency lives **only** in `GpibMcp.NiVisa`, which the server loads at runtime — so
`GpibMcp.Core` and the exe build and run without NI-VISA installed when another backend is selected.
Adding a Prologix/AR488 (or any) adapter means implementing `IGpibTransport` in its own project, with
no changes to the tools, the instrument database, or the MCP plumbing — see
[docs/adding-a-gpib-backend.md](docs/adding-a-gpib-backend.md).

## Why x86?

NI's VISA.NET assemblies are commonly installed under a 32-bit
(`Framework32`) folder only. To bind against them, the server is configured to
build and run as a **32-bit (x86)** process (`<PlatformTarget>x86</PlatformTarget>`
in the project file). If your installation provides 64-bit VISA.NET assemblies and
you prefer a 64-bit build, update the `HintPath`s to the 64-bit assemblies and
change the platform accordingly.

## Project layout

```
GPIB-MCP.sln
LICENSE
README.md
.editorconfig                      shared code-style settings
src/GpibMcp/                       the server EXE - entry point + stdio only (no backend ref)
  GpibMcp.csproj                   net472 / x86; references GpibMcp.Core, no NI
  Program.cs                       entry point + stdio/UTF-8 setup
src/GpibMcp.Core/                  backend-neutral core (no driver dependency; builds without NI)
  Diagnostics/
    Log.cs                         leveled stderr logger (GPIB_MCP_LOG_LEVEL)
  Mcp/
    McpServer.cs                   JSON-RPC 2.0 dispatch (initialize / tools / ping)
    McpTool.cs                     tool + registry + error types
  Instruments/
    IInstrumentManager.cs          tool-facing instrument abstraction (enables testing)
    InstrumentManager.cs           backend-neutral manager (history, errors, capture, the bus lock)
    IGpibTransport.cs              the wire-level backend seam + capabilities + GpibStatus (#22)
    TransportFactory.cs            selects/loads the backend by GPIB_MCP_BACKEND (NI default)
    IoSpec.cs                      per-call I/O behaviour (terminators + bounded read)
    CommandText.cs                 shared command-termination + log helpers
    CommandHistory.cs              bounded per-resource command/response trace
    InstrumentDefinition.cs        instrument command-reference data model
    InstrumentDatabase.cs          loads/indexes model definitions
    AssignmentStore.cs             persistent resource->model assignments
    InstrumentPaths.cs             DB/bindings paths + install-time prepopulation
    ScreenCapture.cs               HP-GL plot handshake + PCL print-stream capture
    Ieee4882Block.cs               strips the #<n><len> header off a SCPI binary block (#10)
  Tools/
    ToolArgs.cs                    shared JSON-Schema + argument helpers
    InstrumentIo.cs                resolves a model's IoSpec (terminators + bounded read)
    InstrumentTools.cs             VISA / native-GPIB + serial-poll / wait-SRQ tools
    DatabaseTools.cs               command-database + assignment + set_termination tools
    CaptureTools.cs                screen-capture + wait-complete tools
src/GpibMcp.NiVisa/                the default GPIB backend - the ONLY project that references NI
  NiVisaTransport.cs               NI-VISA / NI-488.2 IGpibTransport (loaded at runtime)
  VisaErrorInfo.cs                 VISA status decoding for friendly/exact errors
src/Hpgl.Rendering/                standalone HP-GL/2 + PCL + image -> Bitmap/PNG/SVG (no GPIB deps)
  HpglParser.cs / HpglRenderer.cs  HP-GL/2 plot: parse + render pipeline (auto-fit, fills, arcs, labels)
  PclRasterDecoder.cs / PclRenderer.cs  PCL print: raster decode (all compression methods) -> Bitmap/PNG/SVG
  ScreenImage.cs                   SCPI image dump: normalize to PNG + bounded inline thumbnail (#10)
  StrokeFont.cs                    single-stroke vector font (generated from the KE5FX vchar table, #31)
src/Srq.Completion/                headless SRQ completion state machine (no VISA/MCP deps)
  CompletionWaiter.cs              SRQ-edge / direct-bit waiter
  StatusModel.cs / IStatusChannel.cs  data model + transport abstraction
data/instruments/*.json            bundled instrument command database (165 models)
  README.md                        auto-generated catalog (tools/gen_instrument_catalog.py)
tools/HpglViewer/                  WinForms HP-GL viewer (side-by-side vs hp2xx reference)
tools/SrqHarness/                  console SRQ scenarios against a simulated 8560
tools/SrqHwHarness/                run the real waiter against live hardware over NI-VISA
tools/CaptureHarness/              capture a real plot/print over NI-VISA -> raw bytes + PNG (fixtures)
tests/GpibMcp.Tests/               xUnit tests (protocol, tools, db, capture, SRQ, helpers)
tests/Hpgl.Rendering.Tests/        xUnit tests (renderer geometry, fonts, golden regression)
```

## Troubleshooting

| Symptom | Likely cause / fix |
|---------|--------------------|
| Build error: *metadata file `Ivi.Visa.dll` could not be found* | A `HintPath` in `GpibMcp.csproj` does not match your machine — update it (see [Install step 2](#2-point-the-project-at-your-ni-assemblies)). |
| `visa_list_resources` returns nothing | No instruments powered/connected, or NI-VISA not installed. Confirm devices appear in **NI MAX** (Measurement & Automation Explorer). |
| `visa_list_resources` lists *every* GPIB address | An HPIB bus extender (e.g. HP 37204A) is ACKing all addresses; the server flags this. See [Discovery and bus extenders](#discovery-and-bus-extenders-hp-37204a). |
| `BadImageFormatException` at runtime | A bitness mismatch — ensure the build is `x86` and matches your installed NI runtime (see [Why x86?](#why-x86)). |
| A query times out | Instrument needs a different terminator or longer timeout; raise `timeout_ms`, and check the instrument's programming manual for the expected line ending. |
| Tools never appear in Claude Desktop | Check the `command` path in `claude_desktop_config.json` is absolute and backslash-escaped, then fully restart Claude Desktop. |

## Extending

Ideas for follow-on work (see the [issue tracker](https://github.com/TGoodhew/GPIB-MCP/issues)):

- Binary block reads (`visa_read_bytes`) for waveform transfers.
- A GPIB transport abstraction to enable non-NI backends (Prologix / AR488) (#22).
- A cross-platform raster backend for `Hpgl.Rendering` (drop the `System.Drawing`/net472
  coupling) (#9), and SCPI-native screen dump where available (#10).
- Instrument-specific helper tools (e.g. measurement presets).
- A configurable default line terminator.

## License

Released under the [MIT License](LICENSE).
