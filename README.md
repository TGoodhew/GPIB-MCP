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

A clean build should report **0 warnings, 0 errors**. After building, confirm the
server actually starts and speaks the protocol by driving it directly — no instruments
or MCP client required.

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

Edit Claude Desktop's config file (create it if it does not exist):

- **Windows:** `%APPDATA%\Claude\claude_desktop_config.json`

Add a `gpib` server entry pointing at the built executable. Use the **absolute path**
to `GpibMcp.exe` on your machine, with **escaped backslashes**:

```json
{
  "mcpServers": {
    "gpib": {
      "command": "C:\\path\\to\\GPIB-MCP\\src\\GpibMcp\\bin\\x86\\Release\\net472\\GpibMcp.exe"
    }
  }
}
```

Restart Claude Desktop. The `gpib` tools then appear and you can ask things like
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
| `gpib488_query` | `primary_address`, `command` | `board`, `secondary_address` | Native NI-488.2 query by board / primary / secondary |

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
    VisaInstrumentManager.cs       NI-VISA session manager (primary path)
    Gpib488Helper.cs               NI-488.2 native GPIB helper
    CommandText.cs                 shared command-termination + log helpers
  Tools/
    InstrumentTools.cs             tool definitions + JSON Schemas
```

## Troubleshooting

| Symptom | Likely cause / fix |
|---------|--------------------|
| Build error: *metadata file `Ivi.Visa.dll` could not be found* | A `HintPath` in `GpibMcp.csproj` does not match your machine — update it (see [Install step 2](#2-point-the-project-at-your-ni-assemblies)). |
| `visa_list_resources` returns nothing | No instruments powered/connected, or NI-VISA not installed. Confirm devices appear in **NI MAX** (Measurement & Automation Explorer). |
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
