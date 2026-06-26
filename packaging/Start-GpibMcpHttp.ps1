<#
.SYNOPSIS
    Run the GPIB MCP server in Streamable HTTP mode for cloud clients (Microsoft Copilot #88, ChatGPT #92).

.DESCRIPTION
    Cloud assistants connect to a URL instead of launching a local process, so the server must run in HTTP
    mode and be reachable. This sets GPIB_MCP_TRANSPORT=http, binds loopback, generates a bearer token (unless
    you pass one), starts the server, and prints the local URL + token + the next steps (tunnel, then register
    the connector). Stop with Ctrl+C.

    The server still binds to 127.0.0.1; expose it to a cloud client by tunnelling that port (Microsoft dev
    tunnels, ngrok, Cloudflare, …) and registering the PUBLIC https URL. Keep the token (the tunnel is public).

.EXAMPLE
    ./Start-GpibMcpHttp.ps1
        Start on http://127.0.0.1:3001/mcp with a generated bearer token; prints tunnel/registration steps.

.EXAMPLE
    ./Start-GpibMcpHttp.ps1 -Port 8080 -Token my-secret
        Use a fixed port and token.
#>
[CmdletBinding()]
param(
    [string]$InstallDir = "$env:LOCALAPPDATA\Programs\GpibMcp",
    [int]$Port = 3001,
    [string]$Token,
    [string]$BindHost = "127.0.0.1"
)

$ErrorActionPreference = "Stop"
$exe = Join-Path $InstallDir "GpibMcp.exe"
if (-not (Test-Path $exe)) {
    throw "GpibMcp.exe not found at $exe. Install it first (Install-GpibMcp.ps1), or pass -InstallDir."
}
if ([string]::IsNullOrWhiteSpace($Token)) { $Token = [guid]::NewGuid().ToString("N") }

$env:GPIB_MCP_TRANSPORT = "http"
$env:GPIB_MCP_HTTP_HOST = $BindHost
$env:GPIB_MCP_HTTP_PORT = "$Port"
$env:GPIB_MCP_HTTP_TOKEN = $Token

$url = "http://${BindHost}:${Port}/mcp"
Write-Host ""
Write-Host "GPIB MCP (Streamable HTTP) -> $url" -ForegroundColor Cyan
Write-Host "Bearer token: $Token" -ForegroundColor Yellow
Write-Host ""
Write-Host "Next:" -ForegroundColor Cyan
Write-Host "  1. Tunnel this port to a public HTTPS URL, e.g.:"
Write-Host "       devtunnel host -p $Port --allow-anonymous        (Microsoft dev tunnels)"
Write-Host "       ngrok http $Port                                  (ngrok)"
Write-Host "  2. Register <public-url>/mcp + the bearer token in your client:"
Write-Host "       Copilot Studio: Tools -> Add tool -> MCP -> URL   (see packaging/copilot)"
Write-Host "       ChatGPT:        Developer mode -> create connector (see packaging/chatgpt)"
Write-Host ""
Write-Host "Serving... (Ctrl+C to stop)" -ForegroundColor DarkGray
& $exe
