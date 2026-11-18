# Runs every Windows UI check against a local server and prints one line per check.
#   powershell -ExecutionPolicy Bypass -File tests\windows-ui\run-all.ps1 [-NoBuild] [-Only voice-check,layout-check]
# Needs Docker (the database and file storage of server\docker-compose.yml), ffprobe for the recording checks,
# and the desktop left alone meanwhile: the checks click, type and use the microphone and camera.
param([switch]$NoBuild, [string[]]$Only)

# Docker and dotnet write progress to stderr, which Stop would treat as a failure; real failures are checked below.
$ErrorActionPreference = 'Continue'
$here = $PSScriptRoot
$repo = (Resolve-Path "$here\..\..").Path
$api = 'http://127.0.0.1:5199'

if (-not $NoBuild) {
    'Building the app and the helper tools...'
    dotnet build "$repo\Mi``nU.csproj" -f net11.0-windows10.0.19041.0 -v q -nologo | Select-String ' error ' | ForEach-Object { $_.Line }
    if ($LASTEXITCODE -ne 0) { throw 'The app did not build.' }
    foreach ($tool in 'sender', 'peer') {
        dotnet build "$here\tools\$tool" -v q -nologo | Select-String ' error ' | ForEach-Object { $_.Line }
        if ($LASTEXITCODE -ne 0) { throw "tools\$tool did not build." }
    }
}

# The database and storage, then the server in Development, unless one already answers.
$compose = "$repo\server\docker-compose.yml"
docker compose -f $compose up -d 2>&1 | Out-Null
# Docker Desktop may still be starting, and Postgres takes a moment after its container: wait until it takes connections.
$dbReady = $false
for ($i = 0; $i -lt 60 -and -not $dbReady; $i++) {
    docker compose -f $compose exec -T postgres pg_isready -U minu 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 0) { $dbReady = $true } else { Start-Sleep 2; docker compose -f $compose up -d 2>&1 | Out-Null }
}
if (-not $dbReady) { throw 'The database did not start. Is Docker Desktop running?' }
$server = $null
try { Invoke-WebRequest -UseBasicParsing "$api/api/ice-servers" -TimeoutSec 2 | Out-Null } catch {
    if (-not $_.Exception.Response) {
        'Starting the local server...'
        $env:ASPNETCORE_ENVIRONMENT = 'Development'
        # The checks sign in far more often than a person: the helper tools do so for each thing they send.
        $env:Auth__AttemptsPerMinute = '1000'
        $server = Start-Process dotnet -ArgumentList 'run', '--project', "$repo\server\MinU.Server", '--no-launch-profile', '--urls', $api `
            -PassThru -WindowStyle Hidden -RedirectStandardOutput "$env:TEMP\minu-ui-server.log" -RedirectStandardError "$env:TEMP\minu-ui-server.err"
        $ready = $false
        for ($i = 0; $i -lt 90 -and -not $ready -and -not $server.HasExited; $i++) {
            Start-Sleep 1
            try { Invoke-WebRequest -UseBasicParsing "$api/api/ice-servers" -TimeoutSec 2 | Out-Null; $ready = $true } catch { $ready = [bool]$_.Exception.Response }
        }
        if (-not $ready) {
            Get-Content "$env:TEMP\minu-ui-server.err" -Tail 15 -ErrorAction SilentlyContinue
            throw "The server did not start; see $env:TEMP\minu-ui-server.log and .err"
        }
    }
}

$checks = Get-ChildItem "$here\*-check.ps1", "$here\photo-hang.ps1" | Sort-Object Name
if ($Only) { $checks = $checks | Where-Object { $Only -contains $_.BaseName } }
$failed = @()
try {
    foreach ($check in $checks) {
        $output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $check.FullName 2>&1
        # Skips are shown too: a check that could not run here is not one that passed.
        $problems = @($output | Where-Object { "$_" -like 'FAIL*' -or "$_" -like 'ERROR*' -or "$_" -like 'SKIP*' })
        $summary = $output | Where-Object { "$_" -like '===*passed*' } | Select-Object -Last 1
        "{0,-22} {1}" -f $check.BaseName, $(if ($summary) { $summary } else { '=== did not finish' })
        $problems | ForEach-Object { "    $_" }
        if ($LASTEXITCODE -ne 0 -or -not $summary) { $failed += $check.BaseName }
        # Each check starts the app afresh; a moment between them lets the last one close.
        Start-Sleep 5
    }
}
finally {
    # dotnet run starts the server as a child process: the whole tree goes.
    if ($server) { taskkill /PID $server.Id /T /F 2>&1 | Out-Null }
}

if ($failed) { "Failed: $($failed -join ', ')"; exit 1 }
'All checks passed.'
