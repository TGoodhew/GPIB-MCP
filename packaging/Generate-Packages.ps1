<#
.SYNOPSIS
    Generate all MCP-client deployment packages for the GPIB MCP server (#89 VS Code, #90 Cursor,
    #91 Windsurf) from a single Release x86 build.

.DESCRIPTION
    The three target clients all run the SAME local stdio server (GpibMcp.exe); only a small config
    snippet and a one-click install link differ per client. So this builds the server once, stages it as
    a versioned zip (the GitHub Release asset), and emits per-client install assets:

        dist/
          GpibMcp-<version>-win-x86/         staged build (the server + data)
          GpibMcp-<version>-win-x86.zip      release asset (the binary distribution)
          packaging/
            vscode/    mcp.json   + README.md   (code --add-mcp, vscode:mcp/install link)
            cursor/    mcp.json   + README.md   (cursor:// "Add to Cursor" deeplink)
            windsurf/  mcp_config.json + README.md

    The generated configs/deeplinks point at <InstallDir>\GpibMcp.exe - the canonical place a user (or you)
    unzips the release to - so the one-click links work once the zip is extracted there.

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
    [string]$Version = "0.1.0",
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
$common = @{ "__VERSION__" = $Version; "__EXE__" = $exePath; "__INSTALLDIR__" = $InstallDir; "__ZIP__" = "$stageName.zip" }

New-ClientPackage "vscode" "mcp.json" $vscodeConfig ($common + @{
    "__VSCODE_ADDCMD__" = $vscodeAddCmd; "__VSCODE_URL__" = $vscodeUrl; "__CONFIG__" = $vscodeConfig })
New-ClientPackage "cursor" "mcp.json" $cursorConfig ($common + @{
    "__CURSOR_DEEPLINK__" = $cursorDeeplink; "__CONFIG__" = $cursorConfig })
New-ClientPackage "windsurf" "mcp_config.json" $windsurfConfig ($common + @{ "__CONFIG__" = $windsurfConfig })

# ---- 6. Optional: publish a GitHub Release with the zip -------------------------------------------
if ($PublishRelease) {
    Write-Step "Publishing GitHub Release v$Version"
    $notes = "GPIB MCP server $Version (Windows x86). Unzip to ``$InstallDir`` and add it to your MCP client " +
             "using the per-client config in dist/packaging/ (VS Code #89, Cursor #90, Windsurf #91)."
    & gh release create "v$Version" $zipPath --title "GPIB MCP $Version" --notes $notes
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed (exit $LASTEXITCODE)" }
}

Write-Host ""
Write-Step "Done. Packages under $pkgRoot ; release asset: $zipPath"
if (-not $PublishRelease) { Write-Host "    (re-run with -PublishRelease to publish the GitHub Release)" -ForegroundColor DarkGray }
