<#
.SYNOPSIS
    Builds GPIB MCP's installer, GpibMcp-Setup-<version>.exe, with Inno Setup.

.DESCRIPTION
    1. Builds the solution, Release x86, stamped with -Version (unless -SkipBuild reuses a build already
       stamped with it).
    2. Stages the server into one folder, without debug symbols, and checks the staged program reports the
       version it was asked to build.
    3. Compiles packaging\GpibMcp.iss into dist\.
    4. Writes dist\sandbox\, to try the package in Windows Sandbox, which has no NI drivers.

    Nothing NI publishes is in the package. The installer runs the program's own `check` before it installs
    anything, and stops with links to NI's downloads when NI-VISA or NI-488.2 is missing or older than this
    build needs (see GpibMcp.iss).

.EXAMPLE
    ./packaging/Build-Installer.ps1 -Version 0.4.0
.EXAMPLE
    ./packaging/Build-Installer.ps1 -Version 0.4.0 -SkipBuild     # after `dotnet build ... -p:Version=0.4.0`
#>
[CmdletBinding()]
param(
    [string] $Version,
    [switch] $SkipBuild,
    [string] $Configuration = 'Release',
    [string] $OutputDir,
    [string] $Iscc = (@("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
                        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1)
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not $OutputDir) { $OutputDir = Join-Path $repo 'dist' }
$buildOut = Join-Path $repo "src\GpibMcp\bin\x86\$Configuration\net472"
$stage = Join-Path $OutputDir 'installer-stage'

function Write-Step([string] $text) { Write-Host "==> $text" -ForegroundColor Cyan }

if (-not $Version) {
    $Version = ([xml](Get-Content (Join-Path $repo 'src\Directory.Build.props'))).Project.PropertyGroup.Version |
        Where-Object { $_ } | Select-Object -First 1
}
if ($Version -notmatch '^\d+\.\d+\.\d+([-.].+)?$') { throw "Version '$Version' is not an X.Y.Z version." }
if (-not $Iscc) { throw 'Inno Setup 6 was not found. Install it (winget install JRSoftware.InnoSetup), or pass -Iscc.' }

# ---- 1. build -------------------------------------------------------------------------------------
if (-not $SkipBuild) {
    Write-Step "Building $Configuration x86, version $Version"
    & dotnet build (Join-Path $repo 'GPIB-MCP.sln') -c $Configuration -p:Platform=x86 "-p:Version=$Version" -clp:ErrorsOnly
    if ($LASTEXITCODE -ne 0) { throw "build failed (exit $LASTEXITCODE)" }
}

# ---- 2. stage -------------------------------------------------------------------------------------
Write-Step "Staging -> $stage"
if (-not (Test-Path (Join-Path $buildOut 'GpibMcp.exe'))) { throw "No build output at $buildOut." }
if (Test-Path $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item (Join-Path $buildOut '*') $stage -Recurse
Get-ChildItem $stage -Recurse -Include *.pdb | Remove-Item -Force

foreach ($required in 'GpibMcp.exe', 'GpibMcp.exe.config', 'GpibMcp.Core.dll', 'GpibMcp.NiVisa.dll', 'Newtonsoft.Json.dll') {
    if (-not (Test-Path (Join-Path $stage $required))) { throw "The staged build has no $required." }
}
if (-not (Get-ChildItem (Join-Path $stage 'data\instruments') -Filter *.json -ErrorAction SilentlyContinue)) {
    throw 'The staged build has no instrument database (data\instruments\*.json).'
}

# The program says which build it is; an installer must not carry a different one than its name claims.
$reported = (& (Join-Path $stage 'GpibMcp.exe') --version | Select-Object -First 1) -replace '^\S+\s+', ''
if ($reported -notlike "$Version*") {
    throw "The staged GpibMcp.exe reports '$reported', not $Version. Rebuild without -SkipBuild, or build with -p:Version=$Version."
}
$files = Get-ChildItem $stage -Recurse -File
Write-Host ("    {0}: {1} files, {2:N1} MB" -f $reported, $files.Count, (($files | Measure-Object Length -Sum).Sum / 1MB))

# ---- 3. compile -----------------------------------------------------------------------------------
Write-Step 'Compiling the installer'
$defines = [ordered]@{
    AppVersion  = $Version
    # The four-part number Windows shows in the file's properties.
    FileVersion = ($Version -replace '[+-].*$', '') + '.0'
    StageDir    = $stage
    OutputDir   = $OutputDir
    RepoDir     = $repo
}
$arguments = @($defines.GetEnumerator() | ForEach-Object { "/D$($_.Key)=$($_.Value)" }) + (Join-Path $PSScriptRoot 'GpibMcp.iss')
& $Iscc /Q @arguments
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed (exit $LASTEXITCODE)." }

$package = Join-Path $OutputDir "GpibMcp-Setup-$Version.exe"
if (-not (Test-Path $package)) { throw "Inno Setup reported success but $package is not there." }
$hash = (Get-FileHash $package -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host ("    {0}: {1:N1} MB, SHA-256 {2}" -f (Split-Path $package -Leaf), ((Get-Item $package).Length / 1MB), $hash)

# ---- 4. a sandbox to try it in --------------------------------------------------------------------
# Windows Sandbox is a clean Windows, thrown away when closed. packaging\Invoke-SandboxTest.ps1 starts one
# of these and waits for its summary. In sandbox\:
#   test-in-sandbox.wsb       without NI's drivers: Setup must stop, silently and in the wizard, and leave
#                             nothing. Results in results\ (ni-missing.png is the dialog).
#   test-full-in-sandbox.wsb  turns off the sandbox's Smart App Control (it holds each MSI two minutes
#                             there), installs NI-VISA and NI-488.2 from kit\ni, restarts, installs GPIB MCP,
#                             checks Claude Desktop gets it and it serves MCP, and uninstalls. Results in
#                             results-full\.
#   try-in-sandbox.wsb        just opens C:\Installer, to click through by hand.
#   kit\                      what the sandbox sees as C:\Installer: the package and the test script.
#   kit\ni\                   NI's installers for the full test - ni-visa_*_online.exe and
#                             ni-488.2_*_online.exe from ni.com. Put there once; kept across builds.
# Both tests run packaging\test-in-sandbox.ps1 at logon, and the sandbox shuts itself down when it ends -
# passed, failed or fell over - or restarts when the script exits 194, for NI's restart. The script does
# that itself; the logon command does it too, should the script end before it can.
# Only kit\ and the results folders are mapped, never the output folder: once a sandbox has mapped a
# folder, Windows keeps it shared after the sandbox closes, and Inno Setup can no longer write into it.
# The sandbox doesn't share the host's graphics card (vGPU): twice on 2026-10-13 the host hard-reset while
# a test sandbox was running or had just been closed, and vGPU is the usual cause. The tests need no GPU.
$sandboxDir = Join-Path $OutputDir 'sandbox'
$kit = Join-Path $sandboxDir 'kit'
New-Item -ItemType Directory -Force $kit, (Join-Path $kit 'ni') | Out-Null
Get-ChildItem $kit -Filter 'GpibMcp-Setup-*.exe' | Remove-Item -Force
Copy-Item -LiteralPath $package, (Join-Path $PSScriptRoot 'test-in-sandbox.ps1') -Destination $kit
function Write-Sandbox([string] $name, [string] $results, [string] $command) {
    $resultsFolder = ''
    if ($results) {
        $hostResults = Join-Path $sandboxDir $results
        New-Item -ItemType Directory -Force $hostResults | Out-Null
        $resultsFolder = @"

    <MappedFolder>
      <HostFolder>$hostResults</HostFolder>
      <SandboxFolder>C:\Results</SandboxFolder>
      <ReadOnly>false</ReadOnly>
    </MappedFolder>
"@
    }
    @"
<Configuration>
  <MappedFolders>
    <MappedFolder>
      <HostFolder>$kit</HostFolder>
      <SandboxFolder>C:\Installer</SandboxFolder>
      <ReadOnly>true</ReadOnly>
    </MappedFolder>$resultsFolder
  </MappedFolders>
  <Networking>Enable</Networking>
  <VGpu>Disable</VGpu>
  <LogonCommand>
    <Command>$([System.Security.SecurityElement]::Escape($command))</Command>
  </LogonCommand>
</Configuration>
"@ | Set-Content -Path (Join-Path $sandboxDir $name) -Encoding UTF8
}
$test = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\Installer\test-in-sandbox.ps1'
# The script shuts down or restarts the sandbox itself (#163); this is for when it ends before it gets that far.
# Its log says whether cmd got here at all: after a run that went to the end, it is shutdown.exe refusing,
# as the script's shutdown is already scheduled.
$then = '& if errorlevel 194 (shutdown.exe /r /f /t 0 > C:\Results\shutdown-cmd.txt 2>&1) else (shutdown.exe /s /f /t 0 > C:\Results\shutdown-cmd.txt 2>&1)'
Write-Sandbox 'test-in-sandbox.wsb' 'results' "cmd.exe /c $test $then"
Write-Sandbox 'test-full-in-sandbox.wsb' 'results-full' "cmd.exe /c $test -WithNi $then"
Write-Sandbox 'try-in-sandbox.wsb' '' 'explorer.exe C:\Installer'
Write-Host "    Sandbox: $sandboxDir (packaging\Invoke-SandboxTest.ps1 [-Full] runs a test)"
if (-not (Get-ChildItem (Join-Path $kit 'ni') -Filter *.exe)) {
    Write-Host "    For the full test, put NI's NI-VISA and NI-488.2 installers in $(Join-Path $kit 'ni')." -ForegroundColor Yellow
}
# For callers (Generate-Packages.ps1) that attach it to a release.
$package
