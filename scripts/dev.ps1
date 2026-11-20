<#
.SYNOPSIS
    Avvio locale di TriMetric: infrastruttura, servizi .NET e web (Windows PowerShell 5.1 o superiore).

.DESCRIPTION
    Avvia con un solo comando (DEC-009):
      1. l'infrastruttura (PostgreSQL/TimescaleDB e NATS) con "compose -f deploy/compose.yaml up -d";
      2. la compilazione, una volta sola e in sequenza, di ingestion, activities e gateway;
      3. i tre servizi, uno alla volta, con "dotnet run --no-build" in ambiente Development;
      4. il web (Vite) su http://localhost:5173.
    Resta in primo piano: Ctrl+C ferma i servizi e il web (l'infrastruttura resta attiva).
    Funziona da qualsiasi cartella di lavoro. Su Linux, macOS o Git Bash usa scripts/dev.sh
    (stessa interfaccia e stessi messaggi).

    Non espone nulla sulla rete: i servizi ascoltano su 127.0.0.1, il gateway e il web su localhost.
    Il dev server NON si avvia mai con --host: i servizi usano un'identità di sviluppo fissa (DEC-006).

.PARAMETER NoInfra
    Non avvia l'infrastruttura: PostgreSQL e NATS devono essere già attivi.

.PARAMETER NoWeb
    Non avvia il web.

.PARAMETER Stop
    Ferma i servizi e il web avviati dallo script (albero di processi completo).

.PARAMETER StopInfra
    Come -Stop e poi ferma l'infrastruttura con "compose down" (SENZA -v: i volumi e i dati restano).

.PARAMETER Runtime
    Runtime dei contenitori: docker o podman. Predefinito: il primo disponibile e funzionante tra docker
    e podman; si può fissare anche con la variabile TRIMETRIC_CONTAINER_RUNTIME.

.PARAMETER Help
    Mostra questo aiuto.
#>
[CmdletBinding()]
param(
    [switch]$NoInfra,
    [switch]$NoWeb,
    [switch]$Stop,
    [switch]$StopInfra,
    [ValidateSet('', 'docker', 'podman')]
    [string]$Runtime = '',
    [switch]$Help
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

# ----------------------------------------------------------------------------------------------
# Costanti (porte e indirizzi di DEC-009; identità di sviluppo di DEC-006)
# ----------------------------------------------------------------------------------------------
$RepoRoot    = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$ComposeFile = Join-Path $RepoRoot 'deploy\compose.yaml'
$LogDir      = Join-Path $RepoRoot '.dev-logs'
$PidFile     = Join-Path $LogDir 'pids'
$WebDir      = Join-Path $RepoRoot 'src\web'
$DevUserId   = '00000000-0000-4000-8000-000000000001'   # lo stesso di appsettings.Development.json

$Services = @(
    @{ Nome = 'ingestion';  Csproj = 'src\services\ingestion\src\TriMetric.Ingestion\TriMetric.Ingestion.csproj';   Health = 'http://127.0.0.1:5081/health/live' },
    @{ Nome = 'activities'; Csproj = 'src\services\activities\src\TriMetric.Activities\TriMetric.Activities.csproj'; Health = 'http://127.0.0.1:5082/health/live' },
    @{ Nome = 'gateway';    Csproj = 'src\services\gateway\src\TriMetric.Gateway\TriMetric.Gateway.csproj';          Health = 'http://localhost:5080/health/live' }
)
$WebUrl      = 'http://localhost:5173'
$NatsHealth  = 'http://127.0.0.1:8222/healthz'
$AppPorts    = @(5080, 5081, 5082, 5173)
$InfraPorts  = @(4222, 5432, 8222)

# ----------------------------------------------------------------------------------------------
# Messaggi (solo URL, porte e stato: mai valori di file, coordinate o nomi di file)
# ----------------------------------------------------------------------------------------------
function Write-Info($msg) { Write-Host "[dev] $msg" }
function Write-Ok($msg)   { Write-Host "[ok] $msg" -ForegroundColor Green }
function Write-Warn($msg) { Write-Host "[attenzione] $msg" -ForegroundColor Yellow }
function Write-Err($msg)  { Write-Host "[errore] $msg" -ForegroundColor Red }
function Stop-Script($msg) { throw $msg }

function Show-Help {
    @'
Uso: scripts\dev.ps1 [opzioni]

Avvia l'infrastruttura (PostgreSQL e NATS), compila ingestion, activities e gateway,
li avvia in sviluppo e avvia il web su http://localhost:5173. Resta in primo piano:
Ctrl+C ferma i servizi e il web (l'infrastruttura resta attiva).

Opzioni:
  -NoInfra                 non avvia l'infrastruttura (PostgreSQL e NATS già attivi)
  -NoWeb                   non avvia il web
  -Stop                    ferma i servizi e il web avviati dallo script
  -StopInfra               come -Stop e poi ferma i contenitori (compose down, senza -v: i dati restano)
  -Runtime docker|podman   sceglie il runtime dei contenitori (o variabile TRIMETRIC_CONTAINER_RUNTIME)
  -Help                    mostra questo aiuto

Log in .dev-logs\ (un file per servizio). Guida completa: docs\getting-started.md
'@ | Write-Host
}

# ----------------------------------------------------------------------------------------------
# Utilità per processi e porte
# ----------------------------------------------------------------------------------------------
function Invoke-Native([string]$File, [string]$Arguments, [switch]$Capture) {
    # Esegue un programma con la riga di argomenti passata così com'è (nessun problema di virgolette con
    # PowerShell 5.1). Senza -Capture l'output appare in console; con -Capture viene restituito in Out.
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $File
    $psi.Arguments = $Arguments
    $psi.WorkingDirectory = $RepoRoot
    $psi.UseShellExecute = $false
    if ($Capture) {
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.CreateNoWindow = $true
    }
    $p = [System.Diagnostics.Process]::Start($psi)
    $out = ''
    if ($Capture) {
        $out = $p.StandardOutput.ReadToEnd()
        $null = $p.StandardError.ReadToEnd()
    }
    $p.WaitForExit()
    return [pscustomobject]@{ Code = $p.ExitCode; Out = $out }
}

function Test-CommandOk([string]$File, [string]$Arguments) {
    try { return ((Invoke-Native $File $Arguments -Capture).Code -eq 0) } catch { return $false }
}

function Get-Stamp([int]$ProcId) {
    try { return [string](Get-Process -Id $ProcId -ErrorAction Stop).StartTime.ToUniversalTime().Ticks } catch { return $null }
}

function Test-Alive([int]$ProcId, [string]$Stamp) {
    $now = Get-Stamp $ProcId
    if ($null -eq $now) { return $false }
    # Se il PID è stato riusato da un altro processo l'istante di avvio non coincide.
    return ($now -eq $Stamp)
}

function Read-PidFile {
    # Tollerante alla corsa con un -Stop concorrente (file che sparisce mentre si legge): in tal caso, vuoto.
    $items = @()
    try {
        if (-not (Test-Path -LiteralPath $PidFile)) { return $items }
        foreach ($line in Get-Content -LiteralPath $PidFile -ErrorAction Stop) {
            if ([string]::IsNullOrWhiteSpace($line)) { continue }
            if ($line.StartsWith('#')) { continue }
            $p = $line.Split(';')
            if ($p.Count -lt 3) { continue }
            $mode = ''
            if ($p.Count -ge 4) { $mode = $p[3] }
            $items += [pscustomobject]@{ Nome = $p[0]; ProcId = [int]$p[1]; Stamp = $p[2]; Mode = $mode }
        }
    } catch { return @() }
    return $items
}

function Get-PidFileOrigin {
    if (-not (Test-Path -LiteralPath $PidFile)) { return '' }
    $first = Get-Content -LiteralPath $PidFile -TotalCount 1
    if ($first -match '^#\s*avviato da:\s*(\S+)') { return $Matches[1] }
    return ''
}

function Add-PidEntry([string]$Nome, [int]$ProcId, [string]$Mode = '') {
    Add-Content -LiteralPath $PidFile -Value ("{0};{1};{2};{3}" -f $Nome, $ProcId, (Get-Stamp $ProcId), $Mode) -Encoding ASCII
}

function Get-LaunchMode {
    # "file" se questo script gira in un processo dedicato (powershell -File ...), altrimenti "shell":
    # lanciato da una shell interattiva (o con -Command), che -Stop non deve MAI chiudere con la forza.
    try {
        $cl = (Get-CimInstance Win32_Process -Filter "ProcessId=$PID" -ErrorAction Stop).CommandLine
        if ($cl -match '(?i)\s-f(ile)?\s+"?[^\s"]*dev\.ps1') { return 'file' }
    } catch { }
    return 'shell'
}

function Stop-Tree([int]$ProcId, [string]$Stamp) {
    # Termina il processo e tutti i suoi discendenti (dotnet run lascia un processo figlio).
    # Restituisce $true se il processo non esiste più.
    $null = Invoke-Native 'taskkill.exe' "/PID $ProcId /T /F" -Capture
    return (-not (Test-Alive $ProcId $Stamp))
}

function Get-PortListener([int]$Port) {
    if (Get-Command Get-NetTCPConnection -ErrorAction SilentlyContinue) {
        $c = @(Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue)
        if ($c.Count -eq 0) { return $null }
        $owner = [int]$c[0].OwningProcess
        $name = 'processo sconosciuto'
        try { $name = (Get-Process -Id $owner -ErrorAction Stop).ProcessName } catch { }
        return [pscustomobject]@{ Porta = $Port; ProcId = $owner; Nome = $name }
    }
    $l = [System.Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners() |
        Where-Object { $_.Port -eq $Port } | Select-Object -First 1
    if ($null -eq $l) { return $null }
    return [pscustomobject]@{ Porta = $Port; ProcId = 0; Nome = 'processo sconosciuto' }
}

function Test-Tcp([string]$HostName, [int]$Port) {
    # Connessione con attesa breve: su Windows una connessione rifiutata impiega circa 2 s a fallire,
    # qui si rinuncia dopo 400 ms e si riprova al giro successivo (l'avvio si rileva più in fretta).
    # "localhost" si prova sia su IPv6 sia su IPv4, un indirizzo alla volta (il web ascolta solo su ::1).
    $addresses = @($HostName)
    if ($HostName -eq 'localhost') { $addresses = @('::1', '127.0.0.1') }
    foreach ($a in $addresses) {
        $ip = [System.Net.IPAddress]::Parse($a)
        $c = New-Object System.Net.Sockets.TcpClient($ip.AddressFamily)
        try {
            $iar = $c.BeginConnect($ip, $Port, $null, $null)
            if ($iar.AsyncWaitHandle.WaitOne(400)) {
                $c.EndConnect($iar)
                return $true
            }
        } catch { } finally { $c.Close() }
    }
    return $false
}

function Test-Http([string]$Url) {
    $u = [System.Uri]$Url
    if (-not (Test-Tcp $u.Host $u.Port)) { return $false }
    try {
        $req = [System.Net.HttpWebRequest]::Create($Url)
        $req.Proxy = $null          # nessun proxy di sistema per gli indirizzi locali
        $req.Timeout = 3000
        $req.ReadWriteTimeout = 3000
        $resp = $req.GetResponse()
        try { return ([int]$resp.StatusCode -eq 200) } finally { $resp.Close() }
    } catch { return $false }
}

function Wait-Until([scriptblock]$Check, [int]$TimeoutSec, [scriptblock]$Abort = $null) {
    # Restituisce 'ok', 'timeout' oppure 'interrotto' (se Abort diventa vero).
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if ($null -ne $Abort -and (& $Abort)) { return 'interrotto' }
        if (& $Check) { return 'ok' }
        Start-Sleep -Milliseconds 700
    }
    return 'timeout'
}

# ----------------------------------------------------------------------------------------------
# Runtime dei contenitori
# ----------------------------------------------------------------------------------------------
$script:ContainerRuntime = $null

function Resolve-ContainerRuntime {
    $wanted = $Runtime
    if (-not $wanted) { $wanted = $env:TRIMETRIC_CONTAINER_RUNTIME }
    if ($wanted) { $wanted = $wanted.Trim().ToLowerInvariant() }
    if ($wanted -and $wanted -notin @('docker', 'podman')) {
        Stop-Script "Runtime dei contenitori non valido ('$wanted'): usa docker o podman."
    }
    $candidates = if ($wanted) { @($wanted) } else { @('docker', 'podman') }
    $installed = @()
    foreach ($c in $candidates) {
        if (-not (Get-Command $c -CommandType Application -ErrorAction SilentlyContinue)) { continue }
        $installed += $c
        if (-not (Test-CommandOk $c 'info')) { continue }
        if (-not (Test-CommandOk $c 'compose version')) { continue }
        $script:ContainerRuntime = $c
        return
    }
    if ($installed.Count -eq 0) {
        Stop-Script ("Nessun runtime di contenitori trovato ($($candidates -join ' o ')). " +
            "Installa Docker Desktop (https://docs.docker.com/desktop/) oppure Podman (https://podman.io), poi riprova.")
    }
    $first = $installed[0]
    if ($first -eq 'docker') {
        Stop-Script "Docker è installato ma non risponde (o manca 'docker compose'): avvia Docker Desktop, attendi che sia pronto e riprova. In alternativa usa -Runtime podman."
    }
    Stop-Script "Podman è installato ma non risponde (o manca 'podman compose'): avvia la macchina con 'podman machine start' e verifica che 'podman compose version' funzioni."
}

function Invoke-Compose([string]$ComposeArgs, [switch]$Capture) {
    return (Invoke-Native $script:ContainerRuntime "compose -f `"$ComposeFile`" $ComposeArgs" -Capture:$Capture)
}

function Get-RunningComposeServices {
    # Nomi dei servizi compose già in esecuzione (vuoto se il comando non è supportato).
    try {
        $r = Invoke-Compose 'ps --services --filter status=running' -Capture
        if ($r.Code -ne 0) { return @() }
        return @($r.Out -split "`r?`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    } catch { return @() }
}

# ----------------------------------------------------------------------------------------------
# Prerequisiti
# ----------------------------------------------------------------------------------------------
function Test-Prerequisites([bool]$NeedContainers, [bool]$NeedWeb) {
    Write-Info 'Verifica dei prerequisiti...'

    # .NET SDK: global.json fissa la versione; dotnet --version dalla radice applica la stessa regola.
    $required = '(vedi global.json)'
    try { $required = (Get-Content -Raw -LiteralPath (Join-Path $RepoRoot 'global.json') | ConvertFrom-Json).sdk.version } catch { }
    if (-not (Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue)) {
        Stop-Script ".NET SDK non trovato. Installa l'SDK $required (https://dotnet.microsoft.com/download) e riapri il terminale."
    }
    $r = Invoke-Native 'dotnet' '--version' -Capture      # dalla radice: applica global.json
    $sdk = $r.Out.Trim()
    $runtimes = @((Invoke-Native 'dotnet' '--list-runtimes' -Capture).Out -split "`r?`n")
    if ($r.Code -ne 0) {
        Stop-Script ".NET SDK non compatibile con global.json: serve l'SDK $required o una versione più recente della stessa serie (https://dotnet.microsoft.com/download)."
    }
    if (-not ($runtimes | Where-Object { $_ -match '^Microsoft\.AspNetCore\.App 10\.' })) {
        Stop-Script "Manca il runtime ASP.NET Core 10 (i progetti hanno come target net10.0): installa 'ASP.NET Core Runtime 10.x' (https://dotnet.microsoft.com/download/dotnet/10.0)."
    }
    Write-Ok ".NET SDK $sdk (runtime ASP.NET Core 10 presente)"

    if ($NeedWeb) {
        if (-not (Get-Command node -CommandType Application -ErrorAction SilentlyContinue)) {
            Stop-Script "Node.js non trovato. Installa Node 22 o più recente (https://nodejs.org) e riapri il terminale."
        }
        $nodeVer = (Invoke-Native 'node' '--version' -Capture).Out.Trim()
        if ($nodeVer -notmatch '^v(\d+)\.' -or [int]$Matches[1] -lt 22) {
            Stop-Script "Serve Node 22 o più recente (trovato $nodeVer): aggiornalo da https://nodejs.org."
        }
        Write-Ok "Node $nodeVer"

        $pnpmVer = ''
        $r = Invoke-Native $env:ComSpec '/d /s /c "pnpm --version"' -Capture
        if ($r.Code -eq 0) { $pnpmVer = $r.Out.Trim() }
        if (-not $pnpmVer) {
            Stop-Script "pnpm non trovato. Installalo con 'npm install -g pnpm@12' oppure con 'corepack enable' (poi riapri il terminale)."
        }
        if ($pnpmVer -notmatch '^(\d+)\.' -or [int]$Matches[1] -lt 12) {
            Stop-Script "Serve pnpm 12 (trovato $pnpmVer): 'npm install -g pnpm@12'."
        }
        Write-Ok "pnpm $pnpmVer"
    }

    if ($NeedContainers) {
        Resolve-ContainerRuntime
        Write-Ok "Contenitori: $script:ContainerRuntime compose"
    }
}

# ----------------------------------------------------------------------------------------------
# Arresto
# ----------------------------------------------------------------------------------------------
function Stop-Tracked([bool]$KeepLauncher) {
    # Ferma tutti i processi registrati in .dev-logs\pids (in ordine inverso), poi rimuove il file.
    $entries = @(Read-PidFile)
    if ($entries.Count -eq 0) { return 0 }
    $stopped = 0
    # Il file si toglie PRIMA di fermare i processi: lo script in primo piano, vedendolo sparire, capisce che
    # l'arresto è voluto e non segnala la morte dei processi come un errore.
    Remove-Item -LiteralPath $PidFile -Force -ErrorAction SilentlyContinue
    [array]::Reverse($entries)
    foreach ($e in $entries) {
        if ($e.ProcId -eq $PID) { continue }
        if ($e.Nome -eq 'script') { continue }   # lo script in primo piano si ferma dopo, vedi sotto
        if (Test-Alive $e.ProcId $e.Stamp) {
            if (Stop-Tree $e.ProcId $e.Stamp) {
                Write-Info "Fermato $($e.Nome)."
                $stopped++
            } else {
                Write-Warn "Non sono riuscito a fermare $($e.Nome) (PID $($e.ProcId)): chiudilo a mano."
            }
        }
    }
    $launcher = $entries | Where-Object { $_.Nome -eq 'script' } | Select-Object -First 1
    if ($null -ne $launcher -and -not $KeepLauncher -and $launcher.ProcId -ne $PID -and (Test-Alive $launcher.ProcId $launcher.Stamp)) {
        if ($launcher.Mode -eq 'file') {
            # Processo dedicato (powershell -File): lo script in primo piano si accorge che il file non c'è più e
            # termina da solo; se non lo fa entro qualche secondo (ad esempio è ancora in compilazione) si chiude.
            for ($i = 0; $i -lt 6 -and (Test-Alive $launcher.ProcId $launcher.Stamp); $i++) { Start-Sleep -Milliseconds 500 }
            if (Test-Alive $launcher.ProcId $launcher.Stamp) { $null = Stop-Tree $launcher.ProcId $launcher.Stamp }
        } else {
            # Lanciato da una shell interattiva: quel processo è la shell dell'utente e non si chiude MAI con la forza
            # (resta viva anche a script finito, quindi non si può nemmeno sapere se lo script è ancora in corso).
            Write-Info "Il lanciatore (PID $($launcher.ProcId)) è una shell interattiva: non la chiudo. Se lo script è ancora in primo piano termina da solo entro un secondo; altrimenti premi Ctrl+C nella finestra dello script."
        }
    }
    return $stopped
}

function Show-RemainingListeners {
    $ports = @($AppPorts)
    $busy = @()
    foreach ($p in $ports) {
        $l = Get-PortListener $p
        if ($null -ne $l) { $busy += $l }
    }
    foreach ($b in $busy) { Write-Warn "La porta $($b.Porta) è ancora occupata da $($b.Nome) (PID $($b.ProcId)): non è stata avviata da questo script." }
    if ($busy.Count -eq 0) { Write-Ok 'Nessuna delle porte 5080-5082 e 5173 è in ascolto.' }
}

function Invoke-StopMode {
    $origin = Get-PidFileOrigin
    if ($origin -and $origin -ne 'dev.ps1') {
        Stop-Script "I processi risultano avviati con ${origin}: fermali con lo stesso script."
    }
    $n = Stop-Tracked $false
    if ($n -eq 0) {
        Write-Info 'Nessun processo avviato dallo script (o già fermati).'
    } else {
        Write-Ok "Processi fermati: $n."
    }
    Show-RemainingListeners
    if ($StopInfra) {
        Resolve-ContainerRuntime
        Write-Info 'Arresto dell''infrastruttura (compose down, senza -v: i volumi e i dati restano)...'
        $rc = (Invoke-Compose 'down').Code
        if ($rc -ne 0) { Stop-Script 'compose down non riuscito (vedi sopra).' }
        Write-Ok 'Infrastruttura fermata. I dati di sviluppo sono nei volumi trimetric_* e restano.'
    } else {
        Write-Info 'L''infrastruttura (PostgreSQL e NATS) resta attiva: usa -StopInfra per fermarla.'
    }
}

# ----------------------------------------------------------------------------------------------
# Avvio
# ----------------------------------------------------------------------------------------------
function Start-Hidden([string]$Nome, [string]$CommandLine, [hashtable]$EnvVars) {
    # Avvia "CommandLine" in una console nascosta con output nel log del servizio; registra il PID.
    $log = Join-Path $LogDir "$Nome.log"
    $argLine = "/d /s /c `"$CommandLine > `"$log`" 2>&1`""
    $saved = @{}
    foreach ($k in $EnvVars.Keys) {
        $saved[$k] = [Environment]::GetEnvironmentVariable($k, 'Process')
        [Environment]::SetEnvironmentVariable($k, $EnvVars[$k], 'Process')
    }
    try {
        $proc = Start-Process -FilePath $env:ComSpec -ArgumentList $argLine -WorkingDirectory $RepoRoot `
            -WindowStyle Hidden -PassThru
    } finally {
        foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k], 'Process') }
    }
    Add-PidEntry $Nome $proc.Id
    return $proc
}

function Assert-NotRunning {
    $origin = Get-PidFileOrigin
    if ($origin -and $origin -ne 'dev.ps1') {
        Stop-Script "I processi risultano avviati con ${origin}: fermali con lo stesso script."
    }
    $entries = @(Read-PidFile)
    $alive = @($entries | Where-Object { $_.ProcId -ne $PID -and (Test-Alive $_.ProcId $_.Stamp) })
    if ($alive.Count -gt 0) {
        $names = ($alive | ForEach-Object { $_.Nome }) -join ', '
        Stop-Script ("Risulta già attivo un avvio precedente ($names). Non ne avvio un altro: " +
            "fermalo con 'scripts\dev.ps1 -Stop' (e riavvia) oppure usa quello che c'è già.")
    }
    if ($entries.Count -gt 0) {
        Remove-Item -LiteralPath $PidFile -Force -ErrorAction SilentlyContinue   # file residuo di un avvio terminato
    }
}

function Assert-PortsFree([int[]]$Ports, [string]$Hint) {
    $busy = @()
    foreach ($p in $Ports) {
        $l = Get-PortListener $p
        if ($null -ne $l) { $busy += $l }
    }
    if ($busy.Count -gt 0) {
        $list = ($busy | ForEach-Object { "porta $($_.Porta): $($_.Nome) (PID $($_.ProcId))" }) -join '; '
        Stop-Script "Porte già occupate: $list. $Hint"
    }
}

function Start-Infrastructure {
    $running = Get-RunningComposeServices
    if ($running -contains 'postgres' -and $running -contains 'nats') {
        Write-Info 'Infrastruttura già attiva (postgres e nats): la lascio com''è.'
    } else {
        Assert-PortsFree $InfraPorts 'Libera le porte (se sono PostgreSQL e NATS già avviati da compose usa -NoInfra).'
        Write-Info 'Avvio dell''infrastruttura (compose up -d: la prima volta scarica le immagini)...'
        $rc = (Invoke-Compose 'up -d').Code
        if ($rc -ne 0) { Stop-Script 'compose up -d non riuscito (vedi sopra).' }
    }
}

function Wait-Infrastructure([bool]$UseCompose) {
    Write-Info 'Attendo PostgreSQL (fino a 120 s)...'
    $pgCheck = {
        if ($UseCompose) {
            # -h 127.0.0.1: il server temporaneo dell'inizializzazione ascolta solo sul socket Unix,
            # quindi pg_isready via TCP risponde solo a inizializzazione finita (schemi e ruoli creati).
            if ((Invoke-Compose 'exec -T postgres pg_isready -h 127.0.0.1' -Capture).Code -ne 0) { return $false }
        }
        return (Test-Tcp '127.0.0.1' 5432)
    }
    if ((Wait-Until $pgCheck 120) -ne 'ok') {
        Stop-Script 'PostgreSQL non è pronto entro 120 s. Controlla i contenitori (docker compose -f deploy/compose.yaml ps e logs postgres) e la porta 5432.'
    }
    Write-Ok 'PostgreSQL pronto (127.0.0.1:5432)'

    Write-Info 'Attendo NATS (fino a 60 s)...'
    if ((Wait-Until { Test-Http $NatsHealth } 60) -ne 'ok') {
        Stop-Script "NATS non risponde su $NatsHealth entro 60 s. Controlla il contenitore (compose logs nats) e le porte 4222 e 8222."
    }
    Write-Ok 'NATS pronto (127.0.0.1:4222, monitor 127.0.0.1:8222)'
}

function Start-Everything {
    $started = @()
    $dotnet = (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source

    Write-Info 'Compilazione dei servizi, una volta sola e in sequenza (la prima volta può richiedere qualche minuto)...'
    foreach ($s in $Services) {
        Write-Info "  compilo $($s.Nome)..."
        $rc = (Invoke-Native 'dotnet' "build `"$(Join-Path $RepoRoot $s.Csproj)`" --nologo -v q").Code
        if ($rc -ne 0) { Stop-Script "Compilazione di $($s.Nome) non riuscita (errori sopra)." }
    }
    Write-Ok 'Compilazione completata'

    $envDev = @{ ASPNETCORE_ENVIRONMENT = 'Development'; Dev__UserId = $DevUserId }
    foreach ($s in $Services) {
        Write-Info "Avvio di $($s.Nome)..."
        $cmd = "`"$dotnet`" run --no-build --no-launch-profile --project `"$(Join-Path $RepoRoot $s.Csproj)`""
        $proc = Start-Hidden $s.Nome $cmd $envDev
        $started += $s.Nome
        $stamp = Get-Stamp $proc.Id
        # Niente GetNewClosure(): le funzioni dello script non sarebbero visibili se è lanciato con "&" da una
        # shell; le variabili ($s, $proc, $stamp) si leggono dalla funzione chiamante (ambito dinamico).
        $check = { Test-Http $s.Health }
        $abort = { -not (Test-Alive $proc.Id $stamp) }
        $res = Wait-Until $check 90 $abort
        if ($res -eq 'interrotto') { Stop-Script "$($s.Nome) è terminato durante l'avvio: vedi .dev-logs\$($s.Nome).log" }
        if ($res -ne 'ok')         { Stop-Script "$($s.Nome) non risponde su $($s.Health) entro 90 s: vedi .dev-logs\$($s.Nome).log" }
        Write-Ok "$($s.Nome) pronto ($($s.Health))"
    }

    if (-not $NoWeb) {
        if (-not (Test-Path -LiteralPath (Join-Path $WebDir 'node_modules'))) {
            Write-Info 'Installo le dipendenze del web (pnpm install --frozen-lockfile)...'
            $rc = (Invoke-Native $env:ComSpec "/d /s /c `"pnpm -C `"$WebDir`" install --frozen-lockfile`"").Code
            if ($rc -ne 0) { Stop-Script 'pnpm install non riuscito (vedi sopra).' }
        }
        Write-Info 'Avvio del web...'
        $proc = Start-Hidden 'web' "pnpm -C `"$WebDir`" dev" @{}
        $stamp = Get-Stamp $proc.Id
        $abort = { -not (Test-Alive $proc.Id $stamp) }
        $res = Wait-Until { Test-Http $WebUrl } 60 $abort
        if ($res -eq 'interrotto') { Stop-Script 'Il web è terminato durante l''avvio: vedi .dev-logs\web.log' }
        if ($res -ne 'ok')         { Stop-Script "Il web non risponde su $WebUrl entro 60 s: vedi .dev-logs\web.log" }
        Write-Ok "web pronto ($WebUrl)"
    }
}

function Show-Summary {
    Write-Host ''
    Write-Ok 'Tutto avviato.'
    if (-not $NoWeb) { Write-Host "  Web:         $WebUrl   (apri questo indirizzo; /api è inoltrato al gateway)" }
    Write-Host '  Gateway:     http://localhost:5080   (POST /api/ingestion/uploads, GET /api/activities)'
    Write-Host '  Ingestion:   http://127.0.0.1:5081'
    Write-Host '  Activities:  http://127.0.0.1:5082'
    Write-Host '  NATS:        nats://127.0.0.1:4222   (monitor http://127.0.0.1:8222)'
    Write-Host '  PostgreSQL:  127.0.0.1:5432'
    Write-Host "  Log:         $LogDir  (un file per servizio)"
    Write-Host ''
    Write-Info 'Per fermare: Ctrl+C in questa finestra, oppure da un altro terminale scripts\dev.ps1 -Stop'
    Write-Info '(aggiungi -StopInfra per fermare anche i contenitori; i dati restano nei volumi).'
}

function Wait-ForExit {
    # Resta in primo piano finché qualcuno non ferma tutto (Ctrl+C, -Stop) o un processo muore.
    while ($true) {
        Start-Sleep -Seconds 1
        if (-not (Test-Path -LiteralPath $PidFile)) {
            Write-Info 'Arresto richiesto da un altro terminale (-Stop).'
            return 0
        }
        foreach ($e in @(Read-PidFile)) {
            if ($e.Nome -eq 'script') { continue }
            if (-not (Test-Alive $e.ProcId $e.Stamp)) {
                if (-not (Test-Path -LiteralPath $PidFile)) { Write-Info 'Arresto richiesto da un altro terminale (-Stop).'; return 0 }
                Write-Err "Il processo '$($e.Nome)' è terminato: vedi .dev-logs\$($e.Nome).log. Fermo tutto."
                return 1
            }
        }
    }
}

# ----------------------------------------------------------------------------------------------
# Programma principale
# ----------------------------------------------------------------------------------------------
$exitCode = 0
$launched = $false
try {
    if ($Help) { Show-Help; return }

    if ($env:OS -ne 'Windows_NT') {
        Stop-Script 'Questo script è per Windows: su Linux, macOS o Git Bash usa scripts/dev.sh.'
    }

    if ($Stop -or $StopInfra) { Invoke-StopMode; return }

    New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
    Assert-NotRunning

    Test-Prerequisites (-not $NoInfra) (-not $NoWeb)

    $ports = @($AppPorts)
    if ($NoWeb) { $ports = @(5080, 5081, 5082) }
    Assert-PortsFree $ports 'Chiudi i processi che le occupano (o fermali con scripts\dev.ps1 -Stop se li ha avviati questo script) e riprova.'

    if ($NoInfra) {
        Write-Info 'Opzione -NoInfra: non avvio l''infrastruttura, verifico che PostgreSQL e NATS rispondano.'
    } else {
        Start-Infrastructure
    }
    Wait-Infrastructure (-not $NoInfra)

    # Da qui in poi lo script è il proprietario dei processi: l'arresto passa dal blocco finally.
    Set-Content -LiteralPath $PidFile -Value '# avviato da: dev.ps1' -Encoding ASCII
    Add-PidEntry 'script' $PID (Get-LaunchMode)
    $launched = $true

    Start-Everything
    Show-Summary
    $exitCode = Wait-ForExit
}
catch {
    Write-Err $_.Exception.Message
    $exitCode = 1
}
finally {
    if ($launched) {
        Write-Host ''
        Write-Info 'Arresto dei servizi e del web...'
        $null = Stop-Tracked $true
        Remove-Item -LiteralPath $PidFile -Force -ErrorAction SilentlyContinue
        Write-Ok 'Servizi fermati. L''infrastruttura (PostgreSQL e NATS) resta attiva: scripts\dev.ps1 -StopInfra per fermarla.'
    }
}
exit $exitCode
