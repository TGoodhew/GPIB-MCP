<#
.SYNOPSIS
    Signs GPIB MCP's own programs in a folder with a maintainer's signing script, and checks they are signed.

.DESCRIPTION
    Only this repository's own assemblies are signed: GpibMcp.exe, GpibMcp.*.dll, Hpgl.Rendering.dll and
    Srq.Completion.dll. Other publishers' DLLs (Newtonsoft.Json, NI's, Microsoft's) are theirs to sign, and are
    left as they are. A file that already carries a valid signature is not signed again.

    The signing script is given the files to sign, and must sign and timestamp them and fail if it cannot. The
    signing account and its tools live outside this repository, so its path is passed in (or comes from
    GPIBMCP_SIGN_SCRIPT), never written here. ManualForge signs the same way (TGoodhew/ManualForge,
    tools\build-installer.ps1).

.EXAMPLE
    ./packaging/Sign-OwnFiles.ps1 -Directory .\dist\installer-stage -SignScript C:\signing\Sign-Files.ps1
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Directory,
    [Parameter(Mandatory)][string] $SignScript
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $SignScript)) { throw "No signing script at $SignScript." }

$own = @(Get-ChildItem $Directory -File |
    Where-Object { $_.Name -match '^(GpibMcp\.exe|GpibMcp\..+\.dll|Hpgl\.Rendering\.dll|Srq\.Completion\.dll)$' } |
    ForEach-Object FullName)
if (-not ($own | Where-Object { $_ -like '*\GpibMcp.exe' })) { throw "No GpibMcp.exe in $Directory to sign." }

$toSign = @($own | Where-Object { (Get-AuthenticodeSignature $_).Status -ne 'Valid' })
if ($toSign) {
    & $SignScript @toSign
    if (-not $?) { throw "The signing script failed." }
}

$unsigned = @($own | Where-Object { (Get-AuthenticodeSignature $_).Status -ne 'Valid' })
if ($unsigned) { throw "Still not validly signed: $($unsigned -join ', ')" }
Write-Host ("    {0} file(s) signed by {1}" -f $own.Count, (Get-AuthenticodeSignature $own[0]).SignerCertificate.Subject)
