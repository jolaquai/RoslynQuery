param([Parameter(Mandatory)][string]$Exe)
$ErrorActionPreference = 'Stop'

$docs = (& $Exe docs) -join "`n"
if ($LASTEXITCODE -ne 0 -or $docs -notmatch 'Live Visual Studio') { throw "'docs' failed or is incomplete." }

# stdout of the MCP server must carry JSON-RPC frames and nothing else.
$psi = [Diagnostics.ProcessStartInfo]::new($Exe, 'mcp')
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.UseShellExecute = $false
$p = [Diagnostics.Process]::Start($psi)
$out = $p.StandardOutput.ReadToEndAsync()
$err = $p.StandardError.ReadToEndAsync()
$p.StandardInput.WriteLine('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"smoke","version":"1"}}}')
$p.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
$p.StandardInput.WriteLine('{"jsonrpc":"2.0","id":2,"method":"tools/list"}')
$p.StandardInput.WriteLine('{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"bridges","arguments":{}}}')
Start-Sleep -Seconds 3
$p.StandardInput.Close()
if (-not $p.WaitForExit(15000)) { $p.Kill(); throw "'mcp' did not exit after its stdin closed." }

$lines = @($out.Result -split "`r?`n" | Where-Object { $_.Trim() })
if ($lines.Count -lt 3) { throw "'mcp' answered $($lines.Count) frames, expected 3. stderr: $($err.Result)" }
foreach ($line in $lines) {
    $frame = $line | ConvertFrom-Json
    if ($frame.jsonrpc -ne '2.0') { throw "Non JSON-RPC line on stdout: $line" }
}

$tools = ($lines | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object { $_.id -eq 2 }).result.tools.name | Sort-Object
if (($tools -join ',') -ne 'bridges,docs,replace,search') { throw "Unexpected tools: $($tools -join ',')" }

Write-Host "CLI smoke test passed ($($lines.Count) frames, tools: $($tools -join ', '))."
