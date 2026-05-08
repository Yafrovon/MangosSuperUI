#!/bin/bash
# ============================================================================
# MangosSuperUI — Full Installation Validator & Directory Setup
#
# Run AFTER setup-mangossuperui.sh has generated server-config.json.
# This script:
#   1. Reads server-config.json to find all configured paths
#   2. Validates every path exists and has expected contents
#   3. Creates missing directories where safe to do so
#   4. Reports what assets are present vs missing
#   5. Tells you exactly what still needs to be done
#
# Usage:  sudo bash validate-mangossuperui.sh
# ============================================================================

set -e

# ── Colors ──
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
BOLD='\033[1m'
DIM='\033[2m'
NC='\033[0m'

ok()      { echo -e "  ${GREEN}✓${NC} $1"; }
warn()    { echo -e "  ${YELLOW}⚠${NC} $1"; }
fail()    { echo -e "  ${RED}✗${NC} $1"; }
info()    { echo -e "  ${CYAN}→${NC} $1"; }
header()  { echo -e "\n${BOLD}═══ $1 ═══${NC}"; }
subhead() { echo -e "\n${BOLD}  $1${NC}"; }
dim()     { echo -e "  ${DIM}$1${NC}"; }

PASS=0
WARN=0
FAIL=0

pass() { ok "$1"; ((PASS++)); }
warning() { warn "$1"; ((WARN++)); }
failure() { fail "$1"; ((FAIL++)); }

# ── Config ──
INSTALL_DIR="/opt/mangossuperui"
CONFIG_FILE="$INSTALL_DIR/server-config.json"
WWWROOT="$INSTALL_DIR/wwwroot"

# ── Check prerequisites ──
header "MangosSuperUI Installation Validator"
echo ""
echo "  This script validates your complete MangosSuperUI installation"
echo "  and creates any missing directories."
echo ""

if ! command -v jq &>/dev/null; then
    fail "jq is required but not installed."
    info "Install it: sudo apt install jq"
    exit 1
fi

# ── Load config ──
header "1. Configuration Files"

if [ -f "$CONFIG_FILE" ]; then
    pass "server-config.json found at $CONFIG_FILE"
    CONFIG_SOURCE="$CONFIG_FILE"
elif [ -f "$INSTALL_DIR/appsettings.json" ]; then
    warning "No server-config.json — using appsettings.json defaults"
    CONFIG_SOURCE="$INSTALL_DIR/appsettings.json"
else
    failure "No configuration file found"
    echo "    Run setup-mangossuperui.sh first, or configure through the Settings page."
    exit 1
fi

# Helper: read a JSON value (handles both config file structures)
cfg() {
    local key="$1"
    local val
    val=$(jq -r "$key // empty" "$CONFIG_SOURCE" 2>/dev/null)
    echo "$val"
}

# ============================================================================
header "2. Database Connections"
# ============================================================================

check_db_conn() {
    local label="$1"
    local conn="$2"
    
    if [ -z "$conn" ]; then
        failure "$label: not configured"
        return
    fi
    
    # Parse connection string
    local host=$(echo "$conn" | grep -oP 'Server=\K[^;]+')
    local port=$(echo "$conn" | grep -oP 'Port=\K[^;]+')
    local db=$(echo "$conn" | grep -oP 'Database=\K[^;]+')
    local user=$(echo "$conn" | grep -oP 'User=\K[^;]+')
    
    if [ -z "$host" ] || [ -z "$db" ]; then
        failure "$label: malformed connection string"
        return
    fi
    
    # Test connection
    if mysql -h "$host" -P "${port:-3306}" -u "$user" -p"$(echo "$conn" | grep -oP 'Password=\K[^;]+')" "$db" -e "SELECT 1" &>/dev/null; then
        local count=$(mysql -h "$host" -P "${port:-3306}" -u "$user" -p"$(echo "$conn" | grep -oP 'Password=\K[^;]+')" "$db" -N -e "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA='$db'" 2>/dev/null)
        pass "$label: connected ($db — $count tables)"
    else
        failure "$label: connection failed ($user@$host:${port:-3306}/$db)"
    fi
}

CONN_MANGOS=$(cfg '.connectionStrings.mangos // .ConnectionStrings.Mangos')
CONN_CHARS=$(cfg '.connectionStrings.characters // .ConnectionStrings.Characters')
CONN_REALMD=$(cfg '.connectionStrings.realmd // .ConnectionStrings.Realmd')
CONN_LOGS=$(cfg '.connectionStrings.logs // .ConnectionStrings.Logs')
CONN_ADMIN=$(cfg '.connectionStrings.admin // .ConnectionStrings.Admin')

check_db_conn "Mangos (World)" "$CONN_MANGOS"
check_db_conn "Characters" "$CONN_CHARS"
check_db_conn "Realmd" "$CONN_REALMD"
check_db_conn "Logs" "$CONN_LOGS"
check_db_conn "Admin" "$CONN_ADMIN"

# ============================================================================
header "3. VMaNGOS Server Paths"
# ============================================================================

# Read paths from config (handle both camelCase and PascalCase)
BIN_DIR=$(cfg '.vmangos.binDirectory // .Vmangos.BinDirectory')
LOG_DIR=$(cfg '.vmangos.logDirectory // .Vmangos.LogDirectory')
CONF_DIR=$(cfg '.vmangos.configDirectory // .Vmangos.ConfigDirectory')
CONF_PATH=$(cfg '.vmangos.mangosdConfPath // .Vmangos.MangosdConfPath')
LOGS_DIR=$(cfg '.vmangos.logsDir // .Vmangos.LogsDir')
DBC_PATH=$(cfg '.vmangos.dbcPath // .Vmangos.DbcPath')
MAPS_PATH=$(cfg '.vmangos.mapsDataPath // .Vmangos.MapsDataPath')
BACKUP_DIR=$(cfg '.vmangos.backupDirectory // .Vmangos.BackupDirectory')
SOURCE_PATH=$(cfg '.vmangos.vmangosSourcePath // .Vmangos.VmangosSourcePath')
SQL_PATH=$(cfg '.vmangos.vmangosSqlPath // .Vmangos.VmangosSqlPath')

check_dir() {
    local label="$1"
    local path="$2"
    local required="$3"  # "required" or "optional"
    local create="$4"    # "create" to auto-create if missing
    
    if [ -z "$path" ]; then
        if [ "$required" = "required" ]; then
            failure "$label: not configured"
        else
            warning "$label: not configured (optional)"
        fi
        return 1
    fi
    
    if [ -d "$path" ]; then
        pass "$label: $path"
        return 0
    else
        if [ "$create" = "create" ]; then
            mkdir -p "$path" 2>/dev/null
            if [ -d "$path" ]; then
                pass "$label: created $path"
                return 0
            else
                failure "$label: failed to create $path"
                return 1
            fi
        else
            if [ "$required" = "required" ]; then
                failure "$label: $path does not exist"
            else
                warning "$label: $path does not exist (optional)"
            fi
            return 1
        fi
    fi
}

check_file() {
    local label="$1"
    local path="$2"
    local required="$3"
    
    if [ -z "$path" ]; then
        if [ "$required" = "required" ]; then
            failure "$label: not configured"
        else
            warning "$label: not configured (optional)"
        fi
        return 1
    fi
    
    if [ -f "$path" ]; then
        pass "$label: $path"
        return 0
    else
        if [ "$required" = "required" ]; then
            failure "$label: $path not found"
        else
            warning "$label: $path not found (optional)"
        fi
        return 1
    fi
}

subhead "Core Server Directories"
check_dir "Bin directory" "$BIN_DIR" "required"
check_dir "Log directory" "$LOG_DIR" "optional"
check_dir "Config directory" "$CONF_DIR" "required"
check_file "mangosd.conf" "$CONF_PATH" "required"

subhead "Server Data"
if check_dir "DBC directory" "$DBC_PATH" "required"; then
    DBC_COUNT=$(ls "$DBC_PATH"/*.dbc 2>/dev/null | wc -l)
    if [ "$DBC_COUNT" -gt 50 ]; then
        pass "  DBC files: $DBC_COUNT .dbc files found"
        # Check critical DBCs
        for dbc in Spell.dbc SpellIcon.dbc ItemDisplayInfo.dbc SpellVisual.dbc SpellVisualKit.dbc SpellVisualEffectName.dbc SkillLineAbility.dbc GameObjectDisplayInfo.dbc; do
            if [ -f "$DBC_PATH/$dbc" ]; then
                dim "  ✓ $dbc"
            else
                warning "  Missing critical DBC: $dbc"
            fi
        done
    else
        warning "  Only $DBC_COUNT .dbc files — expected 80+. Check the path."
    fi
fi

if check_dir "Maps directory" "$MAPS_PATH" "required"; then
    MAP_COUNT=$(ls "$MAPS_PATH"/*.map 2>/dev/null | wc -l)
    if [ "$MAP_COUNT" -gt 100 ]; then
        pass "  Map files: $MAP_COUNT .map files (World Map Z-resolution available)"
    else
        warning "  Only $MAP_COUNT .map files — expected 4000+. World Map Z will be inaccurate."
    fi
fi

subhead "Backup & Source Paths"
check_dir "Backup directory" "$BACKUP_DIR" "optional" "create"
check_dir "VMaNGOS source" "$SOURCE_PATH" "optional"
check_dir "VMaNGOS SQL" "$SQL_PATH" "optional"

# ============================================================================
header "4. Spell Creator Paths"
# ============================================================================

CLIENT_M2=$(cfg '.vmangos.clientM2Path // .Vmangos.ClientM2Path')
CLIENT_DATA=$(cfg '.vmangos.clientDataPath // .Vmangos.ClientDataPath')
PATCH_OUT=$(cfg '.vmangos.patchOutputPath // .Vmangos.PatchOutputPath')
RAW_BLP=$(cfg '.spellCreator.rawBlpPath // .SpellCreator.RawBlpPath')
SC_DATA=$(cfg '.spellCreator.dataPath // .SpellCreator.DataPath')

subhead "Client Asset Directories (for SpellCreator + WorldViewer)"

if check_dir "Pre-extracted M2 files" "$CLIENT_M2" "optional"; then
    M2_COUNT=$(find "$CLIENT_M2" -name "*.m2" -o -name "*.M2" 2>/dev/null | wc -l)
    pass "  M2 files: $M2_COUNT found"
    # Check for Spells subdirectory specifically
    if [ -d "$CLIENT_M2/Spells" ] || [ -d "$CLIENT_M2/spells" ]; then
        SPELL_M2=$(find "$CLIENT_M2/Spells" "$CLIENT_M2/spells" -name "*.m2" -o -name "*.M2" 2>/dev/null | wc -l)
        dim "  Including $SPELL_M2 spell effect M2s"
    fi
else
    info "  Without M2 files, SpellCreator cannot read/patch particle effects."
    info "  Extract from patch*.MPQ using Ladik's MPQ Editor (exclude model.MPQ)."
fi

if check_dir "Client Data (MPQs)" "$CLIENT_DATA" "optional"; then
    MPQ_COUNT=$(ls "$CLIENT_DATA"/*.MPQ "$CLIENT_DATA"/*.mpq 2>/dev/null | wc -l)
    if [ "$MPQ_COUNT" -gt 0 ]; then
        pass "  MPQ archives: $MPQ_COUNT found"
        dim "  Used by: SpellCreator (M2 fallback), WorldViewer (terrain/WMOs/doodads)"
    else
        warning "  No .MPQ files found in $CLIENT_DATA"
    fi
else
    info "  Without client MPQs, WorldViewer terrain/WMOs and SpellCreator M2 fallback won't work."
    info "  Copy your WoW 1.12.1 client's Data/ folder to the server."
fi

if [ -n "$PATCH_OUT" ]; then
    if check_dir "Patch output" "$PATCH_OUT" "optional" "create"; then
        dim "  patch-3.MPQ (SpellCreator) and patch-Z.MPQ (WorldViewer) are built here"
    fi
else
    warning "Patch output path: not configured"
    info "  Default: /opt/mangossuperui/wwwroot/patches"
    mkdir -p "$WWWROOT/patches" 2>/dev/null && pass "  Created $WWWROOT/patches" || true
fi

subhead "Vanilla BLP Reference (for SpellCreator texture matching)"

if check_dir "Raw BLP directory" "$RAW_BLP" "optional"; then
    BLP_COUNT=$(find "$RAW_BLP" -name "*.blp" -o -name "*.BLP" 2>/dev/null | wc -l)
    if [ "$BLP_COUNT" -gt 0 ]; then
        pass "  BLP files: $BLP_COUNT found"
        dim "  VanillaBlpService reads actual BLP headers for format/dimension matching"
    else
        warning "  No .blp files found in $RAW_BLP"
    fi
else
    info "  Without vanilla BLPs, custom textures default to 512×512 DXT3."
    info "  Extract from patch*.MPQ → flatten into a single directory with lowercase names."
fi

subhead "SpellCreator Data Files"

if check_dir "SpellCreator data" "$SC_DATA" "optional" "create"; then
    for f in m2_texture_graph.json blp_to_m2_reverse.json; do
        if [ -f "$SC_DATA/$f" ]; then
            dim "  ✓ $f"
        else
            warning "  Missing: $f (texture reference lookup will be unavailable)"
        fi
    done
fi

# ============================================================================
header "5. WWWRoot Static Assets"
# ============================================================================

subhead "Directories"

# These are the directories the Extractor outputs into
ASSET_DIRS=(
    "icons:Icon PNGs:required:2600"
    "models:Game Object GLBs:recommended:900"
    "item_models:Item Model GLBs:recommended:100"
    "minimap:Minimap Tile PNGs:recommended:0"
)

for entry in "${ASSET_DIRS[@]}"; do
    IFS=':' read -r dir desc importance expected_min <<< "$entry"
    
    full_path="$WWWROOT/$dir"
    
    if [ -d "$full_path" ]; then
        case "$dir" in
            "icons")
                count=$(ls "$full_path"/*.png 2>/dev/null | wc -l)
                ;;
            "models"|"item_models")
                count=$(ls "$full_path"/*.glb 2>/dev/null | wc -l)
                ;;
            "minimap")
                count=$(find "$full_path" -name "*.png" 2>/dev/null | wc -l)
                ;;
        esac
        
        if [ "$count" -gt "$expected_min" ]; then
            pass "$desc ($dir/): $count files"
        elif [ "$count" -gt 0 ]; then
            warning "$desc ($dir/): $count files (expected $expected_min+)"
        else
            if [ "$importance" = "required" ]; then
                failure "$desc ($dir/): empty"
            else
                warning "$desc ($dir/): empty"
            fi
        fi
    else
        mkdir -p "$full_path" 2>/dev/null
        if [ "$importance" = "required" ]; then
            failure "$desc ($dir/): directory created, but no files — run Extractor"
        else
            warning "$desc ($dir/): directory created, but no files — run Extractor"
        fi
    fi
done

subhead "Icon Case Check"
if [ -d "$WWWROOT/icons" ]; then
    UPPER_COUNT=$(ls "$WWWROOT/icons/" 2>/dev/null | grep -c '[A-Z]')
    if [ "$UPPER_COUNT" -gt 0 ]; then
        warning "$UPPER_COUNT icon files have uppercase characters — they won't load on Linux"
        info "Fix: cd $WWWROOT/icons && for f in *; do mv \"\$f\" \"\$(echo \"\$f\" | tr '[:upper:]' '[:lower:]')\" 2>/dev/null; done"
    else
        ICON_TOTAL=$(ls "$WWWROOT/icons/"*.png 2>/dev/null | wc -l)
        if [ "$ICON_TOTAL" -gt 0 ]; then
            pass "All icon filenames are lowercase"
        fi
    fi
fi

subhead "Three.js Libraries"
if [ -f "$WWWROOT/lib/three/three.min.js" ]; then
    pass "three.min.js present"
else
    warning "three.min.js missing ($WWWROOT/lib/three/three.min.js)"
    info "  Download Three.js r128 and place three.min.js here"
fi

if [ -f "$WWWROOT/lib/three/OrbitControls.js" ]; then
    pass "OrbitControls.js present"
else
    warning "OrbitControls.js missing ($WWWROOT/lib/three/OrbitControls.js)"
fi

subhead "Other Static Data"
for f in "data/commands.json" "data/config-metadata.json" "data/instance-bosses.json" "data/curated-relationships.json"; do
    if [ -f "$WWWROOT/$f" ]; then
        dim "  ✓ $f"
    else
        warning "  Missing: wwwroot/$f"
    fi
done

# ============================================================================
header "6. Remote Access (RA)"
# ============================================================================

RA_HOST=$(cfg '.remoteAccess.host // .RemoteAccess.Host')
RA_PORT=$(cfg '.remoteAccess.port // .RemoteAccess.Port')
RA_USER=$(cfg '.remoteAccess.username // .RemoteAccess.Username')

if [ -n "$RA_HOST" ] && [ -n "$RA_PORT" ]; then
    if timeout 3 bash -c "echo > /dev/tcp/$RA_HOST/$RA_PORT" 2>/dev/null; then
        pass "RA port open on $RA_HOST:$RA_PORT"
        if [ -n "$RA_USER" ] && [ "$RA_USER" != "ADMIN" ] && [ "$RA_USER" != "CHANGE_ME" ]; then
            pass "RA username configured: $RA_USER"
        else
            warning "RA username appears to be default/placeholder: '$RA_USER'"
        fi
    else
        warning "RA port $RA_PORT not reachable (mangosd may not be running)"
    fi
else
    warning "RA not configured"
fi

# ============================================================================
header "7. Process Status"
# ============================================================================

MANGOSD_PROC=$(cfg '.vmangos.mangosdProcess // .Vmangos.MangosdProcess')
REALMD_PROC=$(cfg '.vmangos.realmdProcess // .Vmangos.RealmdProcess')

check_process() {
    local label="$1"
    local proc_name="$2"
    
    if [ -z "$proc_name" ]; then
        warning "$label: process name not configured"
        return
    fi
    
    if pgrep -x "$proc_name" &>/dev/null; then
        local pid=$(pgrep -x "$proc_name" | head -1)
        local mem=$(ps -o rss= -p "$pid" 2>/dev/null | awk '{printf "%.0f MB", $1/1024}')
        pass "$label ($proc_name): running (PID $pid, $mem)"
    else
        warning "$label ($proc_name): not running"
    fi
}

check_process "World Server" "${MANGOSD_PROC:-mangosd}"
check_process "Auth Server" "${REALMD_PROC:-realmd}"

if systemctl is-active mangossuperui &>/dev/null; then
    pass "MangosSuperUI service: running"
else
    warning "MangosSuperUI service: not running"
fi

if systemctl is-active mariadb &>/dev/null || systemctl is-active mysql &>/dev/null; then
    pass "Database service: running"
else
    warning "Database service: not detected (mariadb/mysql)"
fi

# ============================================================================
header "8. AI Services (Optional)"
# ============================================================================

OLLAMA_URL=$(cfg '.spellCreator.ollama.baseUrl // .SpellCreator.Ollama.BaseUrl')
COMFY_NODES=$(cfg '[.spellCreator.comfyUI.nodes // .SpellCreator.ComfyUI.Nodes // [] | length]')

if [ -n "$OLLAMA_URL" ] && [ "$OLLAMA_URL" != "" ]; then
    if curl -s --max-time 3 "$OLLAMA_URL/api/tags" &>/dev/null; then
        MODEL_COUNT=$(curl -s --max-time 3 "$OLLAMA_URL/api/tags" 2>/dev/null | jq '.models | length' 2>/dev/null || echo "?")
        pass "Ollama: reachable at $OLLAMA_URL ($MODEL_COUNT models)"
    else
        warning "Ollama: configured ($OLLAMA_URL) but not reachable"
    fi
else
    dim "  Ollama: not configured (SpellCreator prompt generation + AiBot chat disabled)"
fi

if [ "$COMFY_NODES" -gt 0 ] 2>/dev/null; then
    dim "  ComfyUI: $COMFY_NODES node(s) configured (check pool status on Settings page)"
else
    dim "  ComfyUI: not configured (AI icon/texture generation disabled)"
fi

# ============================================================================
header "SUMMARY"
# ============================================================================

echo ""
echo -e "  ${GREEN}Passed:${NC}  $PASS"
echo -e "  ${YELLOW}Warnings:${NC} $WARN"
echo -e "  ${RED}Failed:${NC}  $FAIL"
echo ""

if [ "$FAIL" -eq 0 ] && [ "$WARN" -eq 0 ]; then
    echo -e "  ${GREEN}${BOLD}Everything looks perfect!${NC}"
elif [ "$FAIL" -eq 0 ]; then
    echo -e "  ${GREEN}${BOLD}Core installation is good.${NC} Warnings are for optional features."
else
    echo -e "  ${RED}${BOLD}$FAIL critical issue(s) need fixing.${NC}"
fi

echo ""

# ============================================================================
# Action items
# ============================================================================
if [ "$FAIL" -gt 0 ] || [ "$WARN" -gt 0 ]; then
    header "TODO — What To Do Next"
    echo ""
    
    # Check for empty asset dirs
    ICONS_COUNT=$(ls "$WWWROOT/icons/"*.png 2>/dev/null | wc -l)
    MODELS_COUNT=$(ls "$WWWROOT/models/"*.glb 2>/dev/null | wc -l)
    
    if [ "$ICONS_COUNT" -eq 0 ] || [ "$MODELS_COUNT" -eq 0 ]; then
        echo "  ${BOLD}Run the MangosSuperUI Extractor on your Windows PC:${NC}"
        echo "    1. Point it at your WoW 1.12.1 client Data/ folder"
        echo "    2. Extract: icons, models, item_models, minimap"
        echo "    3. SCP the output folders to $WWWROOT/"
        if [ "$ICONS_COUNT" -eq 0 ]; then
            echo "    4. Fix icon case: cd $WWWROOT/icons && for f in *; do mv \"\$f\" \"\$(echo \"\$f\" | tr '[:upper:]' '[:lower:]')\" 2>/dev/null; done"
        fi
        echo ""
    fi
    
    if [ -z "$CLIENT_DATA" ] || [ ! -d "$CLIENT_DATA" ]; then
        echo "  ${BOLD}Copy WoW 1.12.1 client Data/ to the server:${NC}"
        echo "    This enables the WorldViewer (terrain, WMOs, doodads) and SpellCreator M2 fallback."
        echo "    scp -r 'C:\\WoW\\Data' YOUR_USER@YOUR_SERVER:/home/YOUR_USER/wowclient/Data"
        echo "    Then set Vmangos:ClientDataPath in Settings."
        echo ""
    fi
    
    if [ -z "$CLIENT_M2" ] || [ ! -d "$CLIENT_M2" ]; then
        echo "  ${BOLD}Extract M2 files for SpellCreator (python mpyq):${NC}"
        echo "    pip3 install --user mpyq --break-system-packages"
        echo "    Then run the M2 extraction script (included below)."
        echo "    Extracts Spells/ and Particles/ M2+BLP files from model.MPQ + patch.MPQ"
        echo "    Set Vmangos:ClientM2Path in Settings to the output directory."
        echo ""
    fi
    
    if [ -z "$RAW_BLP" ] || [ ! -d "$RAW_BLP" ]; then
        echo "  ${BOLD}Extract vanilla BLPs for texture matching (python mpyq):${NC}"
        echo "    Extracts BLPs from texture.MPQ + model.MPQ + patch.MPQ"
        echo "    Flattens to lowercase filenames for VanillaBlpService lookup."
        echo "    Set SpellCreator:RawBlpPath in Settings to the output directory."
        echo ""
    fi
fi

# ============================================================================
# Section 9: Auto-Extract M2s + BLPs (if Client Data is available)
# ============================================================================

CLIENT_DATA_FOR_EXTRACT=$(cfg '.vmangos.clientDataPath // .Vmangos.ClientDataPath')

if [ -n "$CLIENT_DATA_FOR_EXTRACT" ] && [ -d "$CLIENT_DATA_FOR_EXTRACT" ]; then

    M2_TARGET=$(cfg '.vmangos.clientM2Path // .Vmangos.ClientM2Path')
    BLP_TARGET=$(cfg '.spellCreator.rawBlpPath // .SpellCreator.RawBlpPath')
    
    NEED_M2=false
    NEED_BLP=false
    
    if [ -n "$M2_TARGET" ]; then
        M2_FILE_COUNT=$(find "$M2_TARGET" -name "*.m2" -o -name "*.M2" 2>/dev/null | wc -l)
        [ "$M2_FILE_COUNT" -lt 10 ] && NEED_M2=true
    else
        NEED_M2=true
    fi
    
    if [ -n "$BLP_TARGET" ]; then
        BLP_FILE_COUNT=$(find "$BLP_TARGET" -name "*.blp" -o -name "*.BLP" 2>/dev/null | wc -l)
        [ "$BLP_FILE_COUNT" -lt 10 ] && NEED_BLP=true
    else
        NEED_BLP=true
    fi
    
    if $NEED_M2 || $NEED_BLP; then
        header "9. Auto-Extract SpellCreator Assets"
        echo ""
        echo "  Client Data found at: $CLIENT_DATA_FOR_EXTRACT"
        $NEED_M2 && echo "  M2 files:  MISSING or empty"
        $NEED_BLP && echo "  Raw BLPs:  MISSING or empty"
        echo ""
        
        if ! python3 -c "import mpyq" 2>/dev/null; then
            info "mpyq not installed. Install with:"
            echo "    pip3 install --user mpyq --break-system-packages"
            echo ""
            echo "  After installing mpyq, re-run this script to auto-extract."
            NEED_M2=false
            NEED_BLP=false
        else
            pass "mpyq Python module is installed"
        fi
        
        if $NEED_M2 || $NEED_BLP; then
            read -p "  Extract now? [y/N]: " DO_EXTRACT
            
            if [[ "$DO_EXTRACT" =~ ^[Yy]$ ]]; then
            
                # ── Extract M2s ──
                if $NEED_M2; then
                    DEFAULT_USER=$(logname 2>/dev/null || echo "${SUDO_USER:-$USER}")
                    M2_OUT="${M2_TARGET:-/home/$DEFAULT_USER/wowclient/m2}"
                    header "Extracting Spell M2 files → $M2_OUT"
                    
python3 - "$M2_OUT" "$CLIENT_DATA_FOR_EXTRACT" << 'PYEOF'
import mpyq, os, sys

m2_dir = sys.argv[1]
data_dir = sys.argv[2]

for mpq_name in ['model.MPQ', 'patch.MPQ', 'patch-2.MPQ']:
    mpq_path = os.path.join(data_dir, mpq_name)
    if not os.path.exists(mpq_path):
        print(f"  SKIP: {mpq_name} not found")
        continue
    archive = mpyq.MPQArchive(mpq_path)
    listfile = archive.read_file('(listfile)')
    if not listfile:
        print(f"  SKIP: {mpq_name} has no listfile")
        continue
    lines = listfile.decode('utf-8', errors='replace').split('\r\n')
    targets = [l for l in lines if l.lower().startswith(('spells\\', 'particles\\'))
               and (l.lower().endswith('.m2') or l.lower().endswith('.blp'))]
    print(f"  {mpq_name}: {len(targets)} spell/particle files")
    ok = 0
    for t in targets:
        data = archive.read_file(t)
        if data:
            rel = t.replace('\\', '/')
            out = os.path.join(m2_dir, rel)
            os.makedirs(os.path.dirname(out), exist_ok=True)
            with open(out, 'wb') as f:
                f.write(data)
            ok += 1
    print(f"  Extracted {ok}/{len(targets)}")

total = sum(1 for r, d, fs in os.walk(m2_dir) for f in fs)
print(f"\n  Total files in {m2_dir}: {total}")
PYEOF
                    
                    FINAL_M2=$(find "$M2_OUT" -name "*.m2" 2>/dev/null | wc -l)
                    if [ "$FINAL_M2" -gt 0 ]; then
                        pass "Extracted $FINAL_M2 M2 files to $M2_OUT"
                        info "Set Vmangos:ClientM2Path = $M2_OUT in Settings"
                    else
                        failure "M2 extraction produced no files"
                    fi
                fi
                
                # ── Extract BLPs ──
                if $NEED_BLP; then
                    DEFAULT_USER=$(logname 2>/dev/null || echo "${SUDO_USER:-$USER}")
                    BLP_OUT="${BLP_TARGET:-/home/$DEFAULT_USER/wowclient/rawblps/m2_blps}"
                    header "Extracting Vanilla BLPs → $BLP_OUT"
                    
python3 - "$BLP_OUT" "$CLIENT_DATA_FOR_EXTRACT" << 'PYEOF'
import mpyq, os, sys

blp_dir = sys.argv[1]
data_dir = sys.argv[2]
os.makedirs(blp_dir, exist_ok=True)

for mpq_name in ['texture.MPQ', 'model.MPQ', 'patch.MPQ', 'patch-2.MPQ']:
    mpq_path = os.path.join(data_dir, mpq_name)
    if not os.path.exists(mpq_path):
        print(f"  SKIP: {mpq_name} not found")
        continue
    archive = mpyq.MPQArchive(mpq_path)
    listfile = archive.read_file('(listfile)')
    if not listfile:
        print(f"  SKIP: {mpq_name} has no listfile")
        continue
    lines = listfile.decode('utf-8', errors='replace').split('\r\n')
    blp_targets = [l for l in lines if l.lower().endswith('.blp') and
                   any(l.lower().startswith(p) for p in ('spells\\', 'particles\\', 'item\\objectcomponents\\'))]
    print(f"  {mpq_name}: {len(blp_targets)} BLP files")
    ok = 0
    for t in blp_targets:
        data = archive.read_file(t)
        if data:
            fname = os.path.basename(t).lower()
            with open(os.path.join(blp_dir, fname), 'wb') as f:
                f.write(data)
            ok += 1
    print(f"  Extracted {ok}/{len(blp_targets)}")

total = len([f for f in os.listdir(blp_dir) if f.endswith('.blp')])
print(f"\n  Total BLPs in {blp_dir}: {total}")
PYEOF
                    
                    FINAL_BLP=$(ls "$BLP_OUT"/*.blp 2>/dev/null | wc -l)
                    if [ "$FINAL_BLP" -gt 0 ]; then
                        pass "Extracted $FINAL_BLP BLP files to $BLP_OUT"
                        info "Set SpellCreator:RawBlpPath = $BLP_OUT in Settings"
                    else
                        failure "BLP extraction produced no files"
                    fi
                fi
                
                echo ""
                info "Update the paths in Settings and restart:"
                info "  sudo systemctl restart mangossuperui"
            fi
        fi
    fi
fi

echo ""
