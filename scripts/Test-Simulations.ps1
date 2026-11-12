param([string]$BaseUrl = 'http://localhost:5297')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$taskClient = [System.Net.Http.HttpClient]::new()
$taskClient.Timeout = [TimeSpan]::FromSeconds(20)
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskUsers = @()

function Call-Api([string]$method, [string]$path, $body = $null, [string]$token = '', [string]$key = '') {
    $message = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($method), "$BaseUrl/api/v1/$path")
    if ($body) { $message.Content = [System.Net.Http.StringContent]::new(($body | ConvertTo-Json -Depth 10), [Text.Encoding]::UTF8, 'application/json') }
    if ($token) { $message.Headers.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $token) }
    if ($key) { $message.Headers.Add('Idempotency-Key', $key) }
    $response = $taskClient.SendAsync($message).GetAwaiter().GetResult()
    $bytes = $response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
    $json = if ($response.Content.Headers.ContentType.MediaType -match 'json' -and $bytes.Length) { [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json }
    return @{Status = [int]$response.StatusCode; Json = $json; Bytes = $bytes}
}
function Expect($response, [int]$status) {
    if ($response.Status -ne $status) { throw "Expected HTTP $status, received $($response.Status)." }
    return $response.Json
}
function Terminal([string]$id, [string]$token) {
    for ($i = 0; $i -lt 300; $i++) {
        $value = Expect (Call-Api GET "simulations/$id" $null $token) 200
        if ($value.status -notin @('Queued','Running','Cancelling')) { return $value }
        Start-Sleep -Milliseconds 100
    }
    throw "Task $id did not finish within 30 seconds."
}

Push-Location -LiteralPath $taskRoot
try {
    foreach ($n in 1..2) {
        $email = "simulation-check-$([Guid]::NewGuid().ToString('N'))@example.com"
        $credentials = @{email=$email; password='Integration-simulation-123'}
        $user = Expect (Call-Api POST auth/register $credentials) 201
        $taskUsers += [Guid]$user.id
        $login = Expect (Call-Api POST auth/login $credentials) 200
        if ($n -eq 1) { $tokenA = $login.accessToken } else { $tokenB = $login.accessToken }
    }
    $request = @{name='Integration simulation'; domain=@{lengthX=100;lengthY=100;nodesX=101;nodesY=101};
        transport=@{velocityX=0.5;velocityY=0.1;diffusion=0.1};time=@{durationSeconds=60};
        initialCondition=@{peak=1;centerX=25;centerY=50;sigma=5};requestedFrames=16;boundaryCondition='ZeroDirichlet'}
    $plan = Expect (Call-Api POST simulations/validate $request $tokenA) 200
    if ($plan.totalSteps -ne 67) { throw 'Unexpected numerical plan.' }
    docker compose --profile app stop worker
    if ($LASTEXITCODE) { throw 'Could not pause Worker.' }
    $key = [Guid]::NewGuid().ToString()
    $first = Expect (Call-Api POST simulations $request $tokenA $key) 202
    $repeat = Expect (Call-Api POST simulations $request $tokenA $key) 202
    if ($repeat.id -ne $first.id) { throw 'Idempotency created a duplicate.' }
    $request.name = 'Different request'
    Expect (Call-Api POST simulations $request $tokenA $key) 409 | Out-Null
    $pending = @()
    foreach ($n in 1..3) {
        $message = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, "$BaseUrl/api/v1/simulations")
        $message.Content = [System.Net.Http.StringContent]::new(($request | ConvertTo-Json -Depth 10), [Text.Encoding]::UTF8, 'application/json')
        $message.Headers.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $tokenA)
        $pending += $taskClient.SendAsync($message)
    }
    $created = @(); $codes = @()
    foreach ($operation in $pending) {
        $response = $operation.GetAwaiter().GetResult()
        $codes += [int]$response.StatusCode
        if ([int]$response.StatusCode -eq 202) { $created += $response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json }
    }
    if (($codes | Sort-Object) -join ',' -ne '202,202,429') { throw 'Concurrent admission bypassed the quota.' }
    $second = $created[0]; $third = $created[1]
    Expect (Call-Api GET "simulations/$($first.id)" $null $tokenB) 404 | Out-Null
    Expect (Call-Api POST "simulations/$($first.id)/cancel" $null $tokenB) 404 | Out-Null
    Expect (Call-Api GET "simulations/$($first.id)/frames/0" $null $tokenB) 404 | Out-Null
    Expect (Call-Api DELETE "simulations/$($second.id)" $null $tokenA) 409 | Out-Null
    $cancelled = Expect (Call-Api POST "simulations/$($first.id)/cancel" $null $tokenA) 200
    if ($cancelled.status -ne 'Cancelled') { throw 'Queued cancellation failed.' }
    Expect (Call-Api GET "simulations/$($first.id)/result" $null $tokenA) 409 | Out-Null
    Expect (Call-Api DELETE "simulations/$($first.id)" $null $tokenA) 204 | Out-Null
    Expect (Call-Api POST "simulations/$($third.id)/cancel" $null $tokenA) 200 | Out-Null
    $replacement = Expect (Call-Api POST simulations $request $tokenA) 202
    $exhausted = Expect (Call-Api POST simulations $request $tokenA) 202
    # Simulate abandoned attempts, without killing unrelated processes or containers.
    @"
UPDATE "SimulationTasks" SET "Status"='Running', "Attempt"=1, "WorkerId"='abandoned-test', "LeaseUntil"=now()-interval '1 second' WHERE "Id"='$($second.id)';
UPDATE "SimulationTasks" SET "Status"='Running', "Attempt"=2, "WorkerId"='abandoned-test', "LeaseUntil"=now()-interval '1 second' WHERE "Id"='$($exhausted.id)';
"@ | docker compose exec -T postgres psql -U contaminant -d contaminantmigration -v ON_ERROR_STOP=1
    if ($LASTEXITCODE) { throw 'Could not prepare recovery cases.' }
    docker compose --profile app up -d --wait --wait-timeout 120 worker
    if ($LASTEXITCODE) { throw 'Worker did not become healthy.' }
    $completed = Terminal $replacement.id $tokenA
    if ($completed.status -ne 'Completed' -or $completed.progressPercent -ne 100) { throw 'Simulation failed.' }
    $recovered = Terminal $second.id $tokenA
    if ($recovered.status -ne 'Completed' -or $recovered.attempt -ne 2) { throw 'Lease recovery failed.' }
    $failed = Terminal $exhausted.id $tokenA
    if ($failed.status -ne 'Failed' -or $failed.errorCode -ne 'WorkerLeaseExpired') { throw 'Retry limit failed.' }
    $result = Expect (Call-Api GET "simulations/$($replacement.id)/result" $null $tokenA) 200
    if ($result.frames.Count -ne 16) { throw 'Missing frames.' }
    $frame = Call-Api GET "simulations/$($replacement.id)/frames/0" $null $tokenA
    if ($frame.Status -ne 200 -or $frame.Bytes.Length -ne 40804) { throw 'Invalid frame payload.' }
    $request.domain.nodesX = 512; $request.domain.nodesY = 512
    $request.time.durationSeconds = 49.9; $request.requestedFrames = 2
    $active = Expect (Call-Api POST simulations $request $tokenA) 202
    $running = $false
    for ($i = 0; $i -lt 200; $i++) {
        $state = Expect (Call-Api GET "simulations/$($active.id)" $null $tokenA) 200
        if ($state.status -eq 'Running') { $running = $true; break }
        if ($state.status -eq 'Completed') { break }
        Start-Sleep -Milliseconds 10
    }
    if (-not $running) { throw 'Could not observe a running task for the cancellation check.' }
    Expect (Call-Api POST "simulations/$($active.id)/cancel" $null $tokenA) 202 | Out-Null
    $cancelled = Terminal $active.id $tokenA
    if ($cancelled.status -ne 'Cancelled') { throw 'Running cancellation failed.' }
    Expect (Call-Api GET "simulations/$($active.id)/result" $null $tokenA) 409 | Out-Null
    $originalUrl = $BaseUrl
    try {
        docker compose stop rabbitmq
        if ($LASTEXITCODE) { throw 'Could not pause RabbitMQ.' }
        # Bypass YARP readiness filtering to test durable admission while the broker is down.
        $BaseUrl = 'http://localhost:5179'
        $request.domain.nodesX = 101; $request.domain.nodesY = 101
        $request.time.durationSeconds = 60; $request.requestedFrames = 16
        $offline = Expect (Call-Api POST simulations $request $tokenA) 202
    } finally {
        docker compose up -d --wait --wait-timeout 120 rabbitmq
        $BaseUrl = $originalUrl
    }
    # Wait for the API bus and YARP health probes to reconnect.
    for ($i = 0; $i -lt 30; $i++) {
        try { Invoke-WebRequest -Uri "$BaseUrl/health/ready" -UseBasicParsing -TimeoutSec 5 | Out-Null; break }
        catch { Start-Sleep -Seconds 1 }
    }
    $delivered = Terminal $offline.id $tokenA
    if ($delivered.status -ne 'Completed') { throw 'Outbox did not deliver after broker recovery.' }
    Write-Output 'Passed: validation, idempotency, concurrent quotas, owner isolation, queued/running cancellation, lease recovery, retry limit, frames and broker outage recovery.'
} finally {
    docker compose --profile app up -d --wait --wait-timeout 120 worker | Out-Null
    foreach ($id in $taskUsers) {
        @"
DELETE FROM "SimulationTasks" WHERE "OwnerId"='$id';
DELETE FROM "SubmissionKeys" WHERE "OwnerId"='$id';
DELETE FROM "Users" WHERE "Id"='$id';
"@ | docker compose exec -T postgres psql -U contaminant -d contaminantmigration -v ON_ERROR_STOP=1
    }
    Pop-Location
    $taskClient.Dispose()
}
