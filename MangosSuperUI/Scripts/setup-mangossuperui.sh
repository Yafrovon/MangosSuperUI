#!/bin/bash
# ============================================================================
# MangosSuperUI — Setup Script
# 
# Auto-discovers VMaNGOS paths and database credentials from mangosd.conf,
# then generates server-config.json for MangosSuperUI.
#
# Run this AFTER completing Part 1 of the Install Guide and deploying
# MangosSuperUI to /opt/mangossuperui (Part 2, Steps 9-11).
#
# Usage:  sudo bash setup-mangossuperui.sh
# ============================================================================

set -e

# ── Colors ──
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
BOLD='\033[1m'
NC='\033[0m' # No Color

ok()   { echo -e "  ${GREEN}✓${NC} $1" >&2; }
warn() { echo -e "  ${YELLOW}⚠${NC} $1" >&2; }
fail() { echo -e "  ${RED}✗${NC} $1" >&2; }
info() { echo -e "  ${CYAN}→${NC} $1" >&2; }
header() { echo -e "\n${BOLD}$1${NC}" >&2; }

# ── Config ──
INSTALL_DIR="/opt/mangossuperui"
CONFIG_FILE="$INSTALL_DIR/server-config.json"

# ============================================================================
header "MangosSuperUI Setup"
echo "This script will auto-discover your VMaNGOS configuration and generate" >&2
echo "server-config.json for MangosSuperUI." >&2
# ============================================================================

# ── Step 1: Find mangosd.conf ──
header "Step 1: Locating mangosd.conf"

CONF_CANDIDATES=$(find / -name "mangosd.conf" -type f 2>/dev/null || true)
CONF_COUNT=$(echo "$CONF_CANDIDATES" | grep -c "." 2>/dev/null || echo 0)

if [ "$CONF_COUNT" -eq 0 ]; then
    fail "Could not find mangosd.conf anywhere on this system."
    echo "  Make sure VMaNGOS is installed and mangosd.conf exists." >&2
    exit 1
elif [ "$CONF_COUNT" -eq 1 ]; then
    MANGOSD_CONF="$CONF_CANDIDATES"
    ok "Found: $MANGOSD_CONF"
else
    echo "  Found multiple mangosd.conf files:" >&2
    i=1
    while IFS= read -r line; do
        echo "    [$i] $line" >&2
        i=$((i+1))
    done <<< "$CONF_CANDIDATES"
    echo "" >&2
    read -p "  Which one? [1-$CONF_COUNT]: " CONF_CHOICE
    MANGOSD_CONF=$(echo "$CONF_CANDIDATES" | sed -n "${CONF_CHOICE}p")
    if [ -z "$MANGOSD_CONF" ]; then
        fail "Invalid selection."
        exit 1
    fi
    ok "Selected: $MANGOSD_CONF"
fi

# ── Helper: read a value from mangosd.conf ──
# Handles: Key = Value, Key = "Value", Key.Sub = "Value"
conf_get() {
    local key="$1"
    # Escape dots for grep regex
    local escaped_key=$(echo "$key" | sed 's/\./\\./g')
    local val
    val=$(grep -E "^${escaped_key}\s*=" "$MANGOSD_CONF" | head -1 | sed "s/^.*=\s*//" | sed 's/^[[:space:]]*//' | sed 's/^"//' | sed 's/"$//' | xargs)
    echo "$val"
}

# ── Step 2: Parse database connections ──
header "Step 2: Reading database connections from mangosd.conf"

# VMaNGOS format: WorldDatabase.Info = "host;port;user;pass;db"
parse_db_conn() {
    local raw="$1"
    local label="$2"
    if [ -z "$raw" ]; then
        warn "$label: not found in mangosd.conf"
        # Return empty string on stdout, non-zero exit
        echo ""
        return 1
    fi
    local host port user pass db
    host=$(echo "$raw" | cut -d';' -f1)
    port=$(echo "$raw" | cut -d';' -f2)
    user=$(echo "$raw" | cut -d';' -f3)
    pass=$(echo "$raw" | cut -d';' -f4)
    db=$(echo "$raw" | cut -d';' -f5)
    ok "$label: $user@$host:$port/$db"
    # ONLY the connection string goes to stdout — all messages go to stderr
    echo "Server=$host;Port=$port;Database=$db;User=$user;Password=$pass;"
}

WORLD_RAW=$(conf_get "WorldDatabase.Info")
CHAR_RAW=$(conf_get "CharacterDatabase.Info")
LOGIN_RAW=$(conf_get "LoginDatabase.Info")
LOGS_RAW=$(conf_get "LogsDatabase.Info")

CONN_MANGOS=$(parse_db_conn "$WORLD_RAW" "World (mangos)") || true
CONN_CHARACTERS=$(parse_db_conn "$CHAR_RAW" "Characters") || true
CONN_REALMD=$(parse_db_conn "$LOGIN_RAW" "Realmd") || true
CONN_LOGS=$(parse_db_conn "$LOGS_RAW" "Logs") || true

# Admin DB uses the same host/port/user/pass as World, but database = vmangos_admin
if [ -n "$WORLD_RAW" ]; then
    ADMIN_HOST=$(echo "$WORLD_RAW" | cut -d';' -f1)
    ADMIN_PORT=$(echo "$WORLD_RAW" | cut -d';' -f2)
    ADMIN_USER=$(echo "$WORLD_RAW" | cut -d';' -f3)
    ADMIN_PASS=$(echo "$WORLD_RAW" | cut -d';' -f4)
    CONN_ADMIN="Server=$ADMIN_HOST;Port=$ADMIN_PORT;Database=vmangos_admin;User=$ADMIN_USER;Password=$ADMIN_PASS;"
    ok "Admin: $ADMIN_USER@$ADMIN_HOST:$ADMIN_PORT/vmangos_admin (auto-created on first boot)"
else
    CONN_ADMIN=""
    warn "Admin: could not derive (World DB not found)"
fi

# ── Step 3: Parse VMaNGOS paths ──
header "Step 3: Discovering VMaNGOS paths"

CONFIG_DIR=$(dirname "$MANGOSD_CONF")
ok "Config directory: $CONFIG_DIR"

# Find bin directory — prefer paths containing /run/ over /build/
BIN_DIR=""
MANGOSD_BINARY_NAME="mangosd"
ALL_BINS=$(find / \( -name "mangosd" -o -name "mangosd-main" \) -type f -executable 2>/dev/null || true)

if [ -n "$ALL_BINS" ]; then
    # Prefer a path containing /run/ over /build/
    RUN_BIN=$(echo "$ALL_BINS" | grep "/run/" | head -1)
    if [ -n "$RUN_BIN" ]; then
        BIN_DIR=$(dirname "$RUN_BIN")
        MANGOSD_BINARY_NAME=$(basename "$RUN_BIN")
    else
        FIRST_BIN=$(echo "$ALL_BINS" | head -1)
        BIN_DIR=$(dirname "$FIRST_BIN")
        MANGOSD_BINARY_NAME=$(basename "$FIRST_BIN")
    fi
    ok "Bin directory: $BIN_DIR"
    ok "mangosd binary: $MANGOSD_BINARY_NAME"
else
    warn "Could not find mangosd binary"
fi

# Find realmd binary name
REALMD_BINARY_NAME="realmd"
if [ -n "$BIN_DIR" ]; then
    if [ -f "$BIN_DIR/realmd-main" ] && [ -x "$BIN_DIR/realmd-main" ]; then
        REALMD_BINARY_NAME="realmd-main"
    fi
    ok "realmd binary: $REALMD_BINARY_NAME"
fi

# Process names — the binary filename (e.g. "mangosd") can differ from what the
# kernel reports as the process comm name (e.g. "mangosd-main"). MangosSuperUI's
# ProcessManagerService matches against comm, so we must get the real value.
#
# Strategy:
#   1. If the process is running, read /proc/PID/comm (authoritative).
#      Use pgrep -f with the full bin path to avoid exact-name mismatches.
#   2. If not running, read the comm name from the ELF binary itself —
#      Linux uses the first 15 chars of the filename passed to execve(),
#      but for binaries the "comm" is typically baked into prctl(PR_SET_NAME)
#      at startup. The most reliable offline method is to check if the binary
#      is a symlink (comm = symlink target name) or just use the filename.
#      As a pragmatic fallback, we also try briefly running the binary with
#      --version or similar, but VMaNGOS binaries don't support that, so
#      we simply scan for known patterns.

detect_process_name() {
    local bin_dir="$1"
    local binary_name="$2"
    local label="$3"
    local full_path="$bin_dir/$binary_name"
    local pid comm

    # Method 1: Check running process via full path (handles name mismatches)
    pid=$(pgrep -f "$full_path" 2>/dev/null | head -1 || true)
    if [ -n "$pid" ] && [ -f "/proc/$pid/comm" ]; then
        comm=$(cat "/proc/$pid/comm")
        ok "$label process name (live): $comm"
        echo "$comm"
        return
    fi

    # Method 2: Check running process via any matching binary name pattern
    # e.g. pgrep -f "mangosd" might catch "mangosd-main" started from elsewhere
    pid=$(pgrep -f "/${binary_name}" 2>/dev/null | head -1 || true)
    if [ -n "$pid" ] && [ -f "/proc/$pid/comm" ]; then
        comm=$(cat "/proc/$pid/comm")
        ok "$label process name (live): $comm"
        echo "$comm"
        return
    fi

    # Method 3: If the binary is a symlink, the real filename is the comm name
    if [ -L "$full_path" ]; then
        comm=$(basename "$(readlink -f "$full_path")")
        ok "$label process name (from symlink): $comm"
        echo "$comm"
        return
    fi

    # Method 4: Check if the ELF binary embeds a different name via
    # reading the .rodata or using strings — but this is fragile.
    # Instead, check for the "-main" variant which is a common VMaNGOS
    # build pattern: the binary is named "mangosd" but sets PR_SET_NAME
    # to "mangosd-main" at startup.
    # We can detect this by reading the binary for the prctl name string.
    local prctl_name
    prctl_name=$(strings "$full_path" 2>/dev/null | grep -E "^${binary_name}-main$" | head -1 || true)
    if [ -n "$prctl_name" ]; then
        ok "$label process name (from binary strings): $prctl_name"
        echo "$prctl_name"
        return
    fi

    # Fallback: use binary filename as-is
    info "$label not running — using binary name as process name: $binary_name"
    echo "$binary_name"
}

MANGOSD_PROCESS=$(detect_process_name "$BIN_DIR" "$MANGOSD_BINARY_NAME" "mangosd")
REALMD_PROCESS=$(detect_process_name "$BIN_DIR" "$REALMD_BINARY_NAME" "realmd")

# DataDir → DBC and Maps paths
DATA_DIR=$(conf_get "DataDir")
if [ -n "$DATA_DIR" ]; then
    # DataDir might be relative — resolve against bin dir
    if [[ "$DATA_DIR" != /* ]]; then
        if [ -n "$BIN_DIR" ]; then
            DATA_DIR="$BIN_DIR/$DATA_DIR"
        fi
    fi
    # Normalize path
    DATA_DIR=$(cd "$DATA_DIR" 2>/dev/null && pwd || echo "$DATA_DIR")
    ok "DataDir: $DATA_DIR"
else
    warn "DataDir not found in mangosd.conf"
fi

DBC_PATH=""
if [ -n "$DATA_DIR" ] && [ -d "$DATA_DIR/5875/dbc" ]; then
    DBC_PATH="$DATA_DIR/5875/dbc"
    DBC_COUNT=$(ls "$DBC_PATH"/*.dbc 2>/dev/null | wc -l)
    ok "DBC path: $DBC_PATH ($DBC_COUNT .dbc files)"
elif [ -n "$DATA_DIR" ] && [ -d "$DATA_DIR/dbc" ]; then
    DBC_PATH="$DATA_DIR/dbc"
    DBC_COUNT=$(ls "$DBC_PATH"/*.dbc 2>/dev/null | wc -l)
    ok "DBC path: $DBC_PATH ($DBC_COUNT .dbc files)"
else
    warn "DBC directory not found"
fi

MAPS_PATH=""
if [ -n "$DATA_DIR" ] && [ -d "$DATA_DIR/maps" ]; then
    MAPS_PATH="$DATA_DIR/maps"
    MAP_COUNT=$(ls "$MAPS_PATH"/*.map 2>/dev/null | wc -l)
    ok "Maps path: $MAPS_PATH ($MAP_COUNT .map files)"
else
    warn "Maps directory not found (World Map Z-resolution will be unavailable)"
fi

# Log directory — look for .log files in bin dir
LOG_DIR=""
if [ -n "$BIN_DIR" ] && ls "$BIN_DIR"/*.log &>/dev/null; then
    LOG_DIR="$BIN_DIR"
    ok "Log directory: $LOG_DIR"
else
    warn "Log directory not found"
fi

# ── Step 4: RA settings ──
header "Step 4: Remote Access (RA) configuration"

RA_PORT=$(conf_get "Ra.Port")
if [ -z "$RA_PORT" ]; then
    RA_PORT="3443"
    info "Ra.Port not found — using default: 3443"
else
    ok "RA port: $RA_PORT"
fi

RA_HOST="127.0.0.1"
ok "RA host: $RA_HOST (local)"

echo "" >&2
echo -e "  ${BOLD}Enter your RA credentials${NC} (the account you created in Part 1, Step 5):" >&2
echo "" >&2
read -p "  RA Username: " RA_USERNAME
read -sp "  RA Password: " RA_PASSWORD
echo "" >&2

if [ -z "$RA_USERNAME" ] || [ -z "$RA_PASSWORD" ]; then
    fail "RA username and password are required."
    exit 1
fi
ok "RA credentials set"

# ── Step 5: Test RA connectivity ──
header "Step 5: Testing RA connectivity"

if timeout 3 bash -c "echo > /dev/tcp/$RA_HOST/$RA_PORT" 2>/dev/null; then
    ok "RA port $RA_PORT is open"
else
    warn "Could not connect to RA on $RA_HOST:$RA_PORT — mangosd may not be running"
    info "This is OK if you haven't started mangosd yet."
fi

# ── Step 6: Check MangosSuperUI install ──
header "Step 6: Verifying MangosSuperUI installation"

if [ ! -f "$INSTALL_DIR/MangosSuperUI.dll" ]; then
    fail "MangosSuperUI.dll not found in $INSTALL_DIR"
    echo "  Deploy MangosSuperUI before running this script." >&2
    exit 1
fi
ok "MangosSuperUI.dll found in $INSTALL_DIR"

# ── Step 7: Generate server-config.json ──
header "Step 7: Generating server-config.json"

cat > /tmp/mangossuperui-config-generated.json << JSONEOF
{
  "ConnectionStrings": {
    "Mangos": "$CONN_MANGOS",
    "Characters": "$CONN_CHARACTERS",
    "Realmd": "$CONN_REALMD",
    "Logs": "$CONN_LOGS",
    "Admin": "$CONN_ADMIN"
  },
  "Vmangos": {
    "BinDirectory": "$BIN_DIR",
    "LogDirectory": "$LOG_DIR",
    "ConfigDirectory": "$CONFIG_DIR",
    "MangosdProcess": "$MANGOSD_PROCESS",
    "RealmdProcess": "$REALMD_PROCESS",
    "MangosdConfPath": "$MANGOSD_CONF",
    "LogsDir": "$LOG_DIR",
    "DbcPath": "$DBC_PATH",
    "MapsDataPath": "$MAPS_PATH"
  },
  "RemoteAccess": {
    "Host": "$RA_HOST",
    "Port": $RA_PORT,
    "Username": "$RA_USERNAME",
    "Password": "$RA_PASSWORD",
    "ReconnectDelayMs": 3000,
    "CommandTimeoutMs": 5000
  }
}
JSONEOF

# ── Show summary ──
header "Configuration Summary"
echo "" >&2
echo -e "  ${BOLD}Database Connections:${NC}" >&2
echo "    Mangos:      $CONN_MANGOS" >&2
echo "    Characters:  $CONN_CHARACTERS" >&2
echo "    Realmd:      $CONN_REALMD" >&2
echo "    Logs:        $CONN_LOGS" >&2
echo "    Admin:       $CONN_ADMIN" >&2
echo "" >&2
echo -e "  ${BOLD}VMaNGOS Paths:${NC}" >&2
echo "    Bin Dir:     $BIN_DIR" >&2
echo "    Log Dir:     $LOG_DIR" >&2
echo "    Config Dir:  $CONFIG_DIR" >&2
echo "    Conf Path:   $MANGOSD_CONF" >&2
echo "    DBC Path:    $DBC_PATH" >&2
echo "    Maps Path:   $MAPS_PATH" >&2
echo "" >&2
echo -e "  ${BOLD}Process Names:${NC}" >&2
echo "    mangosd:     $MANGOSD_PROCESS" >&2
echo "    realmd:      $REALMD_PROCESS" >&2
echo "" >&2
echo -e "  ${BOLD}Remote Access:${NC}" >&2
echo "    Host:        $RA_HOST:$RA_PORT" >&2
echo "    Username:    $RA_USERNAME" >&2
echo "    Password:    ********" >&2
echo "" >&2

# ── Confirm and write ──
if [ -f "$CONFIG_FILE" ]; then
    warn "server-config.json already exists at $CONFIG_FILE"
    read -p "  Overwrite? [y/N]: " OVERWRITE
    if [[ ! "$OVERWRITE" =~ ^[Yy]$ ]]; then
        info "Saved generated config to /tmp/mangossuperui-config-generated.json instead"
        echo "  You can manually copy it: cp /tmp/mangossuperui-config-generated.json $CONFIG_FILE" >&2
        exit 0
    fi
fi

cp /tmp/mangossuperui-config-generated.json "$CONFIG_FILE"
ok "Written to $CONFIG_FILE"

# ── Step 8: Restart MangosSuperUI ──
header "Step 8: Restarting MangosSuperUI"

if systemctl is-active mangossuperui &>/dev/null; then
    sudo systemctl restart mangossuperui
    sleep 3
    if systemctl is-active mangossuperui &>/dev/null; then
        ok "MangosSuperUI restarted successfully"
    else
        fail "MangosSuperUI failed to start after restart"
        echo "  Check: sudo journalctl -u mangossuperui --no-pager -n 30" >&2
        exit 1
    fi
else
    info "MangosSuperUI service not running — start it with: sudo systemctl start mangossuperui"
fi

# ── Done ──
header "Setup Complete!"
echo "" >&2
echo "  Open your browser to http://$(hostname -I | awk '{print $1}'):5000" >&2
echo "  The Dashboard should show green indicators for mangosd, realmd, RA, and all databases." >&2
echo "" >&2
echo "  If anything is red, check the Troubleshooting section of the Install Guide." >&2
echo "" >&2