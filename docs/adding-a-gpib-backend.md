# Adding a GPIB backend

The server's wire-level I/O sits behind a single abstraction, **`IGpibTransport`**
(`src/GpibMcp.Core/Instruments/IGpibTransport.cs`). The default backend is **NI-VISA**
(`GpibMcp.NiVisa`); you can add another adapter — a [Prologix](https://prologix.biz/)
GPIB-USB/-Ethernet, an [AR488](https://github.com/Twilight-Logic/AR488) (Arduino), anything —
**without touching the tools, the instrument database, or the MCP plumbing**.

## Architecture

```
MCP tools  ->  InstrumentManager (backend-neutral)  ->  IGpibTransport
   (GpibMcp.Core)        (GpibMcp.Core)                  |- NiVisaTransport   (GpibMcp.NiVisa, default)
                                                         |- YourTransport     (GpibMcp.<Yours>)
```

- **`GpibMcp.Core`** — the MCP server, tools, instrument DB, and the abstraction. **No driver
  dependency.** Builds and runs without NI-VISA installed.
- **`GpibMcp.NiVisa`** — the only project that references the NI assemblies. It is **not** referenced
  by the server exe at compile time; the exe loads the chosen backend by assembly name at runtime
  (`TransportFactory`). That is what keeps NI optional.
- **`GpibMcp`** (exe) — entry point + stdio transport only.

## Steps

1. **Create a project** `src/GpibMcp.<Name>/GpibMcp.<Name>.csproj` (net472, x86), referencing
   `GpibMcp.Core` and whatever your adapter needs (a serial-port / TCP library, etc.). Copy the
   `CopyBackendToServer` `AfterBuild` target from `GpibMcp.NiVisa.csproj` so your DLL is deployed
   next to the server exe.
2. **Implement `IGpibTransport`** (`GpibMcp.Instruments.YourTransport`). Cover what you can:
   open/close/discover, `Write`, `Read` (honour `TransportReadRequest`: timeout, `TermChar`,
   and `MaxBytes` — a bounded read returns partial data with `TimedOut` set instead of throwing),
   `SerialPoll`, `WaitForSrq`, `Clear`, `ReturnToLocal`, and `DescribeError`.
3. **Advertise honest `TransportCapabilities`.** If your adapter can't do SRQ or discovery, say so —
   the tools degrade or refuse cleanly rather than guessing. Implement the optional `INativeGpib` only
   if you support board/primary/secondary addressing.
4. **Register it** in `TransportFactory.Create()` (`GpibMcp.Core`): add a `case "<name>":` that calls
   `Load("GpibMcp.<Name>", "GpibMcp.Instruments.YourTransport")`.
5. **Add the project to `GPIB-MCP.sln`** (`dotnet sln add ...`) so a full solution build produces and
   deploys your DLL. Build the **solution**, not just the exe.
6. Select it at runtime with `GPIB_MCP_BACKEND=<name>` (NI-VISA stays the default).

## Addressing model

The canonical identifier is a VISA-style resource string (e.g. `GPIB0::18::INSTR`). Prologix/AR488
talk a serial (or TCP) `++` command set and address by **primary GPIB address**:

```
++mode 1        # controller mode
++addr 18       # talk/listen to GPIB address 18
++auto 1        # or ++read after a query
```

Your transport parses the GPIB primary address out of the resource string and gets the COM port / IP
from configuration (e.g. a `GPIB_MCP_PROLOGIX_PORT=COM5` env var you read in your transport's ctor).
Map the canonical resource onto your adapter's target; keep the resource string as the key the manager
caches on.

## Notes

- The `InstrumentManager` serializes all I/O under one lock, so your transport does **not** need to be
  internally thread-safe.
- Strings are encoded/decoded as Latin-1 (1:1 byte mapping) by the manager; your `Write`/`Read` move
  raw bytes.
- A non-NI contributor can exclude `GpibMcp.NiVisa` (and the VISA-decoding tests in `GpibMcp.Tests`)
  from the build; `GpibMcp.Core` + the exe + your backend build without NI installed.
