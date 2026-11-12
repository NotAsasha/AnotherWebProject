param(
    [string]$BaseUrl = 'http://localhost:5297',
    [ValidateRange(1, 3)][int]$TaskCount = 3,
    [string]$ReportPath = ''
)
# Local, single-Worker scenario. Temporarily stops/recreates Worker; restores it in finally.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$client = [System.Net.Http.HttpClient]::new()
$client.Timeout = [TimeSpan]::FromSeconds(20)
$root = Split-Path -Parent $PSScriptRoot
$userId = $null
$token = ''
$workerChanged = $false
$override = [IO.Path]::GetTempFileName()
$samples = [Collections.Generic.List[object]]::new()
$observations = [Collections.Generic.List[object]]::new()
$report = [ordered]@{ startedAtUtc = [DateTimeOffset]::UtcNow.ToString('o'); scenarios = @(); tasks = @(); samples = @() }

function Docker([string[]]$Arguments) {
    $output = & docker.exe @Arguments
    if ($LASTEXITCODE) { throw "Docker command failed: $($Arguments -join ' ')" }
    return $output
}
function Sql([string]$Query) {
    $output = $Query | & docker.exe compose exec -T postgres psql -U contaminant -d contaminantmigration -v ON_ERROR_STOP=1 -At
    if ($LASTEXITCODE) { throw 'Test database operation failed.' }
    return $output
}
function Api([string]$Method, [string]$Path, $Body = $null, [int[]]$Expected = @(200)) {
    $message = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::new($Method), "$BaseUrl/api/v1/$Path")
    if ($token) { $message.Headers.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $token) }
    if ($null -ne $Body) { $message.Content = [Net.Http.StringContent]::new(($Body | ConvertTo-Json -Depth 10), [Text.Encoding]::UTF8, 'application/json') }
    $response = $null
    try {
        $response = $client.SendAsync($message).GetAwaiter().GetResult()
        $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if ([int]$response.StatusCode -notin $Expected) { throw "HTTP $([int]$response.StatusCode) for $Method $Path : $text" }
        if ($text) { return $text | ConvertFrom-Json }
    } finally {
        if ($response) { $response.Dispose() }
        $message.Dispose()
    }
}
function HeavyRequest([string]$Name) {
    return @{ name=$Name; domain=@{lengthX=100;lengthY=100;nodesX=512;nodesY=512};
        transport=@{velocityX=0.5;velocityY=0.1;diffusion=0.1};
        time=@{durationSeconds=60;timeStepSeconds=0.01};
        initialCondition=@{peak=1;centerX=25;centerY=50;sigma=5};
        requestedFrames=2;boundaryCondition='ZeroDirichlet' }
}
function State([string]$Id) {
    $value = Api GET "simulations/$Id"
    $observations.Add([pscustomobject]@{ atUtc=[DateTimeOffset]::UtcNow.ToString('o'); id=$Id;
        status=$value.status; progress=$value.progressPercent; attempt=$value.attempt })
    return $value
}
function Wait-State([string]$Id, [string[]]$Statuses, [int]$Seconds = 120) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt $Seconds) {
        $value = State $Id
        if ($value.status -in $Statuses) { return $value }
        if ($value.status -notin @('Queued','Running','Cancelling')) { throw "Unexpected terminal state: $($value.status), $($value.errorCode)" }
        Start-Sleep -Milliseconds 100
    }
    throw "Task $Id did not reach $($Statuses -join '/') in $Seconds seconds."
}
function Sample-Resources {
    foreach ($line in (Docker @('stats','--no-stream','--format','{{json .}}',$script:workerContainer))) {
        $stats = $line | ConvertFrom-Json
        $samples.Add([pscustomobject]@{ atUtc=[DateTimeOffset]::UtcNow.ToString('o'); cpu=$stats.CPUPerc; memory=$stats.MemUsage })
    }
}

Push-Location -LiteralPath $root
try {
    $workerContainer = (Docker @('compose','ps','-q','worker') | Select-Object -First 1)
    if (-not $workerContainer) { throw 'Start the full application with exactly one healthy Worker first.' }
    if (@(Docker @('compose','ps','-q','worker')).Count -ne 1) { throw 'This scenario requires exactly one Worker.' }
    if ((Sql 'SELECT count(*) FROM "SimulationTasks" WHERE "Status" IN (''Queued'',''Running'',''Cancelling'');') -ne '0') {
        throw 'Finish or cancel existing tasks before running this isolated load check.'
    }
    $credentials = @{email="load-$([Guid]::NewGuid().ToString('N'))@example.com";password='Load-check-password-123'}
    $userId = [Guid](Api POST auth/register $credentials 201).id
    $token = (Api POST auth/login $credentials).accessToken
    $request = HeavyRequest 'Load batch'
    $plan = Api POST simulations/validate $request
    $limits = Api GET simulation-limits
    $report.plan = $plan
    $report.limits = $limits.numerical
    $report.workerImage = Docker @('inspect','--format','{{.Image}}',$workerContainer)
    $report.dockerResources = Docker @('info','--format','CPUs={{.NCPU}} MemoryBytes={{.MemTotal}}')
    $workerChanged = $true
    Docker @('compose','stop','worker') | Out-Null
    $batch = @()
    foreach ($i in 1..$TaskCount) {
        $request.name = "Load batch $i"
        $task = Api POST simulations $request 202
        $batch += $task.id
        if ((State $task.id).status -ne 'Queued') { throw 'Task was not queued while Worker was stopped.' }
    }
    Docker @('compose','start','worker') | Out-Null
    $batchStartedAt = [DateTimeOffset]::UtcNow
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $remaining = @($batch)
    while ($remaining.Count) {
        if ($watch.Elapsed.TotalSeconds -gt (90 * $TaskCount)) { throw 'Batch deadline exceeded.' }
        foreach ($id in @($remaining)) {
            $state = State $id
            if ($state.status -eq 'Completed') {
                $report.tasks += [pscustomobject]@{ id=$id; queueSeconds=([DateTimeOffset]$state.startedAt - [DateTimeOffset]$state.createdAt).TotalSeconds;
                    executionSeconds=([DateTimeOffset]$state.finishedAt - [DateTimeOffset]$state.startedAt).TotalSeconds; attempt=$state.attempt }
                $metadata = Api GET "simulations/$id/result"
                if ($metadata.frames.Count -ne 2) { throw 'Completed task is missing frames.' }
                $remaining = @($remaining | Where-Object { $_ -ne $id })
            } elseif ($state.status -notin @('Queued','Running')) { throw "Batch task ended with $($state.status)." }
        }
        if ($remaining.Count) { Sample-Resources; Start-Sleep -Milliseconds 100 }
    }
    if (-not ($observations | Where-Object { $_.id -in $batch -and $_.progress -gt 0 -and $_.progress -lt 100 })) {
        throw 'No intermediate progress observed; increase the workload within configured limits.'
    }
    $report.batchSeconds = ([DateTimeOffset]::UtcNow - $batchStartedAt).TotalSeconds
    $previousFinish = $null
    foreach ($id in $batch) {
        $state = State $id
        if ($state.attempt -ne 1) { throw 'Unexpected retry during baseline batch.' }
        if ($previousFinish -and [DateTimeOffset]$state.startedAt -lt $previousFinish) { throw 'Single Worker executed overlapping tasks.' }
        $previousFinish = [DateTimeOffset]$state.finishedAt
    }
    $report.scenarios += 'Queued batch completed sequentially with intermediate progress and readable results'

    $task = Api POST simulations (HeavyRequest 'Load cancellation') 202
    Wait-State $task.id @('Running') | Out-Null
    $cancelWatch = [Diagnostics.Stopwatch]::StartNew()
    Api POST "simulations/$($task.id)/cancel" $null @(200,202) | Out-Null
    Wait-State $task.id @('Cancelled') | Out-Null
    $report.cancellationSeconds = $cancelWatch.Elapsed.TotalSeconds
    Api GET "simulations/$($task.id)/result" $null 409 | Out-Null
    $report.scenarios += 'Running task cancelled without a published result'

    $task = Api POST simulations (HeavyRequest 'Load crash recovery') 202
    Wait-State $task.id @('Running') | Out-Null
    $recoveryWatch = [Diagnostics.Stopwatch]::StartNew()
    Docker @('compose','kill','-s','SIGKILL','worker') | Out-Null
    Docker @('compose','up','-d','--no-deps','--wait','--wait-timeout','120','worker') | Out-Null
    $recovered = Wait-State $task.id @('Completed')
    if ($recovered.attempt -ne 2) { throw "Expected a second attempt after Worker crash; got $($recovered.attempt)." }
    $report.recoverySeconds = $recoveryWatch.Elapsed.TotalSeconds
    $report.scenarios += 'Worker killed during computation; expired lease recovered on attempt 2'

    # A short deadline tests the real Worker timeout path without waiting 60 seconds.
    [IO.File]::WriteAllText($override, "services:`n  worker:`n    environment:`n      SimulationLimits__MaxExecutionTime: '00:00:00.001'`n")
    Docker @('compose','-f','docker-compose.yml','-f',$override,'up','-d','--no-deps','--wait','--wait-timeout','120','worker') | Out-Null
    $task = Api POST simulations (HeavyRequest 'Load timeout') 202
    $timedOut = Wait-State $task.id @('TimedOut')
    if ($timedOut.errorCode -ne 'ExecutionTimeout') { throw 'Missing timeout error code.' }
    Api GET "simulations/$($task.id)/result" $null 409 | Out-Null
    $report.scenarios += 'Execution deadline produces TimedOut without a result'
    $report.samples = @($samples.ToArray())
    $report.observations = @($observations.ToArray())
    $report.finishedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    $report.tasks | Format-Table | Out-Host
    $samples | Format-Table | Out-Host
    Write-Host "Passed: $($report.scenarios -join '; ')."
    Write-Host "Cancellation: $([Math]::Round($report.cancellationSeconds, 3))s; crash recovery: $([Math]::Round($report.recoverySeconds, 3))s."
    Write-Host "Resource samples: $($samples.Count) (sampled container CPU/memory, not an exact process peak)."
    if ($ReportPath) { $report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $ReportPath -Encoding UTF8 }
} finally {
    try {
        if ($userId -and $token) {
            $owned = Api GET 'simulations?page=1&pageSize=50'
            foreach ($task in $owned.items) {
                if ($task.status -in @('Queued','Running','Cancelling')) {
                    Api POST "simulations/$($task.id)/cancel" $null @(200,202) | Out-Null
                }
            }
        }
    } finally {
        try {
            if ($workerChanged) {
                Docker @('compose','up','-d','--no-deps','--force-recreate','--wait','--wait-timeout','120','worker') | Out-Null
            }
            if ($userId) {
                foreach ($task in (Api GET 'simulations?page=1&pageSize=50').items) {
                    Wait-State $task.id @('Completed','Cancelled','TimedOut','Failed') | Out-Null
                }
                $active = Sql "SELECT count(*) FROM `"SimulationTasks`" WHERE `"OwnerId`"='$userId' AND `"Status`" IN ('Queued','Running','Cancelling');"
                if ($active -ne '0') { throw 'Fixture tasks still active; retained records for diagnosis.' }
                Sql "DELETE FROM `"SimulationTasks`" WHERE `"OwnerId`"='$userId'; DELETE FROM `"SubmissionKeys`" WHERE `"OwnerId`"='$userId'; DELETE FROM `"Users`" WHERE `"Id`"='$userId';" | Out-Null
            }
        } finally {
            Remove-Item -LiteralPath $override -Force
            $client.Dispose()
            Pop-Location
        }
    }
}
