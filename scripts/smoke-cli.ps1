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
$err = $p.StandardError.ReadToEndAsync()
$lines = [Collections.Generic.List[string]]::new()
$script:pending = $p.StandardOutput.ReadLineAsync()
$script:eof = $false

function Drain {
    while (-not $script:eof -and $script:pending.IsCompleted) {
        $line = $script:pending.Result
        if ($null -eq $line) { $script:eof = $true; break }
        if ($line.Trim()) { $lines.Add($line) }
        $script:pending = $p.StandardOutput.ReadLineAsync()
    }
}

function WaitFrames([int]$count) {
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    Drain
    while ($lines.Count -lt $count -and -not $script:eof -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100; Drain }
    if ($lines.Count -lt $count) { $p.Kill(); throw "'mcp' answered $($lines.Count) frames, expected $count. stderr: $($err.Result)" }
}

$p.StandardInput.WriteLine('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"smoke","version":"1"}}}')
WaitFrames 1
$p.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
$p.StandardInput.WriteLine('{"jsonrpc":"2.0","id":2,"method":"tools/list"}')
$p.StandardInput.WriteLine('{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"bridges","arguments":{}}}')
WaitFrames 3
$p.StandardInput.Close()
if (-not $p.WaitForExit(15000)) { $p.Kill(); throw "'mcp' did not exit after its stdin closed." }
$p.WaitForExit()
Drain
if ($p.ExitCode -ne 0) { throw "'mcp' exited with $($p.ExitCode)." }

$frames = @($lines | ForEach-Object {
        try { $_ | ConvertFrom-Json } catch { throw "Non JSON line on stdout: $_" }
    })
foreach ($frame in $frames) { if ($frame.jsonrpc -ne '2.0') { throw "Non JSON-RPC frame on stdout: $($frame | ConvertTo-Json -Compress)" } }

$list = $frames | Where-Object { $_.id -eq 2 }
$tools = @($list.result.tools | ForEach-Object { $_.name } | Sort-Object)
if (($tools -join ',') -ne 'bridges,docs,replace,search') { throw "Unexpected tools: $($tools -join ',')" }

$search = $list.result.tools | Where-Object { $_.name -eq 'search' }
if ($search.annotations.readOnlyHint -eq $true) { throw "'search' must not be annotated read-only: its predicate is arbitrary C#." }

$call = $frames | Where-Object { $_.id -eq 3 }
if ($call.error -or $call.result.isError) { throw "'bridges' failed: $($call | ConvertTo-Json -Compress -Depth 6)" }

Write-Host "CLI smoke test passed ($($frames.Count) frames, tools: $($tools -join ', '))."
