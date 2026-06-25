# MCP-client deployment packages

Generates the install packages for the local-stdio MCP clients that consume the GPIB MCP server:
**VS Code** (#89), **Cursor** (#90), **Windsurf** (#91). Claude Desktop is packaged separately as a
`.dxt` (#67); remote-HTTP clients (Copilot #88, ChatGPT #92) need the Streamable HTTP transport (#68)
and are not stdio packages.

All three clients run the **same** `GpibMcp.exe` over stdio, so there is **one** binary artifact (a
versioned GitHub Release zip) and a small per-client config + one-click link generated from it.

## Two scripts

- **`Generate-Packages.ps1`** — *maintainer* side. Builds the server, makes the versioned zip (the Release
  asset), emits the local per-client snippets/links, and (with `-PublishRelease`) publishes the GitHub
  Release with the zip **and** the installer attached.
- **`Install-GpibMcp.ps1`** — *end-user* side. Downloads the latest release (or a local `-FromZip`), unzips
  to `%LOCALAPPDATA%\Programs\GpibMcp`, and registers it with `-Client vscode|cursor|windsurf|all` by
  writing each client's config with the **resolved absolute path** (backing up any existing config first).

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

## Notes
- **NI-VISA build dependency:** the server is built **locally** (the `GpibMcp.NiVisa` backend needs the NI
  SDK to compile), so packaging runs on a dev machine with the NI tooling — not on a stock CI runner. A
  tag-driven GitHub Actions release would need a self-hosted/NI-equipped runner or a build split first
  (tracked separately if/when wanted).
- The generated configs point at `<InstallDir>\GpibMcp.exe`, so the one-click links work once the release
  is unzipped there. NI-VISA / NI-488.2 must be installed on the target machine at runtime.
- `Generate-Packages.ps1` never publishes unless you pass `-PublishRelease`.
