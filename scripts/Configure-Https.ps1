param([switch]$Trust)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskCertDirectory = Join-Path $taskRoot '.local/https'
New-Item -ItemType Directory -Force -Path $taskCertDirectory | Out-Null
$taskCertificate = Join-Path $taskCertDirectory 'localhost.crt'
& dotnet dev-certs https --export-path $taskCertificate --format Pem --no-password
if ($LASTEXITCODE) { throw 'Could not export the localhost development certificate.' }
if (-not (Test-Path -LiteralPath (Join-Path $taskCertDirectory 'localhost.key'))) {
    throw 'The exported certificate private key was not found.'
}
if ($Trust) {
    & dotnet dev-certs https --trust
    if ($LASTEXITCODE) { throw 'Could not trust the localhost development certificate.' }
}
Write-Host 'Local certificate exported. HTTPS URL: https://localhost:5443'
