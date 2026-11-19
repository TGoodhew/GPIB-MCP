<#
.SYNOPSIS
    Signs files with Azure Artifact Signing, as the release workflow does: the -SignScript it passes to
    Generate-Packages.ps1, Sign-OwnFiles.ps1 and Inno Setup.

.DESCRIPTION
    Signs and timestamps every file it is given with signtool and Microsoft's Artifact Signing dlib, and fails
    if any is not then validly signed. It signs in as whoever the Azure CLI is signed in as: in release.yml
    that is the workflow's own identity, through azure/login and GitHub's OIDC token, so no secret is stored
    and nobody has to be signed in on the runner.

    The account comes from the environment, which release.yml fills from the 'release' environment's variables:
        ARTIFACT_SIGNING_ENDPOINT   e.g. https://wus2.codesigning.azure.net
        ARTIFACT_SIGNING_ACCOUNT    the Artifact Signing account
        ARTIFACT_SIGNING_PROFILE    its certificate profile

    The dlib is Microsoft.ArtifactSigning.Client from nuget.org, at the version pinned below. It is fetched
    once into the runner's tool cache, and must be signed by Microsoft. ARTIFACT_SIGNING_DLIB names another.

.EXAMPLE
    ./packaging/Sign-ArtifactSigning.ps1 .\stage\GpibMcp.exe .\stage\GpibMcp.Core.dll
#>
param([Parameter(Mandatory, ValueFromRemainingArguments)][string[]] $Files)

$ErrorActionPreference = 'Stop'
$dlibVersion = '1.0.151'

foreach ($name in 'ARTIFACT_SIGNING_ENDPOINT', 'ARTIFACT_SIGNING_ACCOUNT', 'ARTIFACT_SIGNING_PROFILE') {
    if (-not [Environment]::GetEnvironmentVariable($name)) { throw "$name is not set." }
}

# signtool: the newest x64 one in the Windows SDK. The dlib needs 10.0.2261.755 or later, and not 20348's.
$signTool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.*\x64\signtool.exe" |
    Where-Object { $_.Directory.Parent.Name -notlike '10.0.20348.*' } |
    Sort-Object { [version] $_.Directory.Parent.Name } | Select-Object -Last 1
if (-not $signTool) { throw 'signtool.exe was not found; install the Windows SDK.' }

# The dlib, x64 to match signtool.
$dlib = $env:ARTIFACT_SIGNING_DLIB
if (-not $dlib) {
    $cache = Join-Path ($env:RUNNER_TOOL_CACHE ?? (Join-Path $env:LOCALAPPDATA 'GpibMcp\tools')) "artifact-signing-client\$dlibVersion"
    $dlib = Join-Path $cache 'bin\x64\Azure.CodeSigning.Dlib.dll'
    if (-not (Test-Path $dlib)) {
        $package = Join-Path ([IO.Path]::GetTempPath()) "microsoft.artifactsigning.client.$dlibVersion.zip"
        Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/microsoft.artifactsigning.client/$dlibVersion/microsoft.artifactsigning.client.$dlibVersion.nupkg" -OutFile $package -UseBasicParsing
        Expand-Archive $package $cache -Force
        Remove-Item $package
    }
}
$dlibSignature = Get-AuthenticodeSignature $dlib
if ($dlibSignature.Status -ne 'Valid' -or $dlibSignature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
    throw "$dlib is not validly signed by Microsoft ($($dlibSignature.Status))."
}

# Only the Azure CLI's sign-in is used, so the signature is always made as the identity azure/login set up.
$metadata = Join-Path ([IO.Path]::GetTempPath()) "artifact-signing-$PID.json"
[ordered]@{
    Endpoint               = $env:ARTIFACT_SIGNING_ENDPOINT
    CodeSigningAccountName = $env:ARTIFACT_SIGNING_ACCOUNT
    CertificateProfileName = $env:ARTIFACT_SIGNING_PROFILE
    ExcludeCredentials     = @('EnvironmentCredential', 'ManagedIdentityCredential', 'WorkloadIdentityCredential',
                               'SharedTokenCacheCredential', 'VisualStudioCredential', 'VisualStudioCodeCredential',
                               'AzurePowerShellCredential', 'AzureDeveloperCliCredential', 'InteractiveBrowserCredential')
} | ConvertTo-Json | Set-Content $metadata -Encoding utf8
try {
    & $signTool.FullName sign /fd SHA256 /tr 'http://timestamp.acs.microsoft.com' /td SHA256 /dlib $dlib /dmdf $metadata @Files
    if ($LASTEXITCODE -ne 0) { throw "signtool sign failed (exit $LASTEXITCODE)." }
}
finally {
    Remove-Item $metadata -ErrorAction SilentlyContinue
}

$unsigned = $Files | Where-Object { (Get-AuthenticodeSignature $_).Status -ne 'Valid' }
if ($unsigned) { throw "Not validly signed: $($unsigned -join ', ')" }
