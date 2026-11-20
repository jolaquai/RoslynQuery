# Publishes the roslynquery CLI self-contained for both Windows architectures into RoslynQuery.Cli/publish/<rid>,
# which RoslynQuery.csproj then bundles into the VSIX. Same thing release.yml does. Needs a .NET 11 SDK.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
foreach ($rid in 'win-x64', 'win-arm64') {
    dotnet publish "$root/RoslynQuery.Cli/RoslynQuery.Cli.csproj" -c Release -r $rid --self-contained -p:PublishReadyToRun=true -o "$root/RoslynQuery.Cli/publish/$rid"
    if ($LASTEXITCODE -ne 0) { throw "CLI publish failed for $rid" }
}
