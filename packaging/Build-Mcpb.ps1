<#
.SYNOPSIS
    Build a Claude Desktop Extension bundle (.mcpb, formerly .dxt) for one-click install (#67).

.DESCRIPTION
    An MCP Bundle is a zip of the local MCP server plus a manifest.json (spec v0.3). This writes a manifest
    for the GPIB MCP server as a Windows **binary** server (entry_point GpibMcp.exe) and packs the staged
    build into <out>.mcpb. The user then opens the .mcpb in Claude Desktop (Settings -> Extensions) and it
    installs in one click - no manual config-file editing.

    Packing prefers the official `@anthropic-ai/mcpb` CLI (which validates the manifest against the schema);
    if Node/npx isn't available it falls back to a plain zip, which is the same on-disk format.

    NI-VISA / NI-488.2 must still be installed on the machine - the driver can't be bundled.

.EXAMPLE
    ./Build-Mcpb.ps1 -SourceDir .\dist\GpibMcp-0.2.0-win-x86 -Version 0.2.0 -OutFile .\dist\GpibMcp-0.2.0.mcpb
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourceDir,   # staged build dir (GpibMcp.exe + DLLs + data\)
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$OutFile       # the .mcpb to produce
)

$ErrorActionPreference = "Stop"
function Step($m) { Write-Host "==> $m" -ForegroundColor Cyan }

if (-not (Test-Path (Join-Path $SourceDir "GpibMcp.exe"))) {
    throw "No GpibMcp.exe under $SourceDir - build/stage first."
}

# ---- manifest.json (MCPB spec v0.3, binary server) ------------------------------------------------
$manifest = [ordered]@{
    manifest_version = "0.3"
    name             = "gpib-mcp"
    display_name     = "GPIB MCP"
    version          = $Version
    description      = "Control GPIB / VISA test-and-measurement instruments from Claude (Windows; requires NI-VISA / NI-488.2)."
    long_description = "Connects Claude to your bench instruments over GPIB, USB-TMC, LXI/TCPIP and serial via NI-VISA / NI-488.2: discover instruments, exchange SCPI / IEEE-488.2 commands, capture an instrument's screen as a plot or print, run whole sweeps in one call, and wait on SRQ completion. Windows x86 only; the NI-VISA and NI-488.2 drivers must be installed separately (they cannot be bundled)."
    author           = [ordered]@{ name = "Tony Goodhew"; url = "https://github.com/TGoodhew" }
    homepage         = "https://github.com/TGoodhew/GPIB-MCP"
    documentation    = "https://github.com/TGoodhew/GPIB-MCP#readme"
    repository        = [ordered]@{ type = "git"; url = "https://github.com/TGoodhew/GPIB-MCP" }
    license          = "MIT"
    keywords         = @("gpib", "visa", "scpi", "ieee-488", "ni-visa", "instrument", "test-and-measurement")
    server           = [ordered]@{
        type        = "binary"
        entry_point = "GpibMcp.exe"
        mcp_config  = [ordered]@{
            command = "`${__dirname}/GpibMcp.exe"   # ${__dirname} = the installed extension dir (literal in the manifest)
            args    = @()
            env     = [ordered]@{}
        }
    }
    compatibility    = [ordered]@{
        platforms      = @("win32")
        claude_desktop = ">=0.10.0"
    }
    # A representative subset for the Extensions UI (the server advertises ~29 tools via tools/list).
    tools            = @(
        [ordered]@{ name = "visa_list_resources";       description = "Discover connected GPIB / VISA / USB-TMC / LXI instruments" }
        [ordered]@{ name = "visa_identify";             description = "Identify an instrument (*IDN?)" }
        [ordered]@{ name = "assign_instrument";         description = "Tell the server which model sits at an address" }
        [ordered]@{ name = "visa_query";                description = "Send a command and read the reply" }
        [ordered]@{ name = "visa_write_raw";            description = "Write raw/binary bytes verbatim (e.g. forward a plot to a plotter)" }
        [ordered]@{ name = "instrument_capture_screen"; description = "Capture the instrument screen (HP-GL plot or PCL print) as an image" }
        [ordered]@{ name = "gpib_batch";                description = "Run a whole measurement sweep in one call" }
        [ordered]@{ name = "instrument_wait_complete";  description = "Wait for an operation to truly finish (SRQ)" }
    )
}

# ---- stage manifest + server files, then pack -----------------------------------------------------
$packDir = Join-Path ([System.IO.Path]::GetTempPath()) ("mcpb_" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $packDir | Out-Null
try {
    Copy-Item "$SourceDir\*" $packDir -Recurse -Force
    $manifest | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $packDir "manifest.json") -Encoding UTF8

    if (Test-Path $OutFile) { Remove-Item $OutFile -Force }

    $packed = $false
    if (Get-Command npx -ErrorAction SilentlyContinue) {
        Step "Packing with @anthropic-ai/mcpb (validates the manifest)"
        & npx --yes @anthropic-ai/mcpb pack $packDir $OutFile 2>&1 | Write-Host
        if ($LASTEXITCODE -eq 0 -and (Test-Path $OutFile)) { $packed = $true }
        else { Write-Warning "mcpb CLI unavailable/failed; falling back to a plain zip (same format)." }
    }
    if (-not $packed) {
        Step "Packing manually (zip -> .mcpb)"
        $tmpZip = "$OutFile.zip"
        if (Test-Path $tmpZip) { Remove-Item $tmpZip -Force }
        Compress-Archive -Path "$packDir\*" -DestinationPath $tmpZip
        Move-Item $tmpZip $OutFile -Force
    }
}
finally { Remove-Item $packDir -Recurse -Force -ErrorAction SilentlyContinue }

Step "Built $OutFile ($([math]::Round((Get-Item $OutFile).Length/1MB,1)) MB)"
