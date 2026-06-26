<#
.SYNOPSIS
    Generate all MCP-client deployment packages for the GPIB MCP server from a single Release x86 build -
    stdio clients (#89 VS Code, #90 Cursor, #91 Windsurf) and HTTP clients (#88 Copilot, #92 ChatGPT).

.DESCRIPTION
    All clients run the SAME server (GpibMcp.exe). The stdio clients launch it locally and differ only by a
    config snippet / one-click link; the HTTP clients connect to a tunnelled URL and need connector docs + the
    HTTP launcher. This builds the server once, stages it as a versioned zip (the GitHub Release asset), and
    emits per-client install assets:

        dist/
          GpibMcp-<version>-win-x86/         staged build (the server + data)
          GpibMcp-<version>-win-x86.zip      release asset (the binary distribution)
          packaging/
            vscode/    mcp.json   + README.md   (code --add-mcp, vscode:mcp/install link)
            cursor/    mcp.json   + README.md   (cursor:// "Add to Cursor" deeplink)
            windsurf/  mcp_config.json + README.md
            copilot/   gpib-mcp-connector.swagger.json + README.md + Start-GpibMcpHttp.ps1
            chatgpt/   README.md + Start-GpibMcpHttp.ps1

    The stdio configs/deeplinks point at <InstallDir>\GpibMcp.exe - the canonical place a user (or you) unzips
    the release to - so the one-click links work once the zip is extracted there.

    Nothing is published unless you pass -PublishRelease (which calls `gh release create`).

.EXAMPLE
    ./packaging/Generate-Packages.ps1
        Build + stage + emit all client packages under dist/ (no publish).

.EXAMPLE
    ./packaging/Generate-Packages.ps1 -SkipBuild
        Reuse the existing Release build output (fast re-emit of the packages).

.EXAMPLE
    ./packaging/Generate-Packages.ps1 -Version 0.2.0 -PublishRelease
        Build, emit, and publish a GitHub Release (tag v0.2.0) with the zip attached.
#>
[CmdletBinding()]
param(
    [string]$Version = "0.2.0",
    # Canonical install location the generated configs/deeplinks reference (where the release is unzipped).
    [string]$InstallDir = "$env:LOCALAPPDATA\Programs\GpibMcp",
    [string]$Configuration = "Release",
    [switch]$SkipBuild,
    [switch]$PublishRelease,
    [string]$OutputDir
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
if (-not $OutputDir) { $OutputDir = Join-Path $repo "dist" }
$buildOut = Join-Path $repo "src\GpibMcp\bin\x86\$Configuration\net472"
$stageName = "GpibMcp-$Version-win-x86"
$stageDir  = Join-Path $OutputDir $stageName
$zipPath   = Join-Path $OutputDir "$stageName.zip"
$pkgRoot   = Join-Path $OutputDir "packaging"
$exePath   = Join-Path $InstallDir "GpibMcp.exe"
$tmplDir   = Join-Path $PSScriptRoot "templates"

function Write-Step($m) { Write-Host "==> $m" -ForegroundColor Cyan }

# ---- 1. Build (unless reusing an existing build) --------------------------------------------------
if (-not $SkipBuild) {
    Write-Step "Building $Configuration x86"
    & dotnet build (Join-Path $repo "GPIB-MCP.sln") -c $Configuration -p:Platform=x86 -clp:ErrorsOnly
    if ($LASTEXITCODE -ne 0) { throw "build failed (exit $LASTEXITCODE)" }
}
if (-not (Test-Path (Join-Path $buildOut "GpibMcp.exe"))) {
    throw "No build output at $buildOut. Run without -SkipBuild (and ensure Claude Desktop isn't locking the exe)."
}

# ---- 2. Stage the server + zip it (the release asset) ---------------------------------------------
Write-Step "Staging server -> $stageDir"
if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stageDir | Out-Null
Copy-Item "$buildOut\*" $stageDir -Recurse -Force

Write-Step "Zipping -> $zipPath"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path "$stageDir\*" -DestinationPath $zipPath

# Claude Desktop Extension bundle (.mcpb) for one-click install (#67), built from the same staged server.
$mcpbPath = Join-Path $OutputDir "GpibMcp-$Version.mcpb"
& (Join-Path $PSScriptRoot "Build-Mcpb.ps1") -SourceDir $stageDir -Version $Version -OutFile $mcpbPath

# ---- 3. Per-client config JSON (built with ConvertTo-Json so paths are escaped correctly) ----------
# The stdio server object every client wraps.
$serverObj = [ordered]@{ command = $exePath; args = @() }

$vscodeConfig = [ordered]@{ servers   = [ordered]@{ gpib = ([ordered]@{ type = "stdio" } + $serverObj) } } | ConvertTo-Json -Depth 6
$cursorConfig = [ordered]@{ mcpServers = [ordered]@{ gpib = $serverObj } } | ConvertTo-Json -Depth 6
$windsurfConfig = $cursorConfig   # Windsurf uses the same mcpServers shape

# ---- 4. One-click install artifacts ---------------------------------------------------------------
# VS Code: `code --add-mcp '<json>'` and the vscode:mcp/install URL.
$vscodeServerJson = ([ordered]@{ name = "gpib"; command = $exePath; args = @() } | ConvertTo-Json -Compress)
$vscodeAddCmd     = "code --add-mcp `"$($vscodeServerJson.Replace('"','\"'))`""
$vscodeUrl        = "vscode:mcp/install?" + [uri]::EscapeDataString($vscodeServerJson)

# Cursor: cursor://anysphere.cursor-deeplink/mcp/install?name=&config=<base64 server-json>
$cursorServerJson = ([ordered]@{ command = $exePath; args = @() } | ConvertTo-Json -Compress)
$cursorB64        = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($cursorServerJson))
$cursorDeeplink   = "cursor://anysphere.cursor-deeplink/mcp/install?name=gpib&config=$cursorB64"

# ---- 5. Emit per-client package dirs from templates -----------------------------------------------
function New-ClientPackage($client, $configFileName, $configText, $tokens) {
    $dir = Join-Path $pkgRoot $client
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    Set-Content -Path (Join-Path $dir $configFileName) -Value $configText -Encoding UTF8

    $tmpl = Get-Content (Join-Path $tmplDir "$client.README.md.tmpl") -Raw
    foreach ($k in $tokens.Keys) { $tmpl = $tmpl.Replace($k, [string]$tokens[$k]) }
    Set-Content -Path (Join-Path $dir "README.md") -Value $tmpl -Encoding UTF8
    Write-Host "    $client -> $dir"
}

Write-Step "Emitting client packages -> $pkgRoot"
$common = @{ "__VERSION__" = $Version; "__EXE__" = $exePath; "__ZIP__" = "$stageName.zip" }

New-ClientPackage "vscode" "mcp.json" $vscodeConfig ($common + @{
    "__VSCODE_ADDCMD__" = $vscodeAddCmd; "__VSCODE_URL__" = $vscodeUrl; "__CONFIG__" = $vscodeConfig })
New-ClientPackage "cursor" "mcp.json" $cursorConfig ($common + @{
    "__CURSOR_DEEPLINK__" = $cursorDeeplink; "__CONFIG__" = $cursorConfig })
New-ClientPackage "windsurf" "mcp_config.json" $windsurfConfig ($common + @{ "__CONFIG__" = $windsurfConfig })

# HTTP clients (#88 Copilot, #92 ChatGPT): remote - they connect to a URL over a tunnel, so the package is
# connector docs + the HTTP launcher, not a local config path.
function Write-Template($tmplName, $destPath, $tokens) {
    $t = Get-Content (Join-Path $tmplDir $tmplName) -Raw
    foreach ($k in $tokens.Keys) { $t = $t.Replace($k, [string]$tokens[$k]) }
    Set-Content -Path $destPath -Value $t -Encoding UTF8
}
$httpTokens = $common + @{ "__PORT__" = "3001" }
$httpLauncher = Join-Path $PSScriptRoot "Start-GpibMcpHttp.ps1"

$copilotDir = Join-Path $pkgRoot "copilot"
if (Test-Path $copilotDir) { Remove-Item $copilotDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $copilotDir | Out-Null
Write-Template "copilot.connector.swagger.json.tmpl" (Join-Path $copilotDir "gpib-mcp-connector.swagger.json") @{ "__VERSION__" = $Version }
Write-Template "copilot.README.md.tmpl" (Join-Path $copilotDir "README.md") $httpTokens
if (Test-Path $httpLauncher) { Copy-Item $httpLauncher $copilotDir -Force }
Write-Host "    copilot -> $copilotDir"

$chatgptDir = Join-Path $pkgRoot "chatgpt"
if (Test-Path $chatgptDir) { Remove-Item $chatgptDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $chatgptDir | Out-Null
Write-Template "chatgpt.README.md.tmpl" (Join-Path $chatgptDir "README.md") $httpTokens
if (Test-Path $httpLauncher) { Copy-Item $httpLauncher $chatgptDir -Force }
Write-Host "    chatgpt -> $chatgptDir"

# ---- 6. Optional: publish a GitHub Release with the zip + installer -------------------------------
if ($PublishRelease) {
    Write-Step "Publishing GitHub Release v$Version"
    $installer = Join-Path $PSScriptRoot "Install-GpibMcp.ps1"
    $launcher = Join-Path $PSScriptRoot "Start-GpibMcpHttp.ps1"
    $notes = @"
GPIB MCP server $Version (Windows x86). **Requires NI-VISA / NI-488.2** installed.

**Claude Desktop (one-click):** download ``GpibMcp-$Version.mcpb`` below and open it in Claude Desktop
(Settings -> Extensions), or drag it onto the window. Requires NI-VISA / NI-488.2 (#67).

**Other local clients (stdio):**
``````powershell
iwr https://raw.githubusercontent.com/TGoodhew/GPIB-MCP/main/packaging/Install-GpibMcp.ps1 -OutFile Install-GpibMcp.ps1
./Install-GpibMcp.ps1 -Client all   # vscode | cursor | windsurf | all
``````
VS Code #89, Cursor #90, Windsurf #91 (or Claude Desktop manually).

**Cloud clients (Streamable HTTP, this release adds the HTTP transport #68):** run the server in HTTP mode and
tunnel it, then register the connector. Microsoft Copilot #88 / ChatGPT #92:
``````powershell
./Start-GpibMcpHttp.ps1             # http://127.0.0.1:3001/mcp + a bearer token; prints next steps
``````
See ``packaging/copilot`` and ``packaging/chatgpt`` for the connector docs.

Or download ``$stageName.zip`` below and unzip to ``%LOCALAPPDATA%\Programs\GpibMcp``.
"@
    $assets = @($zipPath)
    if (Test-Path $mcpbPath)  { $assets += $mcpbPath }
    if (Test-Path $installer) { $assets += $installer }
    if (Test-Path $launcher)  { $assets += $launcher }
    & gh release create "v$Version" $assets --title "GPIB MCP $Version" --notes $notes
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed (exit $LASTEXITCODE)" }
}

Write-Host ""
Write-Step "Done. Packages under $pkgRoot ; release asset: $zipPath"
if (-not $PublishRelease) { Write-Host "    (re-run with -PublishRelease to publish the GitHub Release)" -ForegroundColor DarkGray }
