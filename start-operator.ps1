param(
    [string]$configuration = "Release"
)

$scriptPath = $PSScriptRoot

Write-Host "Starting octo-communication-operator (central mode)" -ForegroundColor Green

$publishVersion = "net10.0"
$assemblyName = "Meshmakers.Octo.Communication.Operator.dll"
$binPath = Join-Path $scriptPath "src/CommunicationOperator/bin/$configuration/$publishVersion"
$assemblyPath = Join-Path $binPath $assemblyName

if (!(Test-Path $assemblyPath)) {
    Write-Host "Assembly not found at $assemblyPath" -ForegroundColor Red
    Write-Host "Build the operator first via Invoke-BuildAll -configuration $configuration." -ForegroundColor Yellow
    exit 1
}

# Bind to ports that don't clash with the services started by Start-Octo
# (5000-5021). The webhook endpoints are exposed for parity with the in-cluster
# deployment but are not required for the central-operator E2E smoke test.
$kestrelUrls = "http://*:5022;https://*:5023"

# appsettings.Development.json carries the central-mode defaults
# (AutoManagePools=true, controller URI, broker host, broker creds).
$env:ASPNETCORE_ENVIRONMENT = "Development"

Push-Location $binPath
try {
    Write-Host "Running $assemblyName from $binPath on $kestrelUrls" -ForegroundColor Cyan
    Write-Host "Press Ctrl+C to stop." -ForegroundColor Cyan
    & dotnet $assemblyName --urls=$kestrelUrls
}
finally {
    Pop-Location
}
