#!/usr/bin/env bash
# Avvio locale di Metrica: infrastruttura, servizi .NET e web (Linux, macOS, Git Bash su Windows).
#
# Stessa interfaccia e stessi messaggi di scripts/dev.ps1 (DEC-009):
#   1. infrastruttura (PostgreSQL/TimescaleDB e NATS) con "compose -f deploy/compose.yaml up -d";
#   2. compilazione, una volta sola e in sequenza, di ingestion, activities e gateway;
#   3. i tre servizi, uno alla volta, con "dotnet run --no-build" in ambiente Development;
#   4. il web (Vite) su http://localhost:5173.
# Resta in primo piano: Ctrl+C ferma i servizi e il web (l'infrastruttura resta attiva).
# Funziona da qualsiasi cartella di lavoro.
#
# Non espone nulla sulla rete: i servizi ascoltano su 127.0.0.1, il gateway e il web su localhost.
# Il dev server NON si avvia mai con --host: i servizi usano un'identità di sviluppo fissa (DEC-006).
#
# Richiede: bash, curl, dotnet (SDK di global.json), node 22+, pnpm 12, docker o podman.

set -u

# Va eseguito, non incluso con "source": uno script incluso girerebbe nella shell dell'utente.
if [ "${BASH_SOURCE[0]}" != "$0" ]; then
  echo "[errore] Esegui lo script con 'bash scripts/dev.sh', senza 'source'." >&2
  return 1 2>/dev/null || exit 1
fi

# ---------------------------------------------------------------------------------------------
# Costanti (porte e indirizzi di DEC-009; identità di sviluppo di DEC-006)
# ---------------------------------------------------------------------------------------------
# Su Git Bash "pwd -W" dà il percorso in stile Windows (C:/...), accettato anche dai programmi nativi.
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && { pwd -W 2>/dev/null || pwd; })"
COMPOSE_FILE="$ROOT/deploy/compose.yaml"
LOG_DIR="$ROOT/.dev-logs"
PID_FILE="$LOG_DIR/pids"
WEB_DIR="$ROOT/src/web"
DEV_USER_ID="00000000-0000-4000-8000-000000000001"   # lo stesso di appsettings.Development.json

SERVICE_NAMES=(ingestion activities gateway)
SERVICE_CSPROJ=(
  "src/services/ingestion/src/Metrica.Ingestion/Metrica.Ingestion.csproj"
  "src/services/activities/src/Metrica.Activities/Metrica.Activities.csproj"
  "src/services/gateway/src/Metrica.Gateway/Metrica.Gateway.csproj"
)
SERVICE_HEALTH=(
  "http://127.0.0.1:5081/health/live"
  "http://127.0.0.1:5082/health/live"
  "http://localhost:5080/health/live"
)
WEB_URL="http://localhost:5173"
NATS_HEALTH="http://127.0.0.1:8222/healthz"
APP_PORTS=(5080 5081 5082 5173)
INFRA_PORTS=(4222 5432 8222)

NO_INFRA=0
NO_WEB=0
DO_STOP=0
DO_STOP_INFRA=0
RUNTIME_OPT=""
CONTAINER_RUNTIME=""
LAUNCHED=0

case "$(uname -s 2>/dev/null)" in
  MINGW* | MSYS* | CYGWIN*) IS_MSYS=1 ;;
  *) IS_MSYS=0 ;;
esac

# ---------------------------------------------------------------------------------------------
# Messaggi (solo URL, porte e stato: mai valori di file, coordinate o nomi di file)
# ---------------------------------------------------------------------------------------------
if [ -t 1 ]; then C_OK=$'\033[32m'; C_WARN=$'\033[33m'; C_ERR=$'\033[31m'; C_OFF=$'\033[0m'
else C_OK=""; C_WARN=""; C_ERR=""; C_OFF=""; fi
info() { printf '[dev] %s\n' "$*"; }
ok()   { printf '%s[ok] %s%s\n' "$C_OK" "$*" "$C_OFF"; }
warn() { printf '%s[attenzione] %s%s\n' "$C_WARN" "$*" "$C_OFF"; }
err()  { printf '%s[errore] %s%s\n' "$C_ERR" "$*" "$C_OFF" >&2; }
die()  { err "$*"; exit 1; }

show_help() {
  cat <<'EOF'
Uso: scripts/dev.sh [opzioni]

Avvia l'infrastruttura (PostgreSQL e NATS), compila ingestion, activities e gateway,
li avvia in sviluppo e avvia il web su http://localhost:5173. Resta in primo piano:
Ctrl+C ferma i servizi e il web (l'infrastruttura resta attiva).

Opzioni:
  --no-infra                 non avvia l'infrastruttura (PostgreSQL e NATS già attivi)
  --no-web                   non avvia il web
  --stop                     ferma i servizi e il web avviati dallo script
  --stop-infra               come --stop e poi ferma i contenitori (compose down, senza -v: i dati restano)
  --runtime docker|podman    sceglie il runtime dei contenitori (o variabile METRICA_CONTAINER_RUNTIME)
  --help                     mostra questo aiuto

Log in .dev-logs/ (un file per servizio). Guida completa: docs/getting-started.md
EOF
}

# ---------------------------------------------------------------------------------------------
# Utilità per processi e porte
# ---------------------------------------------------------------------------------------------
proc_stamp() {
  # Istante di avvio del processo. Vuoto dove non è disponibile (Git Bash: ps non lo mostra e il PID di
  # Windows cambia quando uno script fa exec): lì il controllo anti-riuso del PID non c'è.
  [ "$IS_MSYS" = 1 ] && return 0
  ps -o lstart= -p "$1" 2>/dev/null | tr -s ' ' | sed 's/^ //'
  return 0
}

# entry_alive PID STAMP: il processo esiste ed è ancora quello registrato (nessun PID riusato).
entry_alive() {
  local pid=$1 stamp=$2
  kill -0 "$pid" 2>/dev/null || return 1
  if [ -n "$stamp" ]; then [ "$(proc_stamp "$pid")" = "$stamp" ] || return 1; fi
  return 0
}

# Voci del file dei PID, una per riga: "nome;pid;stamp".
add_pid_entry() { printf '%s;%s;%s\n' "$1" "$2" "$(proc_stamp "$2")" >>"$PID_FILE"; }

pid_file_origin() {
  [ -f "$PID_FILE" ] || return 0
  head -n 1 "$PID_FILE" | sed -n 's/^# avviato da: *\([^ ]*\).*/\1/p'
}

# Carica il file dei PID negli array E_NOME, E_PID, E_STAMP.
load_entries() {
  E_NOME=(); E_PID=(); E_STAMP=()
  [ -f "$PID_FILE" ] || return 0
  local nome pid stamp
  while IFS=';' read -r nome pid stamp; do
    case "$nome" in '' | '#'*) continue ;; esac
    E_NOME+=("$nome"); E_PID+=("$pid"); E_STAMP+=("${stamp:-}")
  done <"$PID_FILE"
}

# kill_tree PID: termina il processo e i suoi discendenti.
kill_tree() {
  local pid=$1 i winpid
  if [ "$IS_MSYS" = 1 ]; then
    # Git Bash: i processi sono nativi Windows, il gruppo di processi non basta. Il PID di Windows si legge
    # adesso (cambia dopo un exec) e "//" evita la conversione dei percorsi.
    winpid="$(cat "/proc/$pid/winpid" 2>/dev/null)"
    [ -z "$winpid" ] || taskkill //PID "$winpid" //T //F >/dev/null 2>&1
    kill -KILL "$pid" 2>/dev/null
    return 0
  fi
  kill -TERM -- "-$pid" 2>/dev/null || kill -TERM "$pid" 2>/dev/null
  for i in 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 16 17 18 19 20; do
    kill -0 "$pid" 2>/dev/null || break
    sleep 0.25
  done
  if kill -0 "$pid" 2>/dev/null; then kill -KILL -- "-$pid" 2>/dev/null || kill -KILL "$pid" 2>/dev/null; fi
  return 0
}

# port_listener PORTA: se la porta è in ascolto stampa "nome (PID n)" e restituisce 0.
port_listener() {
  local port=$1 pid="" name=""
  if [ "$IS_MSYS" = 1 ]; then
    pid="$(netstat -ano 2>/dev/null | tr -d '\r' | awk -v p=":$port" '$1=="TCP" && $3 ~ /:0$/ && substr($2, length($2)-length(p)+1)==p {print $5; exit}')"
    [ -n "$pid" ] || return 1
    # Non si legge lo stato (testo localizzato): un socket in ascolto ha indirizzo remoto che finisce con ":0".
    name="$(tasklist //FI "PID eq $pid" //FO CSV //NH 2>/dev/null | tr -d '\r' | grep '^"' | head -n 1 | cut -d, -f1 | tr -d '"')"
  elif command -v ss >/dev/null 2>&1; then
    local line
    line="$(ss -H -ltnp "sport = :$port" 2>/dev/null | head -n 1)"
    [ -n "$line" ] || return 1
    name="$(printf '%s' "$line" | sed -n 's/.*users:(("\([^"]*\)".*/\1/p')"
    pid="$(printf '%s' "$line" | sed -n 's/.*pid=\([0-9]*\).*/\1/p')"
  elif command -v lsof >/dev/null 2>&1; then
    local line
    line="$(lsof -nP -iTCP:"$port" -sTCP:LISTEN 2>/dev/null | sed -n '2p')"
    [ -n "$line" ] || return 1
    name="$(printf '%s' "$line" | awk '{print $1}')"
    pid="$(printf '%s' "$line" | awk '{print $2}')"
  else
    (exec 3<>"/dev/tcp/127.0.0.1/$port") 2>/dev/null || return 1
  fi
  printf '%s (PID %s)\n' "${name:-processo sconosciuto}" "${pid:-?}"
  return 0
}

http_ok() { curl -fsS --noproxy '*' -o /dev/null --max-time 3 "$1" >/dev/null 2>&1; }

# wait_http URL TIMEOUT [PID]: 0 = risponde 200; 1 = timeout; 2 = il processo PID è terminato.
wait_http() {
  local url=$1 timeout=$2 pid=${3:-} deadline=$((SECONDS + $2))
  while [ "$SECONDS" -lt "$deadline" ]; do
    if [ -n "$pid" ] && ! kill -0 "$pid" 2>/dev/null; then return 2; fi
    http_ok "$url" && return 0
    sleep 0.7
  done
  return 1
}

# ---------------------------------------------------------------------------------------------
# Runtime dei contenitori
# ---------------------------------------------------------------------------------------------
resolve_container_runtime() {
  local wanted="${RUNTIME_OPT:-${METRICA_CONTAINER_RUNTIME:-}}" c candidates installed=""
  wanted="$(printf '%s' "$wanted" | tr '[:upper:]' '[:lower:]')"
  case "$wanted" in
    '' | docker | podman) ;;
    *) die "Runtime dei contenitori non valido ('$wanted'): usa docker o podman." ;;
  esac
  if [ -n "$wanted" ]; then candidates="$wanted"; else candidates="docker podman"; fi
  for c in $candidates; do
    command -v "$c" >/dev/null 2>&1 || continue
    installed="${installed:+$installed }$c"
    "$c" info >/dev/null 2>&1 || continue
    "$c" compose version >/dev/null 2>&1 || continue
    CONTAINER_RUNTIME="$c"
    return 0
  done
  if [ -z "$installed" ]; then
    die "Nessun runtime di contenitori trovato ($(printf '%s' "$candidates" | sed 's/ / o /')). Installa Docker (https://docs.docker.com/get-docker/) oppure Podman (https://podman.io), poi riprova."
  fi
  case "${installed%% *}" in
    docker) die "Docker è installato ma non risponde (o manca 'docker compose'): avvia Docker Desktop (o il servizio docker), attendi che sia pronto e riprova. In alternativa usa --runtime podman." ;;
    *) die "Podman è installato ma non risponde (o manca 'podman compose'): avvia la macchina con 'podman machine start' e verifica che 'podman compose version' funzioni." ;;
  esac
}

compose() { "$CONTAINER_RUNTIME" compose -f "$COMPOSE_FILE" "$@"; }

# ---------------------------------------------------------------------------------------------
# Prerequisiti
# ---------------------------------------------------------------------------------------------
check_prerequisites() {
  local need_containers=$1 need_web=$2 required sdk node_ver pnpm_ver
  info "Verifica dei prerequisiti..."

  command -v curl >/dev/null 2>&1 || die "curl non trovato: installalo (serve per controllare lo stato dei servizi) e riprova."

  required="$(sed -n 's/.*"version" *: *"\([^"]*\)".*/\1/p' "$ROOT/global.json" 2>/dev/null | head -n 1)"
  required="${required:-(vedi global.json)}"
  command -v dotnet >/dev/null 2>&1 || die ".NET SDK non trovato. Installa l'SDK $required (https://dotnet.microsoft.com/download) e riapri il terminale."
  # Dalla radice: dotnet --version applica la stessa regola di global.json.
  if ! sdk="$(cd "$ROOT" && dotnet --version 2>/dev/null)"; then
    die ".NET SDK non compatibile con global.json: serve l'SDK $required o una versione più recente della stessa serie (https://dotnet.microsoft.com/download)."
  fi
  if ! (cd "$ROOT" && dotnet --list-runtimes 2>/dev/null) | grep -q '^Microsoft\.AspNetCore\.App 10\.'; then
    die "Manca il runtime ASP.NET Core 10 (i progetti hanno come target net10.0): installa 'ASP.NET Core Runtime 10.x' (https://dotnet.microsoft.com/download/dotnet/10.0)."
  fi
  ok ".NET SDK $sdk (runtime ASP.NET Core 10 presente)"

  if [ "$need_web" = 1 ]; then
    command -v node >/dev/null 2>&1 || die "Node.js non trovato. Installa Node 22 o più recente (https://nodejs.org) e riapri il terminale."
    node_ver="$(node --version)"
    case "$node_ver" in v[0-9].* | v1[0-9].* | v2[01].*) die "Serve Node 22 o più recente (trovato $node_ver): aggiornalo da https://nodejs.org." ;; esac
    ok "Node $node_ver"

    pnpm_ver="$(pnpm --version 2>/dev/null)" || pnpm_ver=""
    [ -n "$pnpm_ver" ] || die "pnpm non trovato. Installalo con 'npm install -g pnpm@12' oppure con 'corepack enable' (poi riapri il terminale)."
    case "$pnpm_ver" in [0-9].* | 1[01].*) die "Serve pnpm 12 (trovato $pnpm_ver): 'npm install -g pnpm@12'." ;; esac
    ok "pnpm $pnpm_ver"
  fi

  if [ "$need_containers" = 1 ]; then
    resolve_container_runtime
    ok "Contenitori: $CONTAINER_RUNTIME compose"
  fi
}

# ---------------------------------------------------------------------------------------------
# Arresto
# ---------------------------------------------------------------------------------------------
# stop_tracked KEEP_LAUNCHER: ferma i processi registrati (in ordine inverso) e rimuove il file dei PID.
STOPPED_COUNT=0
stop_tracked() {
  local keep_launcher=$1 i j n launcher_pid="" launcher_stamp=""
  STOPPED_COUNT=0
  load_entries
  n=${#E_NOME[@]}
  [ "$n" -gt 0 ] || return 0
  # Il file si toglie PRIMA di fermare i processi: lo script in primo piano, vedendolo sparire, capisce che
  # l'arresto è voluto e non segnala la morte dei processi come un errore.
  rm -f "$PID_FILE"
  for ((i = n - 1; i >= 0; i--)); do
    if [ "${E_NOME[$i]}" = "script" ]; then
      launcher_pid="${E_PID[$i]}"; launcher_stamp="${E_STAMP[$i]}"
      continue
    fi
    [ "${E_PID[$i]}" = "$$" ] && continue
    if entry_alive "${E_PID[$i]}" "${E_STAMP[$i]}"; then
      kill_tree "${E_PID[$i]}"
      for j in 1 2 3 4 5 6 7 8; do   # il processo può restare visibile per un istante dopo il kill
        entry_alive "${E_PID[$i]}" "${E_STAMP[$i]}" || break
        sleep 0.25
      done
      if entry_alive "${E_PID[$i]}" "${E_STAMP[$i]}"; then
        warn "Non sono riuscito a fermare ${E_NOME[$i]} (PID ${E_PID[$i]}): chiudilo a mano."
      else
        info "Fermato ${E_NOME[$i]}."
        STOPPED_COUNT=$((STOPPED_COUNT + 1))
      fi
    fi
  done
  if [ "$keep_launcher" != 1 ] && [ -n "$launcher_pid" ] && [ "$launcher_pid" != "$$" ] \
     && entry_alive "$launcher_pid" "$launcher_stamp"; then
    # Lo script in primo piano (altro terminale) si accorge che il file non c'è più e termina da solo;
    # se non lo fa entro qualche secondo (ad esempio è ancora in compilazione) si ferma con la forza.
    for i in 1 2 3 4 5 6; do
      entry_alive "$launcher_pid" "$launcher_stamp" || break
      sleep 0.5
    done
    if entry_alive "$launcher_pid" "$launcher_stamp"; then kill_tree "$launcher_pid"; fi
  fi
  return 0
}

show_remaining_listeners() {
  local p who busy=0
  for p in "${APP_PORTS[@]}"; do
    if who="$(port_listener "$p")"; then
      warn "La porta $p è ancora occupata da $who: non è stata avviata da questo script."
      busy=1
    fi
  done
  [ "$busy" = 0 ] && ok "Nessuna delle porte 5080-5082 e 5173 è in ascolto."
  return 0
}

stop_mode() {
  local origin
  origin="$(pid_file_origin)"
  if [ -n "$origin" ] && [ "$origin" != "dev.sh" ]; then
    die "I processi risultano avviati con $origin: fermali con lo stesso script."
  fi
  stop_tracked 0
  if [ "$STOPPED_COUNT" -eq 0 ]; then
    info "Nessun processo avviato dallo script (o già fermati)."
  else
    ok "Processi fermati: $STOPPED_COUNT."
  fi
  show_remaining_listeners
  if [ "$DO_STOP_INFRA" = 1 ]; then
    resolve_container_runtime
    info "Arresto dell'infrastruttura (compose down, senza -v: i volumi e i dati restano)..."
    compose down || die "compose down non riuscito (vedi sopra)."
    ok "Infrastruttura fermata. I dati di sviluppo sono nei volumi metrica_* e restano."
  else
    info "L'infrastruttura (PostgreSQL e NATS) resta attiva: usa --stop-infra per fermarla."
  fi
}

# ---------------------------------------------------------------------------------------------
# Avvio
# ---------------------------------------------------------------------------------------------
# start_bg NOME COMANDO...: avvia il comando in background, in un gruppo di processi proprio, con l'output
# in .dev-logs/NOME.log; registra il PID e lo lascia in START_PID.
START_PID=""
start_bg() {
  local nome=$1; shift
  set -m
  ( cd "$ROOT" || exit 1; exec "$@" ) >"$LOG_DIR/$nome.log" 2>&1 &
  START_PID=$!
  set +m
  disown "$START_PID" 2>/dev/null   # niente messaggi "Killed" quando lo si ferma
  add_pid_entry "$nome" "$START_PID"
}

assert_not_running() {
  local i alive="" n
  load_entries
  n=${#E_NOME[@]}
  for ((i = 0; i < n; i++)); do
    [ "${E_PID[$i]}" = "$$" ] && continue
    if entry_alive "${E_PID[$i]}" "${E_STAMP[$i]}"; then alive="${alive:+$alive, }${E_NOME[$i]}"; fi
  done
  if [ -n "$alive" ]; then
    die "Risulta già attivo un avvio precedente ($alive). Non ne avvio un altro: fermalo con 'scripts/dev.sh --stop' (e riavvia) oppure usa quello che c'è già."
  fi
  [ "$n" -gt 0 ] && rm -f "$PID_FILE"   # file residuo di un avvio terminato
  return 0
}

# assert_ports_free SUGGERIMENTO PORTA...
assert_ports_free() {
  local hint=$1 p who list=""; shift
  for p in "$@"; do
    if who="$(port_listener "$p")"; then list="${list:+$list; }porta $p: $who"; fi
  done
  [ -z "$list" ] || die "Porte già occupate: $list. $hint"
}

running_compose_services() { compose ps --services --filter status=running 2>/dev/null || true; }

start_infrastructure() {
  local running
  running="$(running_compose_services)"
  if printf '%s\n' "$running" | grep -qx postgres && printf '%s\n' "$running" | grep -qx nats; then
    info "Infrastruttura già attiva (postgres e nats): la lascio com'è."
  else
    assert_ports_free "Libera le porte (se sono PostgreSQL e NATS già avviati da compose usa --no-infra)." "${INFRA_PORTS[@]}"
    info "Avvio dell'infrastruttura (compose up -d: la prima volta scarica le immagini)..."
    compose up -d || die "compose up -d non riuscito (vedi sopra)."
  fi
}

pg_ready() {
  if [ "$1" = 1 ]; then
    # -h 127.0.0.1: il server temporaneo dell'inizializzazione ascolta solo sul socket Unix,
    # quindi pg_isready via TCP risponde solo a inizializzazione finita (schemi e ruoli creati).
    compose exec -T postgres pg_isready -h 127.0.0.1 >/dev/null 2>&1 || return 1
  fi
  (exec 3<>/dev/tcp/127.0.0.1/5432) 2>/dev/null
}

wait_infrastructure() {
  local use_compose=$1 deadline
  info "Attendo PostgreSQL (fino a 120 s)..."
  deadline=$((SECONDS + 120))
  until pg_ready "$use_compose"; do
    [ "$SECONDS" -lt "$deadline" ] || die "PostgreSQL non è pronto entro 120 s. Controlla i contenitori (docker compose -f deploy/compose.yaml ps e logs postgres) e la porta 5432."
    sleep 0.7
  done
  ok "PostgreSQL pronto (127.0.0.1:5432)"

  info "Attendo NATS (fino a 60 s)..."
  wait_http "$NATS_HEALTH" 60 || die "NATS non risponde su $NATS_HEALTH entro 60 s. Controlla il contenitore (compose logs nats) e le porte 4222 e 8222."
  ok "NATS pronto (127.0.0.1:4222, monitor 127.0.0.1:8222)"
}

start_everything() {
  local i name csproj health rc
  info "Compilazione dei servizi, una volta sola e in sequenza (la prima volta può richiedere qualche minuto)..."
  for i in "${!SERVICE_NAMES[@]}"; do
    info "  compilo ${SERVICE_NAMES[$i]}..."
    (cd "$ROOT" && dotnet build "$ROOT/${SERVICE_CSPROJ[$i]}" --nologo -v q) || die "Compilazione di ${SERVICE_NAMES[$i]} non riuscita (errori sopra)."
  done
  ok "Compilazione completata"

  for i in "${!SERVICE_NAMES[@]}"; do
    name="${SERVICE_NAMES[$i]}"; csproj="$ROOT/${SERVICE_CSPROJ[$i]}"; health="${SERVICE_HEALTH[$i]}"
    info "Avvio di $name..."
    start_bg "$name" env ASPNETCORE_ENVIRONMENT=Development "Dev__UserId=$DEV_USER_ID" \
      dotnet run --no-build --no-launch-profile --project "$csproj"
    wait_http "$health" 90 "$START_PID"; rc=$?
    [ "$rc" -ne 2 ] || die "$name è terminato durante l'avvio: vedi .dev-logs/$name.log"
    [ "$rc" -eq 0 ] || die "$name non risponde su $health entro 90 s: vedi .dev-logs/$name.log"
    ok "$name pronto ($health)"
  done

  if [ "$NO_WEB" != 1 ]; then
    if [ ! -d "$WEB_DIR/node_modules" ]; then
      info "Installo le dipendenze del web (pnpm install --frozen-lockfile)..."
      pnpm -C "$WEB_DIR" install --frozen-lockfile || die "pnpm install non riuscito (vedi sopra)."
    fi
    info "Avvio del web..."
    start_bg web pnpm -C "$WEB_DIR" dev
    wait_http "$WEB_URL" 60 "$START_PID"; rc=$?
    [ "$rc" -ne 2 ] || die "Il web è terminato durante l'avvio: vedi .dev-logs/web.log"
    [ "$rc" -eq 0 ] || die "Il web non risponde su $WEB_URL entro 60 s: vedi .dev-logs/web.log"
    ok "web pronto ($WEB_URL)"
  fi
}

show_summary() {
  echo
  ok "Tutto avviato."
  [ "$NO_WEB" = 1 ] || echo "  Web:         $WEB_URL   (apri questo indirizzo; /api è inoltrato al gateway)"
  echo "  Gateway:     http://localhost:5080   (POST /api/ingestion/uploads, GET /api/activities)"
  echo "  Ingestion:   http://127.0.0.1:5081"
  echo "  Activities:  http://127.0.0.1:5082"
  echo "  NATS:        nats://127.0.0.1:4222   (monitor http://127.0.0.1:8222)"
  echo "  PostgreSQL:  127.0.0.1:5432"
  echo "  Log:         $LOG_DIR  (un file per servizio)"
  echo
  info "Per fermare: Ctrl+C in questa finestra, oppure da un altro terminale scripts/dev.sh --stop"
  info "(aggiungi --stop-infra per fermare anche i contenitori; i dati restano nei volumi)."
}

# Resta in primo piano finché qualcuno non ferma tutto (Ctrl+C, --stop) o un processo muore.
wait_for_exit() {
  local i n
  while true; do
    sleep 1
    if [ ! -f "$PID_FILE" ]; then
      info "Arresto richiesto da un altro terminale (--stop)."
      return 0
    fi
    load_entries
    n=${#E_NOME[@]}
    for ((i = 0; i < n; i++)); do
      [ "${E_NOME[$i]}" = "script" ] && continue
      if ! entry_alive "${E_PID[$i]}" "${E_STAMP[$i]}"; then
        if [ ! -f "$PID_FILE" ]; then info "Arresto richiesto da un altro terminale (--stop)."; return 0; fi
        err "Il processo '${E_NOME[$i]}' è terminato: vedi .dev-logs/${E_NOME[$i]}.log. Fermo tutto."
        return 1
      fi
    done
  done
}

cleanup() {
  if [ "$LAUNCHED" = 1 ]; then
    LAUNCHED=0
    echo
    info "Arresto dei servizi e del web..."
    stop_tracked 1
    rm -f "$PID_FILE"
    ok "Servizi fermati. L'infrastruttura (PostgreSQL e NATS) resta attiva: scripts/dev.sh --stop-infra per fermarla."
  fi
}

# ---------------------------------------------------------------------------------------------
# Programma principale
# ---------------------------------------------------------------------------------------------
while [ $# -gt 0 ]; do
  case "$1" in
    --no-infra) NO_INFRA=1 ;;
    --no-web) NO_WEB=1 ;;
    --stop) DO_STOP=1 ;;
    --stop-infra) DO_STOP_INFRA=1 ;;
    --runtime)
      [ $# -ge 2 ] || die "L'opzione --runtime richiede un valore: docker o podman."
      RUNTIME_OPT="$2"; shift ;;
    --runtime=*) RUNTIME_OPT="${1#--runtime=}" ;;
    -h | --help) show_help; exit 0 ;;
    *) err "Opzione sconosciuta: $1"; show_help >&2; exit 2 ;;
  esac
  shift
done

if [ "$DO_STOP" = 1 ] || [ "$DO_STOP_INFRA" = 1 ]; then
  stop_mode
  exit 0
fi

origin="$(pid_file_origin)"
if [ -n "$origin" ] && [ "$origin" != "dev.sh" ]; then
  die "I processi risultano avviati con $origin: fermali con lo stesso script."
fi
mkdir -p "$LOG_DIR"
assert_not_running

if [ "$NO_INFRA" = 1 ]; then need_containers=0; else need_containers=1; fi
if [ "$NO_WEB" = 1 ]; then need_web=0; else need_web=1; fi
check_prerequisites "$need_containers" "$need_web"

if [ "$NO_WEB" = 1 ]; then
  assert_ports_free "Chiudi i processi che le occupano (o fermali con scripts/dev.sh --stop se li ha avviati questo script) e riprova." 5080 5081 5082
else
  assert_ports_free "Chiudi i processi che le occupano (o fermali con scripts/dev.sh --stop se li ha avviati questo script) e riprova." "${APP_PORTS[@]}"
fi

if [ "$NO_INFRA" = 1 ]; then
  info "Opzione --no-infra: non avvio l'infrastruttura, verifico che PostgreSQL e NATS rispondano."
  wait_infrastructure 0
else
  start_infrastructure
  wait_infrastructure 1
fi

# Da qui in poi lo script è il proprietario dei processi: l'arresto passa dal gestore di uscita.
trap cleanup EXIT
trap 'exit 130' INT TERM
printf '# avviato da: dev.sh\n' >"$PID_FILE"
add_pid_entry script "$$"
LAUNCHED=1

start_everything
show_summary
wait_for_exit
exit $?
