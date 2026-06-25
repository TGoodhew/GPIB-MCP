<#
.SYNOPSIS
    Install the GPIB MCP server and register it with your AI client (VS Code #89 / Cursor #90 / Windsurf #91).

.DESCRIPTION
    End-user installer. Downloads the release zip from GitHub (or uses a local -FromZip), unzips it to a
    canonical location, then writes the correct config - with the REAL local path - into the client(s) you
    pick. Because VS Code/Cursor don't expand environment variables in an MCP `command`, having the installer
    write the resolved path is the only reliable cross-client way to make it "just work".

    Prerequisite: NI-VISA / NI-488.2 must be installed (the GPIB driver; not bundled).

.EXAMPLE
    irm https://raw.githubusercontent.com/TGoodhew/GPIB-MCP/main/packaging/Install-GpibMcp.ps1 | iex
        Download + install the latest release (no client registered; prints next steps).

.EXAMPLE
    ./Install-GpibMcp.ps1 -Client vscode
        Install the latest release and register it with VS Code.

.EXAMPLE
    ./Install-GpibMcp.ps1 -FromZip .\dist\GpibMcp-0.1.0-win-x86.zip -Client all
        Install from a local zip and register with VS Code, Cursor, and Windsurf.
#>
[CmdletBinding()]
param(
    [string]$Version = "latest",
    [string]$InstallDir = "$env:LOCALAPPDATA\Programs\GpibMcp",
    [ValidateSet("vscode","cursor","windsurf","all","none")]
    [string]$Client = "none",
    [string]$FromZip,
    [string]$Repo = "TGoodhew/GPIB-MCP",
    # Config-file overrides (defaults are the real per-user locations; handy for testing).
    [string]$VSCodeConfigPath   = "$env:APPDATA\Code\User\mcp.json",
    [string]$CursorConfigPath   = "$env:USERPROFILE\.cursor\mcp.json",
    [string]$WindsurfConfigPath = "$env:USERPROFILE\.codeium\windsurf\mcp_config.json"
)

$ErrorActionPreference = "Stop"
function Step($m) { Write-Host "==> $m" -ForegroundColor Cyan }
function Warn($m) { Write-Host "    ! $m" -ForegroundColor Yellow }

# ---- 1. Acquire the zip ---------------------------------------------------------------------------
if ($FromZip) {
    if (-not (Test-Path $FromZip)) { throw "Zip not found: $FromZip" }
    $zip = (Resolve-Path $FromZip).Path
} else {
    Step "Finding the $Version release on github.com/$Repo"
    $api = if ($Version -eq "latest") { "https://api.github.com/repos/$Repo/releases/latest" }
           else { "https://api.github.com/repos/$Repo/releases/tags/v$($Version.TrimStart('v'))" }
    $rel = Invoke-RestMethod -Uri $api -Headers @{ "User-Agent" = "GpibMcp-Installer" }
    $asset = $rel.assets | Where-Object { $_.name -like "GpibMcp-*-win-x86.zip" } | Select-Object -First 1
    if (-not $asset) { throw "No 'GpibMcp-*-win-x86.zip' asset on release '$($rel.tag_name)'. Is the release published?" }
    $zip = Join-Path $env:TEMP $asset.name
    Step "Downloading $($asset.name) ($([math]::Round($asset.size/1MB,1)) MB)"
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zip
}

# ---- 2. Unzip to the canonical install dir --------------------------------------------------------
Step "Installing to $InstallDir"
if (Test-Path $InstallDir) { Remove-Item $InstallDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Expand-Archive -Path $zip -DestinationPath $InstallDir -Force
$exe = Join-Path $InstallDir "GpibMcp.exe"
if (-not (Test-Path $exe)) { throw "GpibMcp.exe not found under $InstallDir after unzip." }
Write-Host "    server: $exe"

# ---- 3. Register with the chosen client(s) --------------------------------------------------------
function Merge-McpConfig([string]$path, [string]$parentKey, [bool]$withType) {
    $dir = Split-Path -Parent $path
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    if (Test-Path $path) {
        Copy-Item $path "$path.bak" -Force                       # back up before editing
        $root = Get-Content $path -Raw | ConvertFrom-Json
    } else { $root = [pscustomobject]@{} }
    if (-not ($root.PSObject.Properties.Name -contains $parentKey)) {
        $root | Add-Member -NotePropertyName $parentKey -NotePropertyValue ([pscustomobject]@{}) -Force
    }
    $server = if ($withType) { [pscustomobject]@{ type = "stdio"; command = $exe; args = @() } }
              else           { [pscustomobject]@{ command = $exe; args = @() } }
    $root.$parentKey | Add-Member -NotePropertyName "gpib" -NotePropertyValue $server -Force
    $root | ConvertTo-Json -Depth 12 | Set-Content -Path $path -Encoding UTF8
    Write-Host "    wrote $path"
}

# VS Code is registered by writing its user mcp.json directly (same as the others). This avoids the
# PowerShell -> code.cmd quote-stripping that mangles `code --add-mcp "<json>"`, and works whether or not
# the `code` CLI is on PATH.
switch ($Client) {
    "vscode"   { Step "Registering with VS Code"; Merge-McpConfig $VSCodeConfigPath   "servers"    $true  }
    "cursor"   { Step "Registering with Cursor";   Merge-McpConfig $CursorConfigPath   "mcpServers" $false }
    "windsurf" { Step "Registering with Windsurf"; Merge-McpConfig $WindsurfConfigPath "mcpServers" $false }
    "all"      { Step "Registering with VS Code"; Merge-McpConfig $VSCodeConfigPath   "servers"    $true
                 Step "Registering with Cursor";   Merge-McpConfig $CursorConfigPath   "mcpServers" $false
                 Step "Registering with Windsurf"; Merge-McpConfig $WindsurfConfigPath "mcpServers" $false }
    "none"     { Write-Host "    (no client registered - pass -Client vscode|cursor|windsurf|all to wire one up)" -ForegroundColor DarkGray }
}

# ---- 4. Done ---------------------------------------------------------------------------------------
Write-Host ""
Step "Installed."
Write-Host "    Ensure NI-VISA / NI-488.2 is installed (the GPIB driver)." -ForegroundColor DarkGray
if ($Client -ne "none") { Write-Host "    Restart/refresh the client's MCP servers to pick up 'gpib'." -ForegroundColor DarkGray }
