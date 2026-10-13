<#
.SYNOPSIS
    Runs one of the installer's Windows Sandbox tests, waits for it, prints its summary, and makes sure the
    sandbox is closed afterwards.

.DESCRIPTION
    Starts dist\sandbox\test-in-sandbox.wsb (without NI) or, with -Full, test-full-in-sandbox.wsb (installs
    NI, restarts, then installs GPIB MCP), both written by packaging\Build-Installer.ps1. The sandbox shuts
    itself down when its test ends; if it is still open after that - or the test has not finished within
    -TimeoutMinutes - this closes it, so no sandbox is left behind either way.

    Exits 0 when every check passed, 1 when one failed or the test never finished.

.EXAMPLE
    ./packaging/Invoke-SandboxTest.ps1
.EXAMPLE
    ./packaging/Invoke-SandboxTest.ps1 -Full
#>
[CmdletBinding()]
param(
    [switch] $Full,
    [int] $TimeoutMinutes = $(if ($Full) { 75 } else { 10 }),
    [string] $SandboxDir = (Join-Path (Split-Path $PSScriptRoot -Parent) 'dist\sandbox')
)

$ErrorActionPreference = 'Stop'
$wsb = Join-Path $SandboxDir $(if ($Full) { 'test-full-in-sandbox.wsb' } else { 'test-in-sandbox.wsb' })
$results = Join-Path $SandboxDir $(if ($Full) { 'results-full' } else { 'results' })
$summary = Join-Path $results 'summary.txt'
$transcript = Join-Path $results 'transcript.txt'

function Get-Sandboxes {
    $listed = try { (& wsb.exe list --raw 2>$null | Out-String | ConvertFrom-Json).WindowsSandboxEnvironments } catch { @() }
    @($listed | ForEach-Object { if ($_ -is [string]) { $_ } else { $_.Id } } | Where-Object { $_ })
}

function Close-Sandbox {
    foreach ($id in Get-Sandboxes) { & wsb.exe stop --id $id | Out-Null }
    # The window can outlive the sandbox it showed - after a restart inside it, it always does.
    for ($i = 0; $i -lt 30 -and (Get-Process WindowsSandboxRemoteSession -ErrorAction SilentlyContinue); $i++) {
        Get-Process WindowsSandboxRemoteSession -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 1
    }
}

if (-not (Test-Path $wsb)) { throw "No $wsb. Run packaging\Build-Installer.ps1 first." }
if ($Full -and -not (Get-ChildItem (Join-Path $SandboxDir 'kit\ni') -Filter *.exe -ErrorAction SilentlyContinue)) {
    throw "The full test installs NI from $(Join-Path $SandboxDir 'kit\ni'), which has no installers. Put NI-VISA's and NI-488.2's there."
}
# Windows runs one sandbox at a time, and one already open may be someone's own.
if (Get-Sandboxes) { throw 'A Windows Sandbox is already running. Close it first.' }

$started = Get-Date
Write-Host "Started $(Split-Path $wsb -Leaf) at $($started.ToString('HH:mm:ss')); waiting up to $TimeoutMinutes min."
Start-Process $wsb

$deadline = $started.AddMinutes($TimeoutMinutes)
$shown = 0
while ((Get-Date) -lt $deadline) {
    if ((Test-Path $summary) -and (Get-Item $summary).LastWriteTime -gt $started) { break }
    # Progress: each step the test starts, as it starts it.
    if ((Test-Path $transcript) -and (Get-Item $transcript).LastWriteTime -gt $started) {
        $steps = @(Get-Content $transcript -ErrorAction SilentlyContinue | Where-Object { $_ -like '== *' -or $_ -match '^\s+\d+ min:' -or $_ -match '^(PASS|FAIL|NOTE)  ' })
        $steps | Select-Object -Skip $shown | ForEach-Object { Write-Host ("  {0}  {1}" -f (Get-Date -Format 'HH:mm:ss'), ($_ -replace '^== ', '').Trim()) }
        $shown = [Math]::Max($shown, $steps.Count)
    }
    Start-Sleep -Seconds 5
}
$finished = (Test-Path $summary) -and (Get-Item $summary).LastWriteTime -gt $started

# The sandbox shuts itself down after writing the summary; give it a minute, then close what is left.
for ($i = 0; $i -lt 60 -and $finished -and ((Get-Sandboxes) -or (Get-Process WindowsSandboxRemoteSession -ErrorAction SilentlyContinue)); $i++) {
    Start-Sleep -Seconds 1
}
if ((Get-Sandboxes) -or (Get-Process WindowsSandboxRemoteSession -ErrorAction SilentlyContinue)) {
    Write-Host $(if ($finished) { 'The sandbox did not close itself; closing it.' } else { "No summary after $TimeoutMinutes min; closing the sandbox." })
    Close-Sandbox
}

if (-not $finished) {
    Write-Host "The test did not finish. What it got to is in $transcript." -ForegroundColor Red
    exit 1
}
Write-Host ("Finished in {0:N1} min. Results: {1}" -f ((Get-Item $summary).LastWriteTime - $started).TotalMinutes, $results)
Write-Host ''
Get-Content $summary -Encoding UTF8 | ForEach-Object {
    Write-Host $_ -ForegroundColor $(if ($_ -like 'FAIL*') { 'Red' } elseif ($_ -like 'PASS*') { 'Green' } elseif ($_ -like 'NOTE*') { 'Yellow' } else { 'Gray' })
}
if (Get-Content $summary -Encoding UTF8 | Where-Object { $_ -like 'FAIL*' }) { exit 1 }
exit 0
