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
  - [Discovery and bus extenders (HP 37204A)](#discovery-and-bus-extenders-hp-37204a)
  - [Instrument command database](#instrument-command-database)
  - [Screen capture (HP-GL plotter emulation)](#screen-capture-hp-gl-plotter-emulation)
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

With the **.NET SDK**:

```bash
dotnet build GPIB-MCP.sln -c Release
```

Or with **MSBuild** (from a *Developer Command Prompt* / *Developer PowerShell*):

```powershell
msbuild GPIB-MCP.sln /p:Configuration=Release /p:Platform=x86
```

The resulting executable is:

```
src\GpibMcp\bin\x86\Release\net472\GpibMcp.exe
```

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
a `tools/list` result listing the nine tools. If you get those, the build is good.

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

### Tool reference

| Tool | Required args | Optional args | Purpose |
|------|---------------|---------------|---------|
| `visa_list_resources` | — | `filter` | Discover connected VISA resources |
| `visa_query` | `resource`, `command` | `timeout_ms` | Write a command and read the response (e.g. `*IDN?`) |
| `visa_write` | `resource`, `command` | `timeout_ms` | Write a command with no response (e.g. `*RST`, `OUTP ON`) |
| `visa_read` | `resource` | `timeout_ms` | Read a pending response |
| `visa_identify` | `resource` | — | Convenience `*IDN?` query |
| `visa_clear` | `resource` | — | IEEE 488.2 device clear |
| `visa_list_open` | — | — | List sessions this server holds open |
| `visa_close` | `resource` | — | Close a held-open session |
| `visa_command_history` | `resource` | `max` | Show the recent command chain sent to / received from an instrument |
| `visa_last_error` | — | `resource` | Return the exact, verbatim details (codes + text) of the most recent GPIB/VISA failure |
| `visa_serial_poll` | `resource` | — | Serial-poll the instrument; return the status byte (decimal + hex) and the named bits set |
| `visa_wait_srq` | `resource` | `timeout_ms` | Block until the instrument asserts SRQ, or the backstop timeout expires |
| `instrument_wait_complete` | `resource`, `operation` | `timeout_ms` | Wait for an operation to truly complete via SRQ (data-driven; no fixed-timeout guess) |
| `gpib488_query` | `primary_address`, `command` | `board`, `secondary_address` | Native NI-488.2 query by board / primary / secondary |
| `instrument_list_models` | — | — | List models in the command database ("what instruments do you know about?") |
| `instrument_reference` | `model` | `command`, `search`, `category` | Get a model's command reference / a specific command's detail |
| `instrument_identify` | `resource` | — | Query identity and match against the database |
| `assign_instrument` | `resource`, `model` | `confirm`, `verify` | Record that a model sits at a resource (persists on `confirm=true`) |
| `list_assignments` | — | — | List recorded resource→model assignments |
| `unassign_instrument` | `resource` | `confirm` | Remove an assignment (on `confirm=true`) |
| `instrument_db_save` | `definition` | `confirm` | Add/update a model definition (on `confirm=true`) |
| `instrument_capture_screen` | `resource` | `model`, `width`, `height`, `background`, `return_hpgl`, `inline_svg`, `save_dir`, `save_path`, `timeout_ms` | Capture the instrument's screen (HP-GL plotter emulation); returns an SVG to show inline + saves a PNG to Pictures |

Argument notes:

- `resource` — a VISA resource string such as `GPIB0::5::INSTR`,
  `TCPIP0::192.168.1.50::INSTR`, `USB0::0x0699::0x0408::C012345::INSTR`, or `ASRL1::INSTR`.
- `command` — sent verbatim; a newline terminator is appended if you omit one.
- `timeout_ms` — I/O timeout in milliseconds (default `5000`).
- `board` — GPIB controller index (default `0`).
- `secondary_address` — `0` means "no secondary address" (the default).

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

It comes prepopulated with **55 instrument models / ~6,400 documented commands** (HP/Agilent,
Keithley, Tektronix, Rigol, Rohde & Schwarz, Datron) — see the
[catalog](data/instruments/README.md). Each model is one JSON file describing its identity
query, command mnemonics, parameters, units, and examples (see
[`data/instruments/`](data/instruments/)).

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
bundled ones with the same model name.

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
- **Override, don't edit the bundled file.** A `<model>.json` in your **user** directory
  overrides the bundled one of the same name. Fix a wrong command, add a missing one, or
  correct an identity pattern there (or via `instrument_db_save`) and your version wins —
  bundled defaults stay intact and your correction survives upgrades.
- **Confirm-before-write everywhere.** `assign_instrument`, `unassign_instrument`, and
  `instrument_db_save` all report what they *would* do first and only persist when called
  again with `confirm: true`, so nothing changes on disk without your go-ahead.

> Partial / missing by design: a few instruments whose programming guide isn't in the manual
> library (e.g. N9320A, DSA800, full E4436B/E4406A) are intentionally absent or partial rather
> than guessed. Add the relevant programming guide and ask Claude to extract it, or author the
> JSON yourself.

### Screen capture (HP-GL plotter emulation)

For instruments that can plot to an HP 7470A-style plotter (e.g. the **HP 8563E** spectrum
analyzer), `instrument_capture_screen` grabs the **actual screen** — graticule, trace, markers,
and annotation. Ask Claude:

> *"Capture the screen of the analyzer at GPIB0::18 and show it to me."*

It works by **plotter emulation**: the server sends the model's plot command (from its capture
profile), plays the role of the plotter — answering the instrument's `OS` status handshake — and
collects the HP-GL, which [`Hpgl.Rendering`](src/Hpgl.Rendering/) renders to **both** a PNG and a
compact **SVG**.

- The model is taken from the resource's [assignment](#instrument-command-database), or pass `model=`.
- Only models with a `capture` profile in the database are supported (the 8563E ships with one;
  add others as data — `{ "method": "hpgl", "plotCommand": "...", "preRoll": "..." }`).
- `return_hpgl=true` also returns the raw HP-GL/2 source; `background`, `width`, `height` tune the image.
- Every capture is also **saved to a PNG file** — by default in your **Pictures** folder
  (`…\Pictures\GpibMcp Captures`). Say *"…and store it in `C:\path\to\folder`"* to choose where
  (`save_dir`), or pass `save_path` for a full filename. The saved path is reported in the result.

#### Showing it inline in the chat

Claude Desktop does **not** render MCP tool-result *image* blocks inline in the conversation — a
[known client limitation](https://github.com/anthropics/claude-ai-mcp/issues/238) (the image is
visible to the model and, at best, buried inside the expandable tool-call block). It **does**,
however, render **SVG artifacts** inline. So the tool returns the capture as an SVG and asks Claude
to display it as an `image/svg+xml` artifact — which appears inline with the rest of the chat. The
spec-correct PNG image block is still included (for the model's vision and for clients that do render
it), and the PNG is always saved to disk regardless.

- `inline_svg=true` (default) returns the SVG + the instruction to render it as an artifact.
- `inline_svg=false` falls back to the image-block + saved-file behaviour only.
- The vector SVG is small (a real 8563E capture is ~9 KB), so it reproduces reliably as an artifact.
  You can preview the exact look by opening [`Test/test.svg`](Test/test.svg) in a browser.

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
- `statusModel`/operation missing → it **asks** you for the definitions (save them with
  `instrument_db_save`), rather than guessing;
- complete → it runs the SRQ flow above.

The 8563E ships with `sweepComplete` and `sweepAndPeak` operations; the lower-level
`visa_serial_poll` and `visa_wait_srq` tools are available standalone for debugging.

The `statusModel` is **self-describing** so the waiter never guesses: it names the completion bit
per operation (`expectBit`), the failure bit (`errorBit` — e.g. `error` on the 8560, `fail` on the
3325), the enable-mask commands, and an optional `restore`.

**SRQ-edge flow (hardware-confirmed on the 8563E).** When a model names a `requestServiceBit` (the
GPIB request-service bit, `0x40`), the waiter uses a more robust flow: it disarms and drains stale
status, arms `expectBit|errorBit` (**never** the request-service bit — arming that self-fires),
**waits for the operation to go busy** (the expect bit clears) so a condition that is already true at
arm-time can't be read as "done", then treats the next request-service assertion as completion and
classifies by the error bit. This was found the hard way on a real 8563E: the 8560 RQS mask and the
read-back status byte share one layout (Programming Guide Table 7-266) where `0x40` is request-service
— *not* an error — so the old "poll the expect bit" model misread every successful sweep as an error
and could pre-fire on a standing end-of-sweep. Models without a `requestServiceBit` use the legacy
direct-bit flow. (Making this fully device-agnostic across all SRQ instruments is tracked in #26.)

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
src/GpibMcp/
  GpibMcp.csproj                   net472 / x86 project + NI references
  Program.cs                       entry point + stdio/UTF-8 setup
  Diagnostics/
    Log.cs                         leveled stderr logger (GPIB_MCP_LOG_LEVEL)
  Mcp/
    McpServer.cs                   JSON-RPC 2.0 dispatch (initialize / tools / ping)
    McpTool.cs                     tool + registry + error types
  Instruments/
    IInstrumentManager.cs          instrument-layer abstraction (enables testing)
    VisaInstrumentManager.cs       NI-VISA session manager (primary path)
    Gpib488Helper.cs               NI-488.2 native GPIB helper
    CommandText.cs                 shared command-termination + log helpers
    InstrumentDefinition.cs        instrument command-reference data model
    InstrumentDatabase.cs          loads/indexes model definitions
    AssignmentStore.cs             persistent resource->model assignments
    InstrumentPaths.cs             DB/bindings paths + install-time prepopulation
  Tools/
    ToolArgs.cs                    shared JSON-Schema + argument helpers
    InstrumentTools.cs             VISA / NI-488.2 tool definitions
    DatabaseTools.cs               command-database + assignment tools
data/instruments/*.json            bundled instrument command database
tests/GpibMcp.Tests/               xUnit tests (protocol, tools, db, helpers, logging)
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

Ideas for follow-on work:

- Binary block reads (`visa_read_bytes`) for waveform/screenshot transfers.
- Service-request (SRQ) / status-byte polling.
- Instrument-specific helper tools (e.g. measurement presets).
- A configurable default line terminator.

## License

Released under the [MIT License](LICENSE).
