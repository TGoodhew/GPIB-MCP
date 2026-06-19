# GPIB-MCP

An [MCP](https://modelcontextprotocol.io) server that connects Claude (or any MCP
client) directly to your test-and-measurement instruments over **GPIB, USB-TMC,
LXI/TCPIP, and serial**, using the **NI-VISA** and **NI-488.2** .NET libraries.

It speaks JSON-RPC 2.0 over a stdio transport and exposes a set of tools the model
can call to discover instruments and exchange SCPI/IEEE-488.2 commands with them.

- **Language / runtime:** C#, .NET Framework 4.7.2
- **Platform:** **x86** (NI VISA.NET is installed 32-bit only on this machine)
- **Primary path:** NI-VISA (`Ivi.Visa` + `NationalInstruments.Visa`) — works across every bus
- **Native path:** NI-488.2 (`NationalInstruments.NI4882`) — addresses GPIB board/primary/secondary directly

## Requirements

- Windows with **NI-VISA** and **NI-488.2** runtimes installed (the driver assemblies
  are referenced from their installed locations — see `src/GpibMcp/GpibMcp.csproj`).
- .NET Framework 4.7.2 developer pack (or build with the bundled
  `Microsoft.NETFramework.ReferenceAssemblies` package via the .NET SDK).

## Build

With Visual Studio MSBuild:

```powershell
& "C:\Program Files\Microsoft Visual Studio\18\Enterprise\MSBuild\Current\Bin\MSBuild.exe" `
    GPIB-MCP.sln /p:Configuration=Release /p:Platform=x86
```

Or with the .NET SDK:

```powershell
dotnet build GPIB-MCP.sln -c Release
```

Output: `src\GpibMcp\bin\x86\Release\net472\GpibMcp.exe`

## Tools

| Tool | Purpose |
|------|---------|
| `visa_list_resources` | Discover connected VISA resources (GPIB/USB/TCPIP/serial) |
| `visa_query` | Write a command and read the response (e.g. `*IDN?`) |
| `visa_write` | Write a command with no response (e.g. `*RST`, `OUTP ON`) |
| `visa_read` | Read a pending response |
| `visa_identify` | Convenience `*IDN?` query |
| `visa_clear` | IEEE 488.2 device clear |
| `visa_list_open` | List sessions this server holds open |
| `visa_close` | Close a held-open session |
| `gpib488_query` | Native NI-488.2 query by board / primary / secondary address |

Sessions opened via the VISA tools are cached and reused, so an instrument stays
addressed and configured across multiple tool calls until `visa_close` is invoked.

## Connect to Claude Desktop

Add the server to `claude_desktop_config.json`
(`%APPDATA%\Claude\claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "gpib": {
      "command": "C:\\Users\\Tony\\source\\GPIBMCP\\src\\GpibMcp\\bin\\x86\\Release\\net472\\GpibMcp.exe"
    }
  }
}
```

Restart Claude Desktop. You should then be able to ask things like
*"List my instruments and identify the one at GPIB0::9."*

## Quick manual test

Pipe JSON-RPC frames (one per line) into the executable:

```powershell
$exe = "src\GpibMcp\bin\x86\Release\net472\GpibMcp.exe"
@(
  '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{}}}'
  '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"visa_list_resources","arguments":{}}}'
) -join "`n" | & $exe
```

## Project layout

```
GPIB-MCP.sln
src/GpibMcp/
  Program.cs                       entry point + stdio/UTF-8 setup
  Mcp/
    McpServer.cs                   JSON-RPC 2.0 dispatch (initialize/tools/ping)
    McpTool.cs                     tool + registry + error types
  Instruments/
    VisaInstrumentManager.cs       NI-VISA session manager (primary path)
    Gpib488Helper.cs               NI-488.2 native GPIB helper
  Tools/
    InstrumentTools.cs             tool definitions + JSON Schemas
```

## Notes / next steps

- stdout carries protocol traffic only; diagnostics go to stderr.
- The server is single-threaded and synchronous — instrument I/O is serialized,
  which is the safe default for shared GPIB buses.
- Ideas to extend: binary block reads (`visa_read_bytes`), service-request/status-byte
  polling, instrument-specific helper tools, and a configurable default terminator.
