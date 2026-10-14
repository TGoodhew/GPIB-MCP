<#
.SYNOPSIS
    Runs GPIB MCP's installer in a Windows Sandbox: without NI's drivers it must stop; with them (-WithNi) it
    must install, connect Claude Desktop, serve MCP, and uninstall cleanly.

.DESCRIPTION
    Run at logon by the .wsb files packaging\Build-Installer.ps1 writes. It shuts the sandbox down (or
    restarts it) itself as it ends; the .wsb does too, should this end before it gets that far.
    packaging\Invoke-SandboxTest.ps1 starts one and waits for it.

    Without -WithNi (test-in-sandbox.wsb): the sandbox is a clean Windows with no NI-VISA and no NI-488.2,
    the one place to see what someone without them sees. Setup must stop before installing anything, say
    what is missing, and leave nothing behind - silently (exit code, log) and in the wizard (the dialog,
    screenshotted and read).

    With -WithNi (test-full-in-sandbox.wsb): first turns off the sandbox's Smart App Control, which
    otherwise holds each of NI's MSIs for two minutes (see Disable-SmartAppControl). Then installs NI-VISA
    and NI-488.2 from NI's own installers in C:\Installer\ni, the way a user would, with .NET support. Each
    of those needs a restart, so this exits with $RestartCode, the .wsb restarts the sandbox (which keeps
    its state), and this runs again at logon to carry on. Claude Desktop is not in the sandbox, so its settings folder stands in for it - that folder
    is what Setup looks for. Then: a silent install, the NI check passing, Claude Desktop's settings naming
    the installed server, an MCP session with the server started exactly as Claude Desktop would start it,
    and a silent uninstall that takes the registration back out and leaves nothing.

    Reads the installers from C:\Installer (read-only) and writes everything to C:\Results, a folder on the
    host: transcript.txt, the installers' logs, and summary.txt last of all, which says the run has finished.

    Windows PowerShell 5.1: it is what a clean Windows has.
#>
param([switch] $WithNi)

$ErrorActionPreference = 'Continue'
$installerDir = 'C:\Installer'
$results = 'C:\Results'
$app = Join-Path $env:LOCALAPPDATA 'Programs\GpibMcp'
$exe = Join-Path $app 'GpibMcp.exe'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{5D6C3F1E-8B0A-4C7E-9E2B-6A1F0C4D7B93}_is1'
$niVisaUrl = 'https://www.ni.com/en/support/downloads/drivers/download.ni-visa.html'
$ni4882Url = 'https://www.ni.com/en/support/downloads/drivers/download.ni-488-2.html'
$nipkg = Join-Path $env:ProgramFiles 'National Instruments\NI Package Manager\nipkg.exe'
$claudeDir = Join-Path $env:APPDATA 'Claude'
$claudeConfig = Join-Path $claudeDir 'claude_desktop_config.json'

# Inside the sandbox, so it survives the restart NI asks for and is gone with the sandbox.
$phaseFile = 'C:\gpib-mcp-test-phase.txt'
# The .wsb restarts the sandbox when this exits with this code, and shuts it down otherwise.
$RestartCode = 194
# Checks are kept in a file, so the ones made before a restart are in the summary after it.
$checksFile = Join-Path $results 'checks.txt'

$phase = if (Test-Path $phaseFile) { (Get-Content $phaseFile -Raw).Trim() } else { '' }
if (-not $phase) {
    # A fresh sandbox: what is in C:\Results is from an earlier run.
    Get-ChildItem $results -Force -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}
Start-Transcript -Path (Join-Path $results 'transcript.txt') -Append | Out-Null
$exitCode = 0

function Write-Step([string] $text) { Write-Host ''; Write-Host "== $text" -ForegroundColor Cyan }

function Check([string] $name, [bool] $passed, [string] $detail = '') {
    $line = '{0}  {1}{2}' -f $(if ($passed) { 'PASS' } else { 'FAIL' }), $name, $(if ($detail) { " - $detail" } else { '' })
    Add-Content -Path $checksFile -Value $line -Encoding UTF8
    Write-Host $line -ForegroundColor $(if ($passed) { 'Green' } else { 'Red' })
}

# Something learned rather than tested: in the summary, never a failure.
function Note([string] $text) {
    Add-Content -Path $checksFile -Value "NOTE  $text" -Encoding UTF8
    Write-Host "NOTE  $text" -ForegroundColor Yellow
}

# Everything an install would leave, and what Setup's own temporary folder would if it were not cleaned up.
# Not the data folder, %LOCALAPPDATA%\GpibMcp: uninstalling keeps it unless asked, and silently it cannot ask.
function Get-Leftovers([datetime] $since) {
    $found = @()
    if (Test-Path $app) { $found += "program folder $app" }
    if (Test-Path 'HKCU:\Software\GpibMcp') { $found += 'registry HKCU\Software\GpibMcp' }
    if ((Get-ItemProperty 'HKCU:\Environment' -ErrorAction SilentlyContinue).GPIB_MCP_MANUALS) { $found += 'environment GPIB_MCP_MANUALS' }
    if (Test-Path $uninstallKey) { $found += 'Apps & features entry' }
    $found += Get-ChildItem $env:TEMP -Directory -Filter 'is-*.tmp' -ErrorAction SilentlyContinue |
        Where-Object { $_.CreationTime -ge $since -and -not (Test-InnoUninstallLeftover $_) } |
        ForEach-Object { "Setup temporary folder $($_.FullName)" }
    return $found
}

# Inno Setup's uninstaller runs from a copy of itself in %TEMP% and cannot delete the folder that copy runs
# from. It marks the folder done (_unins-done.tmp), and the next Inno Setup uninstall - of anything - deletes
# it. So one such folder lingers by Inno's design (seen with a bare installer of nothing, too); not ours.
function Test-InnoUninstallLeftover([System.IO.DirectoryInfo] $dir) {
    $dir.Name -like 'is-*-uninstall.tmp' -and (Test-Path (Join-Path $dir.FullName '_unins-done.tmp'))
}

function Save-Screen([string] $path) {
    Add-Type -AssemblyName System.Windows.Forms, System.Drawing
    $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bitmap = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
    [System.Drawing.Graphics]::FromImage($bitmap).CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
    $bitmap.Save($path, $(if ($path -like '*.jpg') { [System.Drawing.Imaging.ImageFormat]::Jpeg } else { [System.Drawing.Imaging.ImageFormat]::Png }))
}

function Get-Setup {
    Get-ChildItem $installerDir -Filter 'GpibMcp-Setup-*.exe' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
}

function Test-NiPresent {
    [bool] (Get-ChildItem 'HKLM:\SOFTWARE\WOW6432Node\National Instruments', 'HKLM:\SOFTWARE\National Instruments' -ErrorAction SilentlyContinue)
}

# The versions of an assembly in the .NET 4 GAC, as GpibMcp.exe check reads them.
function Get-GacVersions([string] $assembly) {
    Get-ChildItem "$env:windir\Microsoft.NET\assembly\GAC_MSIL\$assembly", "$env:windir\Microsoft.NET\assembly\GAC_32\$assembly" -Directory -ErrorAction SilentlyContinue |
        ForEach-Object { ($_.Name -split '_')[1] }
}

function Invoke-Program([string] $path, [string[]] $arguments) {
    Write-Host "> $(Split-Path $path -Leaf) $($arguments -join ' ')"
    $output = & $path @arguments 2>&1 | Out-String
    $code = $LASTEXITCODE
    Write-Host $output
    return [pscustomobject]@{ Code = $code; Output = $output }
}

# ---- an MCP session, as an MCP client holds one: JSON-RPC, a line each way over stdio --------------

function Start-McpServer([string] $command, [string[]] $arguments, $environment) {
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = $command
    $info.Arguments = (@($arguments) | Where-Object { $_ } | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $info.UseShellExecute = $false
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = New-Object System.Text.UTF8Encoding $false
    if ($environment) {
        foreach ($property in $environment.PSObject.Properties) { $info.EnvironmentVariables[$property.Name] = [string] $property.Value }
    }
    $process = [System.Diagnostics.Process]::Start($info)
    $process.StandardInput.NewLine = "`n"
    $process.StandardInput.AutoFlush = $true
    return [pscustomobject]@{ Process = $process; Errors = $process.StandardError.ReadToEndAsync() }
}

function Send-Mcp($server, [hashtable] $message) {
    $message.jsonrpc = '2.0'
    $json = $message | ConvertTo-Json -Depth 10 -Compress
    Write-Host "-> $json"
    $server.Process.StandardInput.WriteLine($json)
}

# Sends a request and returns its response, passing over anything else the server says first.
function Invoke-Mcp($server, [int] $id, [string] $method, [hashtable] $params) {
    Send-Mcp $server @{ id = $id; method = $method; params = $params }
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline) {
        $read = $server.Process.StandardOutput.ReadLineAsync()
        if (-not $read.Wait([Math]::Max(1, [int] ($deadline - (Get-Date)).TotalMilliseconds))) { break }
        $line = $read.Result
        if ($null -eq $line) { Write-Host '<- (the server closed its output)'; return $null }
        Write-Host ('<- ' + $(if ($line.Length -gt 400) { $line.Substring(0, 400) + '...' } else { $line }))
        try { $response = $line | ConvertFrom-Json } catch { continue }
        if ($response.id -eq $id) { return $response }
    }
    Write-Host "<- (no response to $method within 60 s)"
    return $null
}

# ---- without NI: Setup must stop ------------------------------------------------------------------

function Test-WithoutNi {
    $setup = Get-Setup
    Write-Step 'Before'
    Write-Host "Installer : $($setup.Name)"
    Write-Host "Windows   : $([Environment]::OSVersion.VersionString)"
    Write-Host "User      : $env:USERNAME"
    Check 'no NI software here (the point of the test)' (-not (Test-NiPresent))
    $before = Get-Leftovers ([datetime]::MinValue)
    Write-Host "Already present: $(if ($before) { $before -join '; ' } else { 'nothing' })"

    Write-Step 'Installing silently'
    $started = Get-Date
    $install = Start-Process $setup.FullName -Wait -PassThru -ArgumentList @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/TASKS=mcp', "/LOG=`"$results\install-silent.log`"")
    # Inno Setup exits 1 when InitializeSetup returns False.
    Check 'silent install refuses (exit code 1)' ($install.ExitCode -eq 1) "code $($install.ExitCode)"
    $log = Get-Content "$results\install-silent.log" -Raw -ErrorAction SilentlyContinue
    Check 'the check ran and found NI missing (exit 3)' ($log -match 'NI check \(exit 3\)') (($log -split "`r?`n" | Select-String 'NI check \(exit') -join '')
    Check 'the log says why it stopped' ($log -match 'Stopping: the NI drivers are not installed\.')
    Check 'the report names NI-VISA''s download' ($log.Contains($niVisaUrl))
    Check 'the report names NI-488.2''s download' ($log.Contains($ni4882Url))
    Check 'not mistaken for the check failing' ($log -notmatch 'the NI driver check failed')
    $left = Get-Leftovers $started
    Check 'nothing installed or left behind' (-not $left) ($left -join '; ')

    Write-Step 'Running the wizard'
    $started = Get-Date
    $wizard = Start-Process $setup.FullName -PassThru -ArgumentList "/LOG=`"$results\install-wizard.log`""
    # The .exe unpacks and starts a .tmp beside it in %TEMP%, which is what shows the dialog.
    $dialog = $null
    for ($i = 0; $i -lt 60 -and -not $dialog; $i++) {
        Start-Sleep -Seconds 1
        $dialog = Get-Process | Where-Object { $_.ProcessName -like 'GpibMcp-Setup-*' -and $_.Path -like '*.tmp' -and $_.MainWindowHandle -ne 0 } |
            Select-Object -First 1
    }
    Check 'Setup shows a window' ([bool] $dialog) $(if ($dialog) { "'$($dialog.MainWindowTitle)'" } else { 'none within 60 s' })
    if ($dialog) {
        Start-Sleep -Seconds 2
        $shell = New-Object -ComObject WScript.Shell
        $shell.AppActivate($dialog.Id) | Out-Null
        Start-Sleep -Milliseconds 500
        Save-Screen "$results\ni-missing.png"
        Write-Host "Screenshot: $results\ni-missing.png"
        Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
        $window = [System.Windows.Automation.AutomationElement]::FromHandle($dialog.MainWindowHandle)
        $text = ($window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
            ForEach-Object { $_.Current.Name } | Where-Object { $_ }) -join "`r`n"
        Set-Content -Path "$results\ni-missing.txt" -Value $text -Encoding UTF8
        Write-Host $text
        Check 'the dialog says what is missing' ($text -match 'MISSING\s+NI-VISA runtime' -and $text -match 'MISSING\s+NI-488\.2 driver')
        Check 'the dialog gives both downloads' ($text.Contains($niVisaUrl) -and $text.Contains($ni4882Url))
        Check 'the dialog does not lead with the build' ($text -notmatch 'gpib-mcp \d') (($text -split "`r?`n" | Select-String 'gpib-mcp') -join ' / ')
        # "No": do not open NI's pages in the sandbox's browser.
        $shell.AppActivate($dialog.Id) | Out-Null
        $shell.SendKeys('n')
    }
    if (-not $wizard.WaitForExit(30000)) {
        Check 'Setup ends when the dialog is answered' $false 'still running after 30 s; killed'
        Get-Process | Where-Object { $_.ProcessName -like 'GpibMcp-Setup-*' } | Stop-Process -Force
    }
    else {
        Check 'Setup ends when the dialog is answered, without installing (exit code 1)' ($wizard.ExitCode -eq 1) "code $($wizard.ExitCode)"
    }
    $log = Get-Content "$results\install-wizard.log" -Raw -ErrorAction SilentlyContinue
    Check 'the wizard ran the same check' ($log -match 'NI check \(exit 3\)')
    Start-Sleep -Seconds 2
    $left = Get-Leftovers $started
    Check 'nothing installed or left behind by the wizard' (-not $left) ($left -join '; ')
}

# ---- with NI, part 1: install NI's drivers as a user would, then restart --------------------------

# What GpibMcp.NiVisa.dll needs from NI beyond the drivers themselves: the .NET support NI's installers
# offer. Named by pattern, so a newer NI release's package names still match.
$dotnetPackages = @(
    @{ Pattern = '^ni-visa-[\d.]+-dotnet-runtime$'; What = 'NI-VISA .NET runtime' },
    @{ Pattern = '^ni-visa-dotnet-shared-components-[\d.]+$'; What = 'VISA.NET Shared Components' },
    @{ Pattern = '^ni-488\.2-dotnet-fx45-[\d.]+-runtime$'; What = 'NI-488.2 .NET runtime (.NET 4.5)' },
    @{ Pattern = '^ni-488\.2-dotnet-fx40-[\d.]+-runtime$'; What = 'NI-488.2 .NET runtime (.NET 4.0)' }
)

# Waits for the installer itself - not, as Start-Process -Wait does, for every process it leaves running,
# which for NI's includes services that never exit. Says what is running each minute and takes a picture
# every five, so a stall shows what it stalled on. Returns the exit code, or $null if it ran out of time.
function Wait-Installer([System.Diagnostics.Process] $process, [string] $label, [int] $minutes) {
    $null = $process.Handle   # without it, Windows PowerShell loses the exit code
    $started = Get-Date
    $deadline = $started.AddMinutes($minutes)
    $tick = 0
    while (-not $process.HasExited -and (Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 15
        $tick++
        if ($tick % 4 -eq 0) {
            $running = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -match '^(ni|install|nipkg|msiexec|setup)' -or $_.ExecutablePath -like '*National Instruments*' } |
                ForEach-Object { $_.Name } | Sort-Object -Unique
            Write-Host ("  {0:N0} min: {1}" -f ((Get-Date) - $started).TotalMinutes, ($running -join ', '))
        }
        if ($tick % 20 -eq 0) { Save-Screen ("$results\{0}-{1:N0}min.jpg" -f $label, ((Get-Date) - $started).TotalMinutes) }
    }
    if (-not $process.HasExited) {
        Save-Screen "$results\$label-gave-up.jpg"
        return $null
    }
    return $process.ExitCode
}

# NI's installers log to the temporary folder and to NI Package Manager's own folders.
function Save-NiLogs([datetime] $since, [string] $label) {
    $to = Join-Path $results "ni-logs\$label"
    New-Item -ItemType Directory -Force $to | Out-Null
    Get-ChildItem $env:TEMP, "$env:ProgramData\National Instruments", "$env:LOCALAPPDATA\National Instruments" -Recurse -File -Include *.log, *.txt -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -ge $since -and $_.Length -lt 20MB } |
        ForEach-Object { Copy-Item $_.FullName (Join-Path $to ($_.FullName -replace '[:\\]', '_')) -ErrorAction SilentlyContinue }
}

function Get-NipkgNames([string] $command) {
    & $nipkg $command 2>$null | ForEach-Object { ($_ -split "`t")[0] } | Where-Object { $_ }
}

# The sandbox runs Smart App Control in evaluation mode, which holds every MSI for its cloud reputation
# check - and in the sandbox that check never answers, so each MSI waits out a 120-second timeout (it is
# Windows Installer's "SOFTWARE RESTRICTION POLICY: Verifying package" step, ended by AppLocker event 8045).
# NI's drivers are well over a hundred MSIs. Turned off here, inside the throwaway sandbox only; Windows
# reads it at boot, so this restarts before installing anything.
function Disable-SmartAppControl {
    Write-Step 'Smart App Control (sandbox only)'
    $key = 'HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy'
    $state = (Get-ItemProperty $key -ErrorAction SilentlyContinue).VerifiedAndReputablePolicyState
    Write-Host "VerifiedAndReputablePolicyState was $state (0 off, 1 on, 2 evaluation)"
    Set-ItemProperty $key -Name VerifiedAndReputablePolicyState -Value 0 -Type DWord
}

function Install-Ni {
    Write-Step 'Before'
    Note ("Smart App Control state after the restart: {0}" -f (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy' -ErrorAction SilentlyContinue).VerifiedAndReputablePolicyState)
    Write-Host "Installer : $((Get-Setup).Name)"
    Write-Host "Windows   : $([Environment]::OSVersion.VersionString)"
    Check 'no NI software here before the test installs it' (-not (Test-NiPresent))

    $niInstallers = @(Get-ChildItem (Join-Path $installerDir 'ni') -Filter '*.exe' -ErrorAction SilentlyContinue |
        Sort-Object { if ($_.Name -match 'visa') { 0 } else { 1 } })
    Check "NI's installers are in C:\Installer\ni" ($niInstallers.Count -ge 2) (($niInstallers | ForEach-Object { $_.Name }) -join ', ')
    if ($niInstallers.Count -lt 2) { return $false }

    foreach ($installer in $niInstallers) {
        Write-Step "Installing $($installer.Name)"
        $started = Get-Date
        # NI's documented unattended switches. NI's own restart is held back: this restarts once, after both.
        $run = Start-Process $installer.FullName -PassThru -ArgumentList '--passive', '--accept-eulas', '--prevent-reboot'
        $code = Wait-Installer $run $installer.BaseName 25
        Save-NiLogs $started $installer.BaseName
        $minutes = ((Get-Date) - $started).TotalMinutes
        if ($null -eq $code) {
            Check "$($installer.Name) installs" $false ("still running after {0:N0} min; screenshots in C:\Results" -f $minutes)
            return $false
        }
        # 0 done; -125071 done, restart needed (NI Package Manager's code).
        Check "$($installer.Name) installs" ($code -eq 0 -or $code -eq -125071) ("exit {0}, {1:N1} min" -f $code, $minutes)
    }

    Check 'NI Package Manager is installed' (Test-Path $nipkg)
    if (-not (Test-Path $nipkg)) { return $false }
    $installed = @(Get-NipkgNames 'list-installed')
    $installed | Where-Object { $_ -match 'visa|488' } | Sort-Object | Set-Content "$results\ni-packages-default.txt"

    # Whether NI's installers, left to their defaults, include the .NET support GPIB MCP needs. Worth
    # knowing: Setup's message tells people to install it "with .NET support".
    $available = @(Get-NipkgNames 'list')
    $wanted = @()
    foreach ($package in $dotnetPackages) {
        $name = $available + $installed | Where-Object { $_ -match $package.Pattern } | Sort-Object -Descending | Select-Object -First 1
        if (-not $name) { Check "NI's feed has the $($package.What)" $false "no package matches $($package.Pattern)"; continue }
        if ($installed -contains $name) { Note "NI's installers include the $($package.What) by default ($name)" }
        else { Note "NI's installers leave out the $($package.What) by default; adding $name"; $wanted += $name }
    }
    if ($wanted) {
        Write-Step '.NET support'
        $run = Invoke-Program $nipkg (@('install', '--accept-eulas', '-y') + $wanted)
        Check 'NI .NET support installs' ($run.Code -eq 0 -or $run.Code -eq -125071) "exit $($run.Code)"
    }
    & $nipkg list-installed 2>$null | Where-Object { $_ -match 'visa|488' } | Sort-Object | Set-Content "$results\ni-packages.txt"
    return $true
}

# ---- with NI, part 2: GPIB MCP ---------------------------------------------------------------------

function Test-WithNi {
    Write-Step 'NI, after the restart'
    Check 'the sandbox kept NI across the restart' (Test-NiPresent)
    foreach ($assembly in 'NationalInstruments.Visa', 'Ivi.Visa', 'NationalInstruments.NI4882', 'NationalInstruments.Common') {
        Write-Host ("{0,-28} {1}" -f $assembly, ((Get-GacVersions $assembly) -join ', '))
    }

    Write-Step 'Claude Desktop, standing in'
    # Not installed here. Its settings folder is what Setup looks for, and a server already in its settings
    # shows Setup adds its own entry without disturbing anyone else's.
    New-Item -ItemType Directory -Force $claudeDir | Out-Null
    '{ "mcpServers": { "someone-else": { "command": "C:\\Tools\\other-server.exe", "args": [ "--flag" ] } } }' |
        Set-Content -Path $claudeConfig -Encoding UTF8
    Get-Content $claudeConfig | Write-Host

    Write-Step 'Installing silently'
    $setup = Get-Setup
    $version = $setup.BaseName -replace '^GpibMcp-Setup-', ''
    $install = Start-Process $setup.FullName -Wait -PassThru -ArgumentList @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/TASKS=mcp', "/LOG=`"$results\install.log`"")
    Check 'installer exit code 0' ($install.ExitCode -eq 0) "code $($install.ExitCode)"
    $log = Get-Content "$results\install.log" -Raw -ErrorAction SilentlyContinue
    Check 'Setup''s NI check passed (exit 0)' ($log -match 'NI check \(exit 0\)') (($log -split "`r?`n" | Select-String 'NI check \(exit') -join '')
    Check 'setup mcp ran (exit 0)' ($log -match 'setup mcp \(exit 0\)')
    Check 'the log names Claude Desktop as registered' ($log -match 'Claude Desktop\s+registered')
    Check 'the log says which build was installed' ($log -match "Installed: gpib-mcp $([regex]::Escape($version))")

    Write-Step 'What the installer put on the machine'
    Check 'GpibMcp.exe present' (Test-Path $exe)
    $files = Get-ChildItem $app -Recurse -File -ErrorAction SilentlyContinue
    Write-Host ("Program folder: {0} files, {1:N1} MB" -f $files.Count, (($files | Measure-Object Length -Sum).Sum / 1MB))
    Check 'Apps & features entry' (Test-Path $uninstallKey)
    $run = Invoke-Program $exe @('--version')
    Check 'GpibMcp.exe --version is the installer''s version' ($run.Code -eq 0 -and $run.Output -match "gpib-mcp $([regex]::Escape($version))") (($run.Output -split "`r?`n")[0])
    $run = Invoke-Program $exe @('check')
    Set-Content -Path "$results\check.txt" -Value $run.Output -Encoding UTF8
    Check 'GpibMcp.exe check passes' ($run.Code -eq 0) "exit $($run.Code)"
    Check 'NI-VISA starts' ($run.Output -match 'OK\s+NI-VISA starts') (($run.Output -split "`r?`n" | Select-String 'NI-VISA starts') -join '')

    Write-Step 'Claude Desktop''s settings'
    Get-Content $claudeConfig | Write-Host
    $config = Get-Content $claudeConfig -Raw | ConvertFrom-Json
    $entry = $config.mcpServers.gpib
    Check 'Claude Desktop has a gpib server' ([bool] $entry)
    Check 'it runs the installed GpibMcp.exe' ($entry.command -eq $exe) "$($entry.command)"
    Check 'the server already there is untouched' ($config.mcpServers.'someone-else'.command -eq 'C:\Tools\other-server.exe' -and
        @($config.mcpServers.'someone-else'.args) -join ' ' -eq '--flag')

    Write-Step 'An MCP session, started as Claude Desktop starts it'
    if ($entry) {
        $server = Start-McpServer $entry.command @($entry.args) $entry.env
        $init = Invoke-Mcp $server 1 'initialize' @{
            protocolVersion = '2025-06-18'; capabilities = @{}; clientInfo = @{ name = 'claude-ai'; version = '0.0.0-sandbox' } }
        Check 'initialize' ($init -and $init.result.serverInfo.name) $(if ($init) { "$($init.result.serverInfo.name) $($init.result.serverInfo.version), protocol $($init.result.protocolVersion)" } else { 'no response' })
        Send-Mcp $server @{ method = 'notifications/initialized' }
        $tools = Invoke-Mcp $server 2 'tools/list' @{}
        $names = @($tools.result.tools | ForEach-Object { $_.name })
        Check 'tools/list' ($names.Count -gt 0 -and $names -contains 'visa_list_resources') "$($names.Count) tools"
        # Through NI-VISA, with no instruments: an answer, not an error.
        $call = Invoke-Mcp $server 3 'tools/call' @{ name = 'visa_list_resources'; arguments = @{} }
        $text = ($call.result.content | ForEach-Object { $_.text }) -join ' '
        Check 'visa_list_resources answers through NI-VISA' ($call -and -not $call.result.isError -and -not $call.error) $(if ($call.error) { $call.error.message } else { $text.Trim() })
        # A client ends a session by closing the server's input.
        $server.Process.StandardInput.Close()
        $ended = $server.Process.WaitForExit(15000)
        Check 'the server exits when its input closes' $ended $(if ($ended) { "exit $($server.Process.ExitCode)" } else { 'still running after 15 s; killed' })
        if (-not $ended) { $server.Process.Kill() }
        if ($server.Errors.Wait(5000) -and $server.Errors.Result) {
            Set-Content -Path "$results\server-stderr.txt" -Value $server.Errors.Result -Encoding UTF8
        }
    }

    Write-Step 'Uninstalling silently'
    $uninstaller = Get-ChildItem $app -Filter 'unins*.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
    Check 'uninstaller present' ([bool] $uninstaller)
    if ($uninstaller) {
        $uninstall = Start-Process $uninstaller.FullName -Wait -PassThru -ArgumentList @(
            '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$results\uninstall.log`"")
        Check 'uninstaller exit code 0' ($uninstall.ExitCode -eq 0) "code $($uninstall.ExitCode)"
        # The uninstaller copies itself to %TEMP% and returns before that copy has finished: the program
        # folder goes, and then the copy marks its folder done (see Test-InnoUninstallLeftover).
        for ($i = 0; $i -lt 60; $i++) {
            $copies = @(Get-ChildItem $env:TEMP -Directory -Filter 'is-*-uninstall.tmp' -ErrorAction SilentlyContinue)
            if (-not (Test-Path $app) -and -not ($copies | Where-Object { -not (Test-InnoUninstallLeftover $_) })) { break }
            Start-Sleep -Seconds 1
        }
        Write-Host "Uninstall finished after a further $i s."
    }
    Get-Content $claudeConfig -ErrorAction SilentlyContinue | Write-Host
    $config = Get-Content $claudeConfig -Raw -ErrorAction SilentlyContinue | ConvertFrom-Json
    Check 'Claude Desktop no longer has a gpib server' ($config -and -not $config.mcpServers.gpib)
    Check 'the other server is still there' ($config.mcpServers.'someone-else'.command -eq 'C:\Tools\other-server.exe')
    $left = Get-Leftovers ([datetime]::MinValue)
    Check 'nothing left after uninstalling' (-not $left) ($left -join '; ')
    Get-ChildItem $env:TEMP -Directory -Filter 'is-*-uninstall.tmp' -ErrorAction SilentlyContinue |
        Where-Object { Test-InnoUninstallLeftover $_ } |
        ForEach-Object { Note "Inno Setup's uninstaller left $($_.Name) in %TEMP%, marked done; the next Inno uninstall deletes it" }
    $data =Join-Path $env:LOCALAPPDATA 'GpibMcp'
    if (Test-Path $data) {
        $kept = @(Get-ChildItem $data -Recurse -File)
        Note ("kept, as a silent uninstall should: {0} ({1} files: {2})" -f $data, $kept.Count,
            (($kept | Group-Object Extension | ForEach-Object { "$($_.Count) $($_.Name)" }) -join ', '))
    }
    Check 'NI is still installed (not GPIB MCP''s to remove)' (Test-NiPresent)
}

# ---- with NI, part 3: the manual library (#159) ------------------------------------------------------

# One library for GPIB MCP and ManualForge: Setup fills the box with ManualForge's library when ManualForge
# is installed, else leaves it empty, and a blank box means no manuals. ManualForge isn't installed here; its
# installer's registry value, which is what Setup reads, stands in for it.
$forgeKey = 'HKCU:\Software\ManualForge'

function Get-ManualState {
    $config = Get-Content $claudeConfig -Raw -ErrorAction SilentlyContinue | ConvertFrom-Json
    [pscustomobject]@{
        Setup    = (Get-ItemProperty 'HKCU:\Software\GpibMcp' -Name Manuals -ErrorAction SilentlyContinue).Manuals
        Variable = (Get-ItemProperty 'HKCU:\Environment' -Name GPIB_MCP_MANUALS -ErrorAction SilentlyContinue).GPIB_MCP_MANUALS
        Claude   = $config.mcpServers.gpib.env.GPIB_MCP_MANUALS
    }
}

function Install-Quietly([string] $label, [string[]] $more) {
    $install = Start-Process (Get-Setup).FullName -Wait -PassThru -ArgumentList (@(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$results\install-$label.log`"") + $more)
    Check "$label - installs (exit 0)" ($install.ExitCode -eq 0) "code $($install.ExitCode)"
    $state = Get-ManualState
    Write-Host "  setup '$($state.Setup)', variable '$($state.Variable)', Claude Desktop '$($state.Claude)'"
    return $state
}

function Uninstall-Quietly([string] $label) {
    $uninstaller = Get-ChildItem $app -Filter 'unins*.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $uninstaller) { Check "$label - uninstaller present" $false; return }
    $run = Start-Process $uninstaller.FullName -Wait -PassThru -ArgumentList @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$results\uninstall-$label.log`"")
    for ($i = 0; $i -lt 60 -and (Test-Path $app); $i++) { Start-Sleep -Seconds 1 }
    Check "$label - uninstalls (exit 0)" ($run.ExitCode -eq 0) "code $($run.ExitCode)"
}

# ---- the wizard's manual page, driven by window messages

# Inno Setup's controls are its own classes (TNewButton, TNewRadioButton, TPasswordEdit), which UI Automation
# reports as plain panes with no patterns: it can find them by class and caption, but not press or fill them.
# So they are found through UI Automation and worked with the window messages a click or a typed value sends.
function Initialize-WizardDriver {
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    if (-not ('WizardWindow' -as [type])) {
        Add-Type @'
using System; using System.Runtime.InteropServices; using System.Text;
public static class WizardWindow {
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, string l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, StringBuilder l);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, int m, IntPtr w, IntPtr l);
}
'@
    }
}

$BM_CLICK = 0xF5; $WM_SETTEXT = 0x0C; $WM_GETTEXT = 0x0D

function Get-WizardProcess {
    for ($i = 0; $i -lt 60; $i++) {
        $p = Get-Process | Where-Object { $_.ProcessName -like 'GpibMcp-Setup-*' -and $_.Path -like '*.tmp' -and $_.MainWindowHandle -ne 0 } |
            Select-Object -First 1
        if ($p) { return $p }
        Start-Sleep -Seconds 1
    }
    return $null
}

# What is showing under $root: the current page (other pages are off screen) and any message box it opened.
function Get-Shown($root) {
    @($root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
        Where-Object { -not $_.Current.IsOffscreen })
}

function Get-Texts($root) { (Get-Shown $root | ForEach-Object { $_.Current.Name } | Where-Object { $_ }) -join "`r`n" }

# A control by caption, e.g. 'Next', 'I accept*', 'OK': Inno's own or a message box's.
function Find-Control($root, [string] $caption) {
    Get-Shown $root | Where-Object { ($_.Current.Name -replace '&', '') -like $caption -and $_.Current.IsEnabled } | Select-Object -First 1
}

# Posted, not sent: a click that opens a message box would otherwise wait until someone closed it.
function Invoke-Button($root, [string] $caption) {
    $control = Find-Control $root $caption
    if (-not $control) { return $false }
    [void][WizardWindow]::PostMessage([IntPtr]$control.Current.NativeWindowHandle, $BM_CLICK, [IntPtr]::Zero, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 1500
    return $true
}

# Starts the wizard and clicks Next until the manual page shows. Returns the process and its window, or $null.
function Open-ManualPage([string] $label) {
    Initialize-WizardDriver
    Start-Process (Get-Setup).FullName -ArgumentList "/LOG=`"$results\install-$label.log`"" | Out-Null
    $process = Get-WizardProcess
    if (-not $process) { Check "$label - the wizard opens" $false; return $null }
    $window = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    for ($i = 0; $i -lt 8; $i++) {
        if ((Get-Texts $window) -match 'Which folder holds your instrument manuals') { break }
        # The licence page holds Next back until the agreement is accepted.
        Invoke-Button $window 'I accept*' | Out-Null
        if (-not (Invoke-Button $window 'Next')) { Start-Sleep -Seconds 1 }
    }
    $onPage = (Get-Texts $window) -match 'Which folder holds your instrument manuals'
    Check "$label - the wizard reaches the manual page" $onPage
    if (-not $onPage) { Save-Screen "$results\$label-lost.jpg"; Stop-Setup; return $null }
    return [pscustomobject]@{ Process = $process; Window = $window }
}

function Get-ManualBox($wizard) {
    $box = Get-Shown $wizard.Window | Where-Object { $_.Current.ClassName -eq 'TPasswordEdit' } | Select-Object -First 1
    [IntPtr]$box.Current.NativeWindowHandle
}

function Set-ManualBox($wizard, [string] $value) {
    [void][WizardWindow]::SendMessage((Get-ManualBox $wizard), $WM_SETTEXT, [IntPtr]::Zero, $value)
}

function Get-ManualBoxValue($wizard) {
    $text = New-Object System.Text.StringBuilder 1024
    [void][WizardWindow]::SendMessage((Get-ManualBox $wizard), $WM_GETTEXT, [IntPtr]1024, $text)
    $text.ToString()
}

# Ends any Setup still running - the .tmp that shows the wizard and the .exe that started it - so nothing
# holds the sandbox open or leaves its temporary folder behind.
function Stop-Setup {
    Get-Process | Where-Object { $_.ProcessName -like 'GpibMcp-Setup-*' } | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
}

function Close-Wizard($wizard) {
    # Cancel, then "Exit Setup?" - Yes.
    if (Invoke-Button $wizard.Window 'Cancel') { Invoke-Button $wizard.Window 'Yes' | Out-Null }
    if (-not $wizard.Process.WaitForExit(20000)) { Stop-Setup }
}

function Test-ManualLibrary {
    $forge = 'C:\Manuals-Forge'; $mine = 'C:\Manuals-Mine'; $theirs = 'C:\Manuals-Theirs'
    New-Item -ItemType Directory -Force $forge, $mine, $theirs | Out-Null

    Write-Step 'Manual library: ManualForge installed'
    New-Item -Path $forgeKey -Force | Out-Null
    Set-ItemProperty $forgeKey -Name Library -Value $forge
    $s = Install-Quietly 'forge' @('/TASKS=mcp')
    Check 'ManualForge installed - its library is used' ($s.Setup -eq $forge -and $s.Variable -eq $forge -and $s.Claude -eq $forge) "setup '$($s.Setup)', variable '$($s.Variable)', Claude '$($s.Claude)'"

    Write-Step 'Manual library: /MANUALS= on a reinstall'
    $s = Install-Quietly 'manuals-switch' @('/TASKS=mcp', "/MANUALS=`"$mine`"")
    Check '/MANUALS wins, and the variable Setup set follows it' ($s.Setup -eq $mine -and $s.Variable -eq $mine -and $s.Claude -eq $mine) "setup '$($s.Setup)', variable '$($s.Variable)', Claude '$($s.Claude)'"

    Write-Step 'Manual library: ManualForge not installed'
    Remove-Item $forgeKey -Recurse -Force
    $s = Install-Quietly 'no-forge' @('/TASKS=mcp')
    Check 'no ManualForge - no manuals, and the variable Setup set is removed' (-not $s.Setup -and -not $s.Variable -and -not $s.Claude) "setup '$($s.Setup)', variable '$($s.Variable)', Claude '$($s.Claude)'"
    Uninstall-Quietly 'no-forge'

    Write-Step 'Manual library: an old command line naming the manuals task'
    $s = Install-Quietly 'old-tasks' @('/TASKS="mcp,manuals"', "/MANUALS=`"$mine`"")
    Check 'the old /TASKS still installs, with the folder given' ($s.Setup -eq $mine -and $s.Claude -eq $mine) "setup '$($s.Setup)', Claude '$($s.Claude)'"
    Uninstall-Quietly 'old-tasks'
    Check 'uninstalling removes the variable Setup set' (-not (Get-ManualState).Variable)

    Write-Step 'Manual library: someone else''s GPIB_MCP_MANUALS'
    Set-ItemProperty 'HKCU:\Environment' -Name GPIB_MCP_MANUALS -Value $theirs
    $s = Install-Quietly 'theirs' @('/TASKS=mcp', "/MANUALS=`"$mine`"")
    Check 'a variable Setup didn''t set is left alone' ($s.Variable -eq $theirs -and $s.Claude -eq $mine) "variable '$($s.Variable)', Claude '$($s.Claude)'"
    Uninstall-Quietly 'theirs'
    Check '... and kept on uninstall' ((Get-ManualState).Variable -eq $theirs)
    Remove-ItemProperty 'HKCU:\Environment' -Name GPIB_MCP_MANUALS

    Write-Step 'Manual page in the wizard: ManualForge installed'
    New-Item -Path $forgeKey -Force | Out-Null
    Set-ItemProperty $forgeKey -Name Library -Value $forge
    $wizard = Open-ManualPage 'wizard-forge'
    if ($wizard) {
        Save-Screen "$results\manual-page-forge.jpg"
        $text = Get-Texts $wizard.Window
        Set-Content "$results\manual-page-forge.txt" $text -Encoding UTF8
        Check 'the box holds ManualForge''s library' ((Get-ManualBoxValue $wizard) -eq $forge) "'$(Get-ManualBoxValue $wizard)'"
        Check 'the page says it is ManualForge''s library' ($text -match 'This is ManualForge''s library')
        Check 'the page says blank is fine' ($text -match 'Leave this blank')
        Check 'there is a Browse button' ([bool] (Find-Control $wizard.Window 'Browse*'))

        Set-ManualBox $wizard 'C:\No-Such-Folder'
        Invoke-Button $wizard.Window 'Next' | Out-Null
        # The message box shows under the wizard's window.
        $refused = (Get-Texts $wizard.Window) -match 'There is no folder at'
        Check 'a folder that isn''t there is refused' $refused
        if ($refused) { Save-Screen "$results\manual-page-missing-folder.jpg"; Invoke-Button $wizard.Window 'OK' | Out-Null }
        Check '... and the page stays' ((Get-Texts $wizard.Window) -match 'Which folder holds your instrument manuals')

        Set-ManualBox $wizard ''
        Invoke-Button $wizard.Window 'Next' | Out-Null
        Check 'a blank box is accepted' ((Get-Texts $wizard.Window) -notmatch 'Which folder holds your instrument manuals')
        Save-Screen "$results\manual-page-blank-next.jpg"
        Invoke-Button $wizard.Window 'Install' | Out-Null
        for ($i = 0; $i -lt 120 -and -not (Find-Control $wizard.Window 'Finish'); $i++) { Start-Sleep -Seconds 1 }
        Invoke-Button $wizard.Window 'Finish' | Out-Null
        if (-not $wizard.Process.WaitForExit(30000)) { Stop-Setup }
        $s = Get-ManualState
        Check 'installed from the wizard with no manuals' (-not $s.Setup -and -not $s.Claude) "setup '$($s.Setup)', Claude '$($s.Claude)'"
        Uninstall-Quietly 'wizard-forge'
    }

    Write-Step 'Manual page in the wizard: ManualForge not installed'
    Remove-Item $forgeKey -Recurse -Force
    $wizard = Open-ManualPage 'wizard-no-forge'
    if ($wizard) {
        Save-Screen "$results\manual-page-no-forge.jpg"
        $text = Get-Texts $wizard.Window
        Set-Content "$results\manual-page-no-forge.txt" $text -Encoding UTF8
        Check 'the box is empty' (-not (Get-ManualBoxValue $wizard)) "'$(Get-ManualBoxValue $wizard)'"
        Check 'the page says blank is fine' ($text -match 'Leave this blank')
        Check 'the page doesn''t mention ManualForge''s library' ($text -notmatch 'This is ManualForge''s library')
        Close-Wizard $wizard
    }

    Stop-Setup
    $left = Get-Leftovers ([datetime]::MinValue)
    Check 'nothing left after the manual library tests' (-not $left) ($left -join '; ')
}

# ---- run ---------------------------------------------------------------------------------------------

$finished = $true
try {
    if (-not $WithNi) {
        Test-WithoutNi
    }
    elseif ($phase -eq '') {
        Disable-SmartAppControl
        'sac-off' | Set-Content $phaseFile
        Write-Host 'Restarting the sandbox for Smart App Control; this carries on at the next logon.'
        $finished = $false
        $exitCode = $RestartCode
    }
    elseif ($phase -eq 'sac-off') {
        # As below: if NI's installers restart the sandbox themselves, the next logon says so, not reinstalls.
        'installing-ni' | Set-Content $phaseFile
        if (Install-Ni) {
            'restarted' | Set-Content $phaseFile
            Write-Host 'Restarting the sandbox, as NI asks; this carries on at the next logon.'
            $finished = $false
            $exitCode = $RestartCode
        }
    }
    elseif ($phase -eq 'restarted') {
        # Before anything that could restart it again: a second logon in this phase must not loop.
        'testing' | Set-Content $phaseFile
        Test-WithNi
        Test-ManualLibrary
    }
    else {
        Check 'the sandbox restarted only when asked' $false "it restarted during phase '$phase'"
    }
}
catch {
    Check 'script ran to the end' $false ($_.Exception.Message + ' ' + $_.InvocationInfo.PositionMessage)
}
finally {
    Stop-Transcript | Out-Null
    if ($finished) {
        $checks = @(Get-Content $checksFile -Encoding UTF8 -ErrorAction SilentlyContinue)
        $failed = @($checks | Where-Object { $_ -like 'FAIL*' }).Count
        $tested = @($checks | Where-Object { $_ -notlike 'NOTE*' }).Count
        $title = if ($WithNi) { 'GPIB MCP sandbox test, with NI' } else { 'GPIB MCP sandbox test, without NI' }
        @("$title, $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')", "$tested checks, $failed failed", '') + $checks |
            Set-Content -Path (Join-Path $results 'summary.txt') -Encoding UTF8
    }
}
# Said before going, so a sandbox that doesn't close shows whether this got here.
Set-Content -Path (Join-Path $results 'exited.txt') -Value "exit $exitCode at $(Get-Date -Format 'HH:mm:ss')" -Encoding UTF8
# Shut down or restart from here, not only from the .wsb's cmd after this exits: once UI Automation has been
# loaded, PowerShell can hang on its way out (exit and Environment.Exit alike), and cmd then never gets to its
# shutdown (#163). The restarts, whose phases don't load it, always worked. A few seconds' grace lets this
# exit; cmd's own shutdown then finds this one already scheduled and does nothing.
$shutdown = if ($exitCode -eq $RestartCode) { '/r' } else { '/s' }
& shutdown.exe $shutdown /f /t 5 2>&1 | Out-File (Join-Path $results 'shutdown.txt') -Encoding UTF8
"shutdown.exe $shutdown exit $LASTEXITCODE at $(Get-Date -Format 'HH:mm:ss')" | Out-File (Join-Path $results 'shutdown.txt') -Append -Encoding UTF8
[Environment]::Exit($exitCode)
