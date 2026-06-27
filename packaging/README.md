# MCP-client deployment packages

Generates the install packages for every MCP client that consumes the GPIB MCP server. They all run the
**same** `GpibMcp.exe`, so there is **one** binary artifact (a versioned GitHub Release zip); the per-client
package differs only in how the client reaches the server:

- **Local stdio clients** — launch the server as a child process: **VS Code** (#89), **Cursor** (#90),
  **Windsurf** (#91), and Claude Desktop (`.dxt`, #67). Package = a small config snippet + one-click link.
- **Cloud / HTTP clients** — connect to a tunnelled URL over **Streamable HTTP** (#68): **Microsoft Copilot**
  (#88), **ChatGPT** (#92). Package = the HTTP launcher + connector docs (and, for Copilot, an OpenAPI
  custom-connector template).

## Scripts

- **`Generate-Packages.ps1`** — *maintainer*. Builds the server, makes the versioned zip (the Release asset),
  emits every client package, and (with `-PublishRelease`) publishes the GitHub Release with the zip, the
  installer, **and** the HTTP launcher attached.
- **`Install-GpibMcp.ps1`** — *end-user, stdio*. Downloads the latest release (or a local `-FromZip`), unzips
  to `%LOCALAPPDATA%\Programs\GpibMcp`, and registers it with `-Client vscode|cursor|windsurf|all` by writing
  each client's config with the **resolved absolute path** (backing up any existing config first).
- **`Start-GpibMcpHttp.ps1`** — *end-user, cloud*. Runs the installed server in HTTP mode
  (`GPIB_MCP_TRANSPORT=http`) with a generated bearer token, and prints the local URL + the tunnel +
  connector-registration steps for Copilot / ChatGPT.
- **`Build-Mcpb.ps1`** — builds the **Claude Desktop Extension** bundle (`.mcpb`, #67): writes a manifest
  (spec v0.3, binary server) over the staged build and packs it (prefers the official `@anthropic-ai/mcpb`
  CLI, which validates the manifest; falls back to a plain zip). `Generate-Packages.ps1` calls it and
  attaches the `.mcpb` to the release. End users just open the `.mcpb` in Claude Desktop → Extensions.

  Why an installer rather than a copy-paste config: VS Code and Cursor do **not** expand `${env:…}` in an
  MCP `command`, so a portable hand-written path isn't possible across clients — having the installer write
  the real per-user path is the only reliable cross-client approach. (VS Code also accepts the predefined
  `${userHome}`; the committed README documents the manual fallback.)

## Generate everything

```powershell
./packaging/Generate-Packages.ps1            # build + stage + emit (no publish)
./packaging/Generate-Packages.ps1 -SkipBuild # reuse the current build, just re-emit
./packaging/Generate-Packages.ps1 -Version 0.2.0 -PublishRelease   # also publish the GitHub Release
```

Output (git-ignored):

```
dist/
  GpibMcp-<version>-win-x86/        staged server build
  GpibMcp-<version>-win-x86.zip     the GitHub Release asset
  packaging/
    vscode/    mcp.json   + README.md
    cursor/    mcp.json   + README.md
    windsurf/  mcp_config.json + README.md
```

## Parameters

| param | default | purpose |
|---|---|---|
| `-Version` | `0.1.0` | release/version label and zip/tag name |
| `-InstallDir` | `%LOCALAPPDATA%\Programs\GpibMcp` | where the release unzips to — the path baked into the generated configs/deeplinks |
| `-Configuration` | `Release` | build configuration |
| `-SkipBuild` | off | reuse the existing build output (skip `dotnet build`) |
| `-PublishRelease` | off | publish a GitHub Release (`v<Version>`) with the zip via `gh` |
| `-OutputDir` | `dist` | output root |

## Continuous integration (GitHub Actions)

Two workflows under [`.github/workflows/`](../.github/workflows/) drive build/test and releases. Both run on
a **self-hosted Windows runner** because the `GpibMcp.NiVisa` backend links the NI-VISA / NI-488.2 assemblies,
which only exist on a machine with the NI drivers installed — a stock GitHub-hosted runner can't compile it.

| Workflow | Trigger | What it does |
|---|---|---|
| **`ci.yml`** | push to `main`, PRs into `main` | `dotnet build` + `dotnet test` (the hardware-free suite) — catches regressions before a release. |
| **`release.yml`** | push a `v*` tag (or manual **Run workflow** with a version) | builds + tests, then runs `Generate-Packages.ps1 -SkipBuild -PublishRelease` to build and **post the complete asset set** to the GitHub Release: the **zip**, the **`.mcpb`**, **`Install-GpibMcp.ps1`**, and **`Start-GpibMcpHttp.ps1`**. |

**Cut a release:** tag the commit and push — e.g. `git tag v0.3.0 && git push origin v0.3.0` — or use
**Actions → Release → Run workflow** and enter the version. Publishing is **idempotent**: re-running an
existing tag refreshes that release's notes and re-uploads the assets (`gh release ... --clobber`) instead of
failing. The release step authenticates `gh` with the workflow's `GITHUB_TOKEN` (`permissions: contents: write`).

**One-time runner setup:** register a self-hosted runner (repo **Settings → Actions → Runners → New runner**)
on the bench PC, with the default `self-hosted` + `Windows` labels, and ensure **NI-VISA/NI-488.2**, the
**.NET SDK**, and the **`gh` CLI** are installed (they already are on the machine that builds locally today).

## Notes
- **NI-VISA build dependency:** the server is built on a machine with the NI tooling (locally, or the
  self-hosted CI runner above) because the `GpibMcp.NiVisa` backend needs the NI assemblies to compile.
- The generated configs point at `<InstallDir>\GpibMcp.exe`, so the one-click links work once the release
  is unzipped there. NI-VISA / NI-488.2 must be installed on the target machine at runtime.
- `Generate-Packages.ps1` never publishes unless you pass `-PublishRelease`.
