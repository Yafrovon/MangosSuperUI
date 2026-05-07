# MangosSuperUI — Installation Guide

> **Audience:** Someone with a working VMaNGOS 1.12.1 server (compiled, databases populated, able to log in and play). This guide does NOT cover compiling VMaNGOS or populating the world database — see the [VMaNGOS Wiki](https://github.com/vmangos/wiki) for that.

> **Database:** VMaNGOS works with MySQL 5.5+ or MariaDB 10.x+. Most Linux installs use MariaDB. MangosSuperUI uses the `MySqlConnector` .NET library which speaks the same wire protocol to either.

---

## Part 1: VMaNGOS Prerequisites

These steps prepare your VMaNGOS server for MangosSuperUI. Complete them in order.

---

### Step 1: Identify Your VMaNGOS Paths

You need to know where your VMaNGOS installation lives. Run these commands and note the results.

**Find your mangosd.conf:**
```bash
find / -name "mangosd.conf" 2>/dev/null
```

**Find your binary directory:**
```bash
find / -name "mangosd" -type f -executable 2>/dev/null
```
> You may see multiple results (e.g. one in `build/` and one in `run/bin/`). You want the one in your **runtime** directory — typically something like `/home/YOU/vmangos/run/bin/`, NOT the one inside a `build/` directory.

**Find your realmd binary name:**
```bash
ls YOUR_BIN_DIRECTORY/ | grep realmd
```

**Note your OS username:**
```bash
whoami
```

Write down these values — you'll use them throughout this guide:

| Item | Your Value |
|------|-----------|
| OS Username | ______________ |
| mangosd.conf path | ______________ |
| Binary directory (the run/ one) | ______________ |
| mangosd binary name | ______________ |
| realmd binary name | ______________ |

---

### Step 2: Verify Your Binary Names

The mangosd binary might be named `mangosd` or `mangosd-main` depending on your build. Check:

```bash
ls -la YOUR_BIN_DIRECTORY/ | grep -E "mangosd|realmd"
```

Note the exact filenames — you'll need them for the systemd service files.

---

### Step 3: Configure Remote Access (RA) in mangosd.conf

MangosSuperUI communicates with mangosd through the Remote Access (RA) TCP interface. You must enable it.

**Open mangosd.conf:**
```bash
nano YOUR_MANGOSD_CONF_PATH
```

**Find the RA settings:** Press `Ctrl+W` and search for `Ra.Enable`.

> **Note:** The first match will likely land you in a block of **comments** (lines starting with `#`, shown in blue text in nano). These are documentation lines, not the actual settings. Press `Ctrl+W` again and hit Enter to search forward — keep searching until you find the **uncommented** lines that look like `Ra.Enable = 0` (no `#` at the start).

**Set these values:**
```
Ra.Enable = 1
Ra.Restricted = 0
```

**CRITICAL — Add a new line** directly after `Ra.Restricted`. Type this exactly (it does not exist in the default config file but is required by the VMaNGOS source code):
```
Ra.MinLevel = 3
```

> **Why:** The VMaNGOS source code (`RASocket.cpp`) reads `Ra.MinLevel` to determine the minimum GM level for RA access. The `.dist` config file documents `Ra.MinAccountLevel` but the code ignores that setting entirely — it is never read. Without `Ra.MinLevel` explicitly set, the default blocks ALL accounts regardless of GM level. This is a confirmed VMaNGOS bug/documentation gap that every user will hit.

**Save and exit:** `Ctrl+O`, Enter, `Ctrl+X`.

**Verify your changes:**
```bash
grep -E "^Ra\." YOUR_MANGOSD_CONF_PATH
```

Expected output:
```
Ra.Enable = 1
Ra.IP = 0.0.0.0
Ra.Port = 3443
Ra.MinAccountLevel = 3
Ra.Restricted = 0
Ra.MinLevel = 3
```

---

### Step 4: Create systemd Services

VMaNGOS needs to run as system services so MangosSuperUI can start/stop/restart them.

> **Important:** mangosd reads from stdin. If systemd provides no stdin, mangosd receives EOF and shuts down immediately. The service file uses `StandardInput=tty-force` to prevent this.

**Create the mangosd service:**

Before pasting, replace the four placeholders below with your values from Step 1:
- `YOUR_USERNAME` → your OS username
- `YOUR_BIN_DIRECTORY` → your binary directory path (two occurrences)
- `YOUR_MANGOSD_BINARY` → your mangosd binary name

```bash
sudo tee /etc/systemd/system/mangosd.service > /dev/null << 'EOF'
[Unit]
Description=VMaNGOS World Server (mangosd)
After=mysql.service mariadb.service
Wants=mysql.service mariadb.service

[Service]
Type=simple
User=YOUR_USERNAME
WorkingDirectory=YOUR_BIN_DIRECTORY
ExecStart=YOUR_BIN_DIRECTORY/YOUR_MANGOSD_BINARY
Restart=on-failure
RestartSec=10
StandardInput=tty-force
TTYPath=/dev/tty20

[Install]
WantedBy=multi-user.target
EOF
```

> This command produces no output on success. That's normal.

**Verify it wrote correctly:**
```bash
cat /etc/systemd/system/mangosd.service
```

Confirm your paths and username appear correctly in the output.

**Create the realmd service:**

Same process — replace `YOUR_USERNAME`, `YOUR_BIN_DIRECTORY`, and `YOUR_REALMD_BINARY`:

```bash
sudo tee /etc/systemd/system/realmd.service > /dev/null << 'EOF'
[Unit]
Description=VMaNGOS Auth Server (realmd)
After=mysql.service mariadb.service
Wants=mysql.service mariadb.service

[Service]
Type=simple
User=YOUR_USERNAME
WorkingDirectory=YOUR_BIN_DIRECTORY
ExecStart=YOUR_BIN_DIRECTORY/YOUR_REALMD_BINARY
Restart=on-failure
RestartSec=10

[Install]
WantedBy=multi-user.target
EOF
```

**Verify:**
```bash
cat /etc/systemd/system/realmd.service
```

**Enable the services:**
```bash
sudo systemctl daemon-reload
sudo systemctl enable mangosd realmd
```

> Do NOT start the services yet — we need to create the RA account first (Step 5).

---

### Step 5: Create the RA Account

MangosSuperUI needs a game account with GM level 6 to authenticate with RA. Level 6 is the highest GM level in VMaNGOS and ensures full access to all RA commands. This account **must** be created through the mangosd console — creating accounts via raw SQL does not generate the required password hash (SRP6) and RA authentication will always fail.

**Install screen** (if not already installed):
```bash
sudo apt install screen -y
```

**Start a screen session:**
```bash
screen -S mangosd-setup
```

This drops you into a new shell inside screen.

**Start mangosd manually:**
```bash
cd YOUR_BIN_DIRECTORY && ./YOUR_MANGOSD_BINARY
```

**Wait for the `mangos>` prompt to appear** (approximately 10-15 seconds). You will see loading messages scroll by.

**Create the account — pick a username and password and write them down:**
```
.account create YOUR_RA_USERNAME YOUR_RA_PASSWORD
```

**Set GM level:**
```
.account set gmlevel YOUR_RA_USERNAME 6
```

> **Write down the RA username and password now.** You will need them during the setup script in Part 2. These are the only values the setup script cannot auto-discover.

**Shut down mangosd cleanly:**
```
.server shutdown 0
```

**Wait for mangosd to fully shut down** (you'll see "Halting process..." messages and eventually return to a shell prompt).

**Exit the screen session:**
```
exit
```

> **Important:** After exiting screen, your terminal may look garbled — the cursor may be at the top of the screen with stale text visible. This is a known screen artifact. **Close your terminal window and reconnect via SSH.** Everything is fine; the display is just confused.

---

### Step 6: Start the Services

```bash
sudo systemctl start realmd
```

```bash
sudo systemctl start mangosd
```

**Wait 15-20 seconds** for mangosd to fully load, then verify each one:

```bash
sudo systemctl status mangosd --no-pager
```

```bash
sudo systemctl status realmd --no-pager
```

Both should show `Active: active (running)`.

---

### Step 7: Verify RA Connectivity

```bash
telnet 127.0.0.1 3443
```

You should see:
```
Welcome to World of Warcraft!
Patch 1.12: Drums of War is now live!
Username:
```

Enter your RA username. When prompted, enter your RA password. If you see `+Logged in.` and `mangos>`, RA is working.

Type `.server info` to confirm commands work, then exit with `Ctrl+]` and type `quit`.

**If authentication fails:** Double-check that `Ra.MinLevel = 3` is in your mangosd.conf and that you restarted mangosd after adding it. Also verify your account was created with `.account create` in the mangosd console (not via raw SQL) and has gmlevel 6.

---

### Step 8: Configure sudo for MangosSuperUI

MangosSuperUI needs passwordless sudo access to start/stop/restart the mangosd and realmd services.

```bash
sudo visudo -f /etc/sudoers.d/mangossuperui
```

This opens an editor. Paste this single line (replace `YOUR_USERNAME` with your OS username):
```
YOUR_USERNAME ALL=(ALL) NOPASSWD: /usr/bin/systemctl start mangosd, /usr/bin/systemctl stop mangosd, /usr/bin/systemctl restart mangosd, /usr/bin/systemctl start realmd, /usr/bin/systemctl stop realmd, /usr/bin/systemctl restart realmd, /usr/bin/systemctl status mangosd, /usr/bin/systemctl status realmd
```

**Save and exit:** `Ctrl+X`, then Enter.

**Verify:**
```bash
sudo cat /etc/sudoers.d/mangossuperui
```

You should see your line with your username and all the systemctl commands.

---

### Part 1 Complete — Verify Everything

Run these checks to confirm your setup:

```bash
sudo systemctl status mangosd --no-pager | head -3
```

```bash
sudo systemctl status realmd --no-pager | head -3
```

```bash
mysql -u mangos -pmangos -e "SELECT a.id, a.username, aa.gmlevel FROM realmd.account a LEFT JOIN realmd.account_access aa ON a.id = aa.id;"
```

```bash
sudo cat /etc/sudoers.d/mangossuperui
```

You should see:
- Both services `active (running)`
- Your RA account with gmlevel 6
- The sudoers line with your username

If all checks pass, your VMaNGOS server is ready for MangosSuperUI.

---

## Part 2: MangosSuperUI Deployment

These steps install MangosSuperUI itself and get the dashboard green.

---

### Step 9: Install Prerequisites

MangosSuperUI is an ASP.NET Core 8.0 application. You need the ASP.NET Core runtime (not the full SDK) to run a pre-built release. You also need `curl` and `unzip` for later steps.

**Install curl and unzip:**
```bash
sudo apt install curl unzip -y
```

**Add the Microsoft package repository:**
```bash
wget https://packages.microsoft.com/config/ubuntu/$(lsb_release -rs)/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb
rm packages-microsoft-prod.deb
```

**Install the ASP.NET Core 8.0 runtime:**
```bash
sudo apt update
sudo apt install aspnetcore-runtime-8.0 -y
```

> If you want to build from source on the server instead of deploying a pre-built release, install `dotnet-sdk-8.0` instead. The SDK includes the runtime.

**Verify:**
```bash
dotnet --list-runtimes | grep AspNetCore
```

You should see a line containing `Microsoft.AspNetCore.App 8.0.x`.

---

### Step 10: Download and Install MangosSuperUI

There are two ways to get MangosSuperUI onto your server. Pick whichever suits you.

**First, create the install directory:**
```bash
sudo mkdir -p /opt/mangossuperui
```

> This creates the `/opt/mangossuperui` directory where MangosSuperUI will live. The `-p` flag means it won't error if the directory already exists.

#### Option A: Pre-Built Release (recommended)

Download the latest release ZIP from the GitHub releases page:

```bash
cd /tmp
wget https://github.com/YOUR_GITHUB/MangosSuperUI/releases/latest/download/MangosSuperUI-linux-x64.zip
```

> **TODO:** Replace the URL above with your actual GitHub releases URL once the repository is published.

Extract it to the install directory:
```bash
sudo unzip MangosSuperUI-linux-x64.zip -d /opt/mangossuperui
```

Set ownership to your user:
```bash
sudo chown -R YOUR_USERNAME:YOUR_USERNAME /opt/mangossuperui
```

#### Option B: Build from Source

Clone the repository and publish it yourself. This requires `dotnet-sdk-8.0` (not just the runtime).

```bash
cd /tmp
git clone https://github.com/YOUR_GITHUB/MangosSuperUI.git
cd MangosSuperUI
dotnet publish -c Release -o /tmp/mangossuperui-publish
```

Copy the published output to the install directory:
```bash
sudo cp -r /tmp/mangossuperui-publish/* /opt/mangossuperui/
sudo chown -R YOUR_USERNAME:YOUR_USERNAME /opt/mangossuperui
```

#### Verify the install:

```bash
ls /opt/mangossuperui/MangosSuperUI.dll
```

You should see the file listed. If not, check that you extracted/copied to the right directory.

---

### Step 11: Create the MangosSuperUI systemd Service

This creates a system service so MangosSuperUI starts automatically and can be managed with `systemctl`.

Replace `YOUR_USERNAME` with your OS username before pasting:

```bash
sudo tee /etc/systemd/system/mangossuperui.service > /dev/null << 'EOF'
[Unit]
Description=MangosSuperUI Web Admin
After=network.target mariadb.service

[Service]
Type=simple
User=YOUR_USERNAME
WorkingDirectory=/opt/mangossuperui
ExecStart=/usr/bin/dotnet /opt/mangossuperui/MangosSuperUI.dll
Restart=on-failure
RestartSec=5
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=DOTNET_PRINT_TELEMETRY_MESSAGE=false

[Install]
WantedBy=multi-user.target
EOF
```

**Enable the service (but do NOT start it yet):**
```bash
sudo systemctl daemon-reload
sudo systemctl enable mangossuperui
```

> We need to create the admin database and run the setup script before MangosSuperUI's first boot. Starting it now without a valid `server-config.json` and without the `vmangos_admin` database would produce errors on the dashboard.

---

### Step 12: Prepare the Admin Database

MangosSuperUI uses its own database called `vmangos_admin` for audit logs, config history, baseline snapshots, and scheduled actions. On first boot, MangosSuperUI's `DbInitializationService` automatically creates the tables and indexes inside this database — but it cannot create the database itself or grant its own permissions. The default VMaNGOS database user (`mangos`) only has grants on the VMaNGOS databases, not server-level `CREATE` privileges.

You must create the database and grant access **before MangosSuperUI starts for the first time:**

```bash
sudo mysql -e "CREATE DATABASE IF NOT EXISTS vmangos_admin; GRANT ALL PRIVILEGES ON vmangos_admin.* TO 'mangos'@'localhost'; FLUSH PRIVILEGES;"
```

> If your VMaNGOS installation uses a different database username or host, adjust the command accordingly. For example, if your database user is `vmangos` instead of `mangos`:
> ```bash
> sudo mysql -e "CREATE DATABASE IF NOT EXISTS vmangos_admin; GRANT ALL PRIVILEGES ON vmangos_admin.* TO 'vmangos'@'localhost'; FLUSH PRIVILEGES;"
> ```
>
> The username and host must match what's in your `mangosd.conf` database connection lines (e.g. `WorldDatabase.Info = "127.0.0.1;3306;mangos;mangos;mangos"` — the third field is the username).

**Verify the database exists and the grant works:**
```bash
mysql -u mangos -pmangos -e "USE vmangos_admin; SELECT 'OK';"
```

You should see `OK`. If you get "Access denied", double-check the username and re-run the grant command.

> **What happens on first boot:** When MangosSuperUI starts, `DbInitializationService` connects to `vmangos_admin` and runs `CREATE TABLE IF NOT EXISTS` for four tables: `audit_log`, `config_history`, `scheduled_actions`, and `og_baseline_meta` — all with their indexes. This is automatic and silent. If the database doesn't exist or the user lacks permission, the dashboard will show the Admin database as red, but the rest of MangosSuperUI still functions.

---

### Step 13: Run the Setup Script

MangosSuperUI needs a `server-config.json` file that tells it how to connect to your databases, where your VMaNGOS files are, and your RA credentials. Rather than filling this out manually, the setup script auto-discovers everything from your `mangosd.conf`.

**Download the setup script:**
```bash
cd ~
wget https://github.com/YOUR_GITHUB/MangosSuperUI/releases/latest/download/setup-mangossuperui.sh
```

> **TODO:** Replace the URL above with your actual GitHub releases URL once the repository is published.

**Run it:**
```bash
sudo bash ~/setup-mangossuperui.sh
```

The script will:

1. **Find your mangosd.conf** — searches the filesystem automatically. If multiple copies exist (e.g. one in `build/` and one in `run/etc/`), it asks you to pick.

2. **Read your database connections** — parses the `WorldDatabase.Info`, `CharacterDatabase.Info`, `LoginDatabase.Info`, and `LogsDatabase.Info` lines from `mangosd.conf`. These are in the format `host;port;user;password;database` and the script converts them to the connection string format MangosSuperUI expects. The `vmangos_admin` connection is derived automatically from the World DB credentials.

3. **Discover VMaNGOS paths** — finds your binary directory (preferring `run/` paths over `build/` paths), config directory, DBC files, heightmap files, and log files. All derived from the binary location and the `DataDir` setting in `mangosd.conf`.

4. **Detect process names** — if mangosd and realmd are running, reads their process names from `/proc`. Otherwise falls back to the binary filenames.

5. **Ask for RA credentials** — this is the only thing the script cannot auto-discover. Enter the username and password you created in Part 1, Step 5.

6. **Test RA connectivity** — quick TCP check to confirm the RA port is reachable.

7. **Generate `server-config.json`** — writes the config file to `/opt/mangossuperui/server-config.json` and shows you a summary of everything it discovered.

8. **Start MangosSuperUI** — starts (or restarts) the service so the new configuration takes effect. This is the first real boot.

**Example output from a successful run:**
```
Step 1: Locating mangosd.conf
  ✓ Found: /home/nicholas/vmangos/run/etc/mangosd.conf

Step 2: Reading database connections from mangosd.conf
  ✓ World (mangos): mangos@127.0.0.1:3306/mangos
  ✓ Characters: mangos@127.0.0.1:3306/characters
  ✓ Realmd: mangos@127.0.0.1:3306/realmd
  ✓ Logs: mangos@127.0.0.1:3306/logs
  ✓ Admin: mangos@127.0.0.1:3306/vmangos_admin (auto-created on first boot)

Step 3: Discovering VMaNGOS paths
  ✓ Config directory: /home/nicholas/vmangos/run/etc
  ✓ Bin directory: /home/nicholas/vmangos/run/bin
  ✓ mangosd binary: mangosd
  ✓ realmd binary: realmd
  → mangosd not running — using binary name as process name: mangosd
  → realmd not running — using binary name as process name: realmd
  ✓ DataDir: /home/nicholas/vmangos/run/data
  ✓ DBC path: /home/nicholas/vmangos/run/data/5875/dbc (158 .dbc files)
  ✓ Maps path: /home/nicholas/vmangos/run/data/maps (2429 .map files)
  ✓ Log directory: /home/nicholas/vmangos/run/bin

Step 4: Remote Access (RA) configuration
  ✓ RA port: 3443
  ✓ RA host: 127.0.0.1 (local)

  Enter your RA credentials (the account you created in Part 1, Step 5):

  RA Username: superui
  RA Password:
  ✓ RA credentials set

Step 5: Testing RA connectivity
  ✓ RA port 3443 is open

Step 6: Verifying MangosSuperUI installation
  ✓ MangosSuperUI.dll found in /opt/mangossuperui

Step 7: Generating server-config.json

Configuration Summary

  Database Connections:
    Mangos:      Server=127.0.0.1;Port=3306;Database=mangos;User=mangos;Password=mangos;
    Characters:  Server=127.0.0.1;Port=3306;Database=characters;User=mangos;Password=mangos;
    Realmd:      Server=127.0.0.1;Port=3306;Database=realmd;User=mangos;Password=mangos;
    Logs:        Server=127.0.0.1;Port=3306;Database=logs;User=mangos;Password=mangos;
    Admin:       Server=127.0.0.1;Port=3306;Database=vmangos_admin;User=mangos;Password=mangos;

  VMaNGOS Paths:
    Bin Dir:     /home/nicholas/vmangos/run/bin
    Log Dir:     /home/nicholas/vmangos/run/bin
    Config Dir:  /home/nicholas/vmangos/run/etc
    Conf Path:   /home/nicholas/vmangos/run/etc/mangosd.conf
    DBC Path:    /home/nicholas/vmangos/run/data/5875/dbc
    Maps Path:   /home/nicholas/vmangos/run/data/maps

  Process Names:
    mangosd:     mangosd
    realmd:      realmd

  Remote Access:
    Host:        127.0.0.1:3443
    Username:    superui
    Password:    ********

  ✓ Written to /opt/mangossuperui/server-config.json

Step 8: Starting MangosSuperUI
  ✓ MangosSuperUI started successfully

Setup Complete!

  Open your browser to http://192.168.50.2:5000
  The Dashboard should show green indicators for mangosd, realmd, RA, and all databases.

  If anything is red, check the Troubleshooting section of the Install Guide.
```

If you see all green checkmarks and the summary looks correct, you're done with configuration.

> **Config layering:** MangosSuperUI loads `appsettings.json` (safe defaults shipped with the release) first, then overlays `server-config.json` (your personal values) on top. `server-config.json` is `.gitignore`d and never committed. The setup script only writes `server-config.json` — it never modifies `appsettings.json`. You can also edit everything through the Settings page in the web UI, which writes back to `server-config.json`.

---

### Step 14: Verify the Dashboard

Open your browser and navigate to:
```
http://YOUR_SERVER_IP:5000
```

The Dashboard should show:
- **mangosd**: green (running)
- **realmd**: green (running)
- **RA**: green (connected)
- **All five databases**: green (reachable)
- **vmangos_admin**: initialized (tables created automatically on first connection)
- **Players Online**, **Uptime**, and **Core Revision**: showing live data from the RA connection

If all indicators are green, your MangosSuperUI installation is fully operational.

> **If the Admin database shows red** with "Access denied for user 'mangos'@'localhost' to database 'vmangos_admin'", the database permissions from Step 12 didn't take effect. Re-run the grant command and restart MangosSuperUI:
> ```bash
> sudo mysql -e "CREATE DATABASE IF NOT EXISTS vmangos_admin; GRANT ALL PRIVILEGES ON vmangos_admin.* TO 'mangos'@'localhost'; FLUSH PRIVILEGES;"
> sudo systemctl restart mangossuperui
> ```

---

### Step 15: Understanding the Settings Page (Reference)

The setup script generates `server-config.json` automatically, but you may want to adjust settings later. The Settings page (click **Settings** in the sidebar) lets you edit everything the setup script configured, plus a few additional options.

The page has six sections:

#### Database Connections

Connection strings for the five MangosSuperUI databases. Format: `Server=HOST;Port=PORT;Database=DBNAME;User=USER;Password=PASS;`

The setup script reads these from your `mangosd.conf` database lines (e.g. `WorldDatabase.Info = "127.0.0.1;3306;mangos;mangos;mangos"`) and converts them automatically.

#### Remote Access (RA)

TCP connection to mangosd's RA console. The setup script reads the port from `Ra.Port` in `mangosd.conf` and uses `127.0.0.1` as the host (assumes MangosSuperUI runs on the same machine as mangosd).

| Field | What It Is |
|-------|------------|
| Host | IP address of the machine running mangosd |
| Port | RA port (default `3443`, read from `Ra.Port` in mangosd.conf) |
| Username | The RA account created in Part 1, Step 5 |
| Password | The RA account password |
| Timeout (ms) | How long to wait for RA command responses (default `5000`) |

#### VMaNGOS Paths & Processes

Where your VMaNGOS installation lives on the filesystem. The setup script discovers all of these automatically.

| Field | Example | What It's For |
|-------|---------|---------------|
| Bin Directory | `/home/YOU/vmangos/run/bin` | Location of the mangosd/realmd binaries |
| Log Directory | `/home/YOU/vmangos/run/bin` | Live Logs page reads `.log` files from here |
| Config Directory | `/home/YOU/vmangos/run/etc` | Directory containing `mangosd.conf` |
| World Server Process Name | `mangosd` or `mangosd-main` | The process name as the OS sees it — used for status detection |
| Auth Server Process Name | `realmd` or `realmd-main` | Same as above, for the auth server |
| mangosd.conf Path | `/home/YOU/vmangos/run/etc/mangosd.conf` | Full path — used by the Config Editor page |
| Server Logs Directory | `/home/YOU/vmangos/run/bin` | Used by the Live Logs page for real-time log tailing |

> **Finding your process names:** If you need to check what your binaries register as, run this while the server is running:
> ```bash
> cat /proc/$(pgrep -f mangosd)/comm
> ```

#### DBC Data Files

Path to the extracted 1.12.1 DBC files on the server. The setup script derives this from the `DataDir` setting in `mangosd.conf` (typically `DataDir/5875/dbc`). These files are read directly from your VMaNGOS data directory — they are NOT copied into the MangosSuperUI deployment.

After changing the path, click **Reload DBC** to re-parse the files. The status panel shows record counts for each parsed DBC file (ItemDisplayInfo, SpellIcon, SpellDuration, SpellCastTimes, SpellRange).

#### Maps Data (Heightmaps)

Path to the `.map` files generated by the VMaNGOS map extractor. The setup script derives this from `DataDir/maps`. Used for terrain Z-coordinate resolution when placing game objects through the World Map page.

> Without this configured, game objects placed via the World Map will spawn at Z=0 (sea level). Everything else works fine.

#### Web Server (Kestrel)

The URL MangosSuperUI listens on. Default: `http://0.0.0.0:5000` (all network interfaces, port 5000). Change the port here if 5000 conflicts with something else.

> Changing the Kestrel listen URL requires a service restart to take effect: `sudo systemctl restart mangossuperui`

---

### Step 16: Asset Directories

MangosSuperUI serves four types of static assets from its `wwwroot/` directory. These are extracted from the WoW 1.12.1 client and are used by the content browser and world map pages.

| Directory | Contents | Used By | Approximate Count |
|-----------|----------|---------|-------------------|
| `wwwroot/icons/` | Item and spell icon PNGs (e.g. `inv_sword_04.png`) | Items, Spells, Lootifier, Instance Loot pages | ~2,684 files |
| `wwwroot/models/` | Game object 3D models in GLB format | Game Objects page (3D model viewer) | ~1,070 files |
| `wwwroot/item_models/` | Item 3D models in GLB format (e.g. `23904.glb`) | Items page (3D model viewer) | Varies |
| `wwwroot/minimap/{MapName}/` | Minimap tile PNGs (e.g. `Azeroth/map29_28.png`) | World Map page (Leaflet tile layer) | Varies by map |

#### Where do these files come from?

These assets are extracted from the WoW 1.12.1 client's MPQ archives using the **MangosSuperUI Extractor** — a Windows tool included with the project. See Part 3 below for extraction and deployment instructions.

https://github.com/Yafrovon/MangosSuperUI_Extractor

#### Filename casing

The MangosSuperUI Extractor handles all filename casing automatically — icon filenames are output in lowercase, and minimap folder names preserve the original WoW map casing (e.g. `Azeroth/`, `Kalimdor/`). No manual renaming is needed after extraction.

#### What works without assets?

MangosSuperUI is fully functional without these asset directories — you just won't see:
- Icon images on the Items, Spells, and Lootifier pages (items/spells still display, just with placeholder icons)
- 3D model previews on the Game Objects or Items pages
- The minimap tile layer on the World Map page (placement and spawn overlay still work with a blank canvas)

All database operations, RA commands, server management, loot editing, and every other feature works regardless of whether assets are present.

---

### Step 17: Deploy Script (Optional)

If you develop on a separate machine (e.g. Windows with Visual Studio) and deploy to your Linux server, a simple deploy script saves time. Create this on your Linux server:

```bash
nano ~/deploy-ui.sh
```

Paste:
```bash
#!/bin/bash
echo "Stopping MangosSuperUI..."
sudo systemctl stop mangossuperui

echo "Copying files from staging..."
cp -r /tmp/mangossuperui-deploy/* /opt/mangossuperui/

echo "Starting MangosSuperUI..."
sudo systemctl start mangossuperui

echo "Done. Checking status..."
sleep 2
sudo systemctl status mangossuperui --no-pager | head -5
```

```bash
chmod +x ~/deploy-ui.sh
```

**Usage from your development machine (Windows PowerShell):**
```powershell
# After publishing in Visual Studio (Release → Publish)
scp -r bin/Release/net8.0/publish/* YOUR_USERNAME@YOUR_SERVER_IP:/tmp/mangossuperui-deploy/
```

**Then SSH into the server:**
```bash
~/deploy-ui.sh
```

> The staging directory `/tmp/mangossuperui-deploy/` is a safe landing zone — you copy files there first, then the script stops the service, copies them to the install directory, and restarts. This avoids any issues with overwriting files while the app is running.

---

### Part 2 Complete — Verify Everything

Run these final checks:

```bash
sudo systemctl status mangossuperui --no-pager | head -3
```

Open the Dashboard in your browser at `http://YOUR_SERVER_IP:5000` and confirm:
- mangosd: green (running)
- realmd: green (running)
- RA: green (connected)
- All five databases: green (reachable)
- vmangos_admin: initialized (tables created automatically)
- Players Online / Uptime / Core Revision: showing live data

If all indicators are green, your MangosSuperUI installation is complete.

---

## Part 3: Asset Extraction

The MangosSuperUI Extractor is a Windows tool that reads the WoW 1.12.1 client's MPQ archives and outputs the icons, 3D models, and minimap tiles that MangosSuperUI uses for its content browser and world map pages.

---

### Requirements

- **Windows PC** with the MangosSuperUI Extractor installed
- **WoW 1.12.1 client** — the extractor reads from the `Data/` directory containing the MPQ archives

---

### Step 18: Run the Extractor

1. Open the MangosSuperUI Extractor on your Windows machine.
2. Point it at your WoW 1.12.1 client's `Data/` directory.
3. Run the extraction. It will scan the MPQ archives and output assets into folders on your desktop (or configured output location).

The extractor produces these output folders:

| Output Folder | Contents | MangosSuperUI Location |
|---------------|----------|----------------------|
| `icons/` | Item and spell icon PNGs | `wwwroot/icons/` |
| `models/` | Game object 3D models (GLB) | `wwwroot/models/` |
| `item_models/` | Item 3D models by displayId (GLB) | `wwwroot/item_models/` |
| `minimap/` | Minimap tile PNGs organized by map name | `wwwroot/minimap/` |

> The extractor also outputs additional folders (`manifests/`, `worldmaps/`, `dbc/`, `creature_textures/`, `item_textures/`) — these are not needed by MangosSuperUI and can be ignored.

---

### Step 19: Copy Assets to the Server

Copy the contents of each extraction folder into the corresponding `wwwroot/` directory on your MangosSuperUI server.

**Create the directories on the server:**
```bash
mkdir -p /opt/mangossuperui/wwwroot/icons
mkdir -p /opt/mangossuperui/wwwroot/models
mkdir -p /opt/mangossuperui/wwwroot/item_models
mkdir -p /opt/mangossuperui/wwwroot/minimap
```

**From Windows PowerShell, SCP each folder:**
```powershell
scp -r "C:\path\to\extracted\icons\*" YOUR_USERNAME@YOUR_SERVER_IP:/opt/mangossuperui/wwwroot/icons/
scp -r "C:\path\to\extracted\models\*" YOUR_USERNAME@YOUR_SERVER_IP:/opt/mangossuperui/wwwroot/models/
scp -r "C:\path\to\extracted\item_models\*" YOUR_USERNAME@YOUR_SERVER_IP:/opt/mangossuperui/wwwroot/item_models/
scp -r "C:\path\to\extracted\minimap\*" YOUR_USERNAME@YOUR_SERVER_IP:/opt/mangossuperui/wwwroot/minimap/
```

> Replace `C:\path\to\extracted\` with the actual path where the extractor saved its output.

---

### Step 20: Verify Assets

**Check file counts:**
```bash
echo "Icons: $(ls /opt/mangossuperui/wwwroot/icons/*.png 2>/dev/null | wc -l)"
echo "GO Models: $(ls /opt/mangossuperui/wwwroot/models/*.glb 2>/dev/null | wc -l)"
echo "Item Models: $(ls /opt/mangossuperui/wwwroot/item_models/*.glb 2>/dev/null | wc -l)"
echo "Minimap maps: $(ls -d /opt/mangossuperui/wwwroot/minimap/*/ 2>/dev/null | wc -l)"
```

Expected approximate counts: ~2,684 icons, ~1,070 GO models, item models vary, and several minimap map directories (Azeroth, Kalimdor, plus dungeon/raid maps).

**Spot-check in the browser:**
- **Icons:** Open the Items page, search for any item — icons should appear next to item names.
- **Game Object models:** Open the Game Objects page, click any object — 3D model viewer should load.
- **Item models:** Open the Items page, click an item with a weapon/armor model — 3D model viewer should load.
- **Minimap:** Open the World Map page — tile imagery should render on the map.

No restart of MangosSuperUI is needed. Static files are served immediately.

---

## Troubleshooting

### RA authentication always fails
- **Most common cause:** `Ra.MinLevel = 3` is missing from `mangosd.conf`. The default config file does NOT include this setting, but the VMaNGOS source code requires it. Add it and restart mangosd.
- **Second most common cause:** Account was created via raw SQL INSERT instead of `.account create` in the mangosd console. The SRP6 password hash is not generated by SQL — delete the account and recreate it through the console.

### mangosd starts then immediately stops as a systemd service
- The service file needs `StandardInput=tty-force` and `TTYPath=/dev/tty20`. Without this, mangosd receives EOF on stdin and interprets it as a shutdown command.

### "Connection refused" on telnet to port 3443
- mangosd may still be loading (takes 10-15 seconds). Wait and try again.
- Check that `Ra.Enable = 1` is set in mangosd.conf.
- Check that mangosd is actually running: `sudo systemctl status mangosd`

### Terminal looks garbled after exiting screen
- This is normal. Close your terminal window and reconnect via SSH.

### MangosSuperUI won't start / port 5000 connection refused
- Check the service status: `sudo systemctl status mangossuperui --no-pager`
- Check the journal for errors: `sudo journalctl -u mangossuperui --no-pager -n 50`
- If you see `Unable to bind to address`, something else is using port 5000. Change the Kestrel listen URL in Settings or in `appsettings.json` directly and restart.
- If you see DBC-related errors on first start, verify your DBC path is correct in Settings. The app will still start without DBC files — those errors are warnings, not fatal.

### MangosSuperUI crashes on startup after running the setup script
- The setup script may have written invalid values into `server-config.json`. Check the file: `cat /opt/mangossuperui/server-config.json`
- If the connection string values contain warning messages instead of actual connection strings (e.g. "not found in mangosd.conf"), the script failed to parse your `mangosd.conf`. Delete the config and try again: `sudo rm /opt/mangossuperui/server-config.json && sudo systemctl restart mangossuperui`
- You can also configure everything manually through the Settings page in the web interface instead of using the setup script.

### Dashboard shows "Access denied" for vmangos_admin database
- The VMaNGOS database user does not have permission to access the `vmangos_admin` database. Run the grant command from Step 12:
  ```bash
  sudo mysql -e "CREATE DATABASE IF NOT EXISTS vmangos_admin; GRANT ALL PRIVILEGES ON vmangos_admin.* TO 'mangos'@'localhost'; FLUSH PRIVILEGES;"
  ```
- If your database user is not `mangos`, replace it in the command above. Check your `mangosd.conf` — the username is the third field in the database connection lines (e.g. `WorldDatabase.Info = "127.0.0.1;3306;mangos;mangos;mangos"`).
- After granting permissions, restart MangosSuperUI: `sudo systemctl restart mangossuperui`

### Dashboard shows databases as red/unreachable
- Verify MariaDB/MySQL is running: `sudo systemctl status mariadb` or `sudo systemctl status mysql`
- Verify the connection strings in Settings match your database server address, port, username, and password.
- If MangosSuperUI is on a different machine than the database, ensure the database user has remote access privileges.

### Dashboard shows RA as red but databases are green
- mangosd may not be running. Check: `sudo systemctl status mangosd`
- The RA username or password in Settings may be wrong. Double-check against what you created in Step 5.
- If mangosd is running but RA still won't connect, verify `Ra.Enable = 1` and `Ra.MinLevel = 3` in `mangosd.conf` and restart mangosd.

### DBC Reload shows zero records
- The DBC path in Settings is wrong or the directory is empty. Verify the path contains `.dbc` files: `ls YOUR_DBC_PATH/*.dbc | head`
- The DBC files must be the 1.12.1 (build 5875) versions. Other client versions will fail to parse or show incorrect data.

### Settings changes don't take effect
- Most settings apply immediately after clicking Save (database connections, RA credentials, paths). The **Kestrel listen URL** is the exception — changing the port requires a full service restart: `sudo systemctl restart mangossuperui`

### World Map shows no Z-resolution / objects spawn at sea level
- Verify the Maps Data path is set correctly in Settings and points to a directory containing `.map` files: `ls YOUR_MAPS_PATH/*.map | head`
- If the minimap tiles display but Z-resolution doesn't work, check the minimap folder names have proper casing (e.g. `Azeroth/`, `Kalimdor/`, not `azeroth/`, `kalimdor/`). The HeightMapService matches these folder names against the map data. If your folders are lowercase, rename them to match the original WoW map names:
  ```bash
  cd /opt/mangossuperui/wwwroot/minimap
  mv azeroth Azeroth
  mv kalimdor Kalimdor
  # ... etc. for each map directory
  ```
  This is typically caused by using an older version of the MangosSuperUI Extractor that lowercased folder names. Re-extracting with the current version will produce the correct casing.

### Setup script finds wrong binary directory (build/ instead of run/)
- The script prefers paths containing `/run/` over `/build/`, but if your directory structure is unusual it may pick the wrong one. Check the Configuration Summary output. If the Bin Dir points to a `build/` path, you can either re-run the script and adjust `server-config.json` manually afterwards, or edit the config through the Settings page in the web interface.