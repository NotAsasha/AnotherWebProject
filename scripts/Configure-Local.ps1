$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot

$taskEnvPath = Join-Path $taskRoot '.env'
if (-not (Test-Path -LiteralPath $taskEnvPath)) { throw 'Create .env from .env.example first.' }
if (-not (Select-String -LiteralPath $taskEnvPath -Pattern '^JWT_SIGNING_KEY=' -Quiet)) {
    $taskKeyBytes = New-Object byte[] 64
    $taskRng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $taskRng.GetBytes($taskKeyBytes) } finally { $taskRng.Dispose() }
    Add-Content -LiteralPath $taskEnvPath -Value ("`nJWT_SIGNING_KEY=" + [Convert]::ToBase64String($taskKeyBytes))
}

# Let Compose resolve .env quoting/interpolation; never print the resolved secrets.
Push-Location -LiteralPath $taskRoot
try {
    $taskJson = & docker compose --profile app config --format json
    if ($LASTEXITCODE -ne 0) { throw 'Could not read Compose configuration.' }
    $taskCompose = ($taskJson -join "`n") | ConvertFrom-Json
} finally {
    Pop-Location
}

function Get-LocalPort($service, [int]$target) {
    $taskPort = @($service.ports | Where-Object { $_.target -eq $target })
    if ($taskPort.Count -ne 1) { throw "Expected one published port for $target." }
    return $taskPort[0].published
}

$taskPgPort = Get-LocalPort $taskCompose.services.postgres 5432
$taskRedisPort = Get-LocalPort $taskCompose.services.redis 6379
$taskRabbitPort = Get-LocalPort $taskCompose.services.rabbitmq 5672
$taskPgPassword = $taskCompose.services.postgres.environment.POSTGRES_PASSWORD.Replace('"', '""')
$taskConnection = "Host=localhost;Port=$taskPgPort;Database=contaminantmigration;Username=contaminant;Password=`"$taskPgPassword`""

foreach ($taskProject in @('ContaminantMigration.WebApi', 'ContaminantMigration.Worker')) {
    $taskPath = Join-Path $taskRoot "src/$taskProject/appsettings.Local.json"
    $taskConfig = if (Test-Path -LiteralPath $taskPath) {
        Get-Content -LiteralPath $taskPath -Raw | ConvertFrom-Json
    } else { [pscustomobject]@{} }

    if (-not $taskConfig.PSObject.Properties['ConnectionStrings']) {
        $taskConfig | Add-Member -NotePropertyName ConnectionStrings -NotePropertyValue ([pscustomobject]@{})
    }
    $taskConfig.ConnectionStrings | Add-Member -NotePropertyName Postgres -NotePropertyValue $taskConnection -Force
    $taskConfig.ConnectionStrings | Add-Member -NotePropertyName Redis -NotePropertyValue "localhost:$taskRedisPort" -Force
    $taskConfig | Add-Member -NotePropertyName RabbitMq -NotePropertyValue ([pscustomobject]@{
        Host = 'localhost'
        Port = [int]$taskRabbitPort
        VirtualHost = '/'
        Username = $taskCompose.services.rabbitmq.environment.RABBITMQ_DEFAULT_USER
        Password = $taskCompose.services.rabbitmq.environment.RABBITMQ_DEFAULT_PASS
    }) -Force
    if ($taskProject -eq 'ContaminantMigration.WebApi') {
        $taskConfig | Add-Member -NotePropertyName Jwt -NotePropertyValue ([pscustomobject]@{
            SigningKey = $taskCompose.services.api.environment.Jwt__SigningKey
        }) -Force
    }
    $taskConfig | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $taskPath -Encoding UTF8
}

Write-Output 'Local API and Worker configuration updated. Secret files are excluded from Git.'
