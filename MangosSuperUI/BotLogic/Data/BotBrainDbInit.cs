using Dapper;
using MangosSuperUI.Models;
using MangosSuperUI.Services;

namespace MangosSuperUI.BotLogic.Data;

/// <summary>
/// Creates the BotLogic tables in the vmangos_admin MariaDB database.
/// Called once at startup by BotBrainService.
/// Uses CREATE TABLE IF NOT EXISTS — safe to run repeatedly.
/// </summary>
public class BotBrainDbInit
{
    private readonly ConnectionFactory _db;
    private readonly ILogger<BotBrainDbInit> _logger;

    public BotBrainDbInit(ConnectionFactory db, ILogger<BotBrainDbInit> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task InitializeAsync()
    {
        try
        {
            using var conn = _db.Admin();

            // --- bot_personality ---
            await conn.ExecuteAsync(@"
                CREATE TABLE IF NOT EXISTS bot_personality (
                    bot_guid            INT NOT NULL PRIMARY KEY,
                    patience            FLOAT NOT NULL DEFAULT 0.5,
                    greed               FLOAT NOT NULL DEFAULT 0.5,
                    curiosity           FLOAT NOT NULL DEFAULT 0.5,
                    sociability         FLOAT NOT NULL DEFAULT 0.5,
                    aggression          FLOAT NOT NULL DEFAULT 0.5,
                    efficiency          FLOAT NOT NULL DEFAULT 0.5,
                    cautiousness        FLOAT NOT NULL DEFAULT 0.5,
                    indecisiveness      FLOAT NOT NULL DEFAULT 0.5,
                    spontaneity         FLOAT NOT NULL DEFAULT 0.5,
                    chat_style          VARCHAR(32) NOT NULL DEFAULT 'casual',
                    temperament         VARCHAR(32) NOT NULL DEFAULT 'friendly',
                    quirk_ids           VARCHAR(256) DEFAULT NULL,
                    created_at          DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4");

            // --- bot_activity_log ---
            await conn.ExecuteAsync(@"
                CREATE TABLE IF NOT EXISTS bot_activity_log (
                    id                  INT AUTO_INCREMENT PRIMARY KEY,
                    bot_guid            INT NOT NULL,
                    activity_type       VARCHAR(32) NOT NULL,
                    started_at          DATETIME NOT NULL,
                    ended_at            DATETIME DEFAULT NULL,
                    context_tag         VARCHAR(128) DEFAULT NULL,
                    decision_reason     VARCHAR(256) DEFAULT NULL,
                    weight_snapshot     TEXT DEFAULT NULL,
                    roll_value          FLOAT DEFAULT NULL,
                    INDEX idx_bot_activity_log_guid (bot_guid),
                    INDEX idx_bot_activity_log_started (started_at)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4");

            // --- bot_registry ---
            await conn.ExecuteAsync(@"
                CREATE TABLE IF NOT EXISTS bot_registry (
                    bot_guid            INT NOT NULL PRIMARY KEY,
                    bot_name            VARCHAR(64) NOT NULL,
                    race                TINYINT NOT NULL,
                    class_id            TINYINT NOT NULL,
                    level               TINYINT NOT NULL DEFAULT 1,
                    faction             VARCHAR(16) NOT NULL DEFAULT '',
                    spawn_status        VARCHAR(16) NOT NULL DEFAULT 'inactive',
                    current_zone_id     INT DEFAULT NULL,
                    current_activity    VARCHAR(32) DEFAULT NULL,
                    last_seen           DATETIME DEFAULT NULL,
                    created_at          DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    INDEX idx_bot_registry_status (spawn_status),
                    INDEX idx_bot_registry_zone (current_zone_id)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4");

            // --- bot_wallet (shadow economy) ---
            await conn.ExecuteAsync(@"
                CREATE TABLE IF NOT EXISTS bot_wallet (
                    bot_guid            INT NOT NULL PRIMARY KEY,
                    copper              BIGINT NOT NULL DEFAULT 0,
                    updated_at          DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4");

            // --- bot_inventory (shadow inventory persistence) ---
            await conn.ExecuteAsync(@"
                CREATE TABLE IF NOT EXISTS bot_inventory (
                    id                  INT AUTO_INCREMENT PRIMARY KEY,
                    bot_guid            INT NOT NULL,
                    item_id             INT NOT NULL,
                    count               INT NOT NULL DEFAULT 1,
                    quality             TINYINT NOT NULL DEFAULT 0,
                    sell_price          INT NOT NULL DEFAULT 0,
                    source              VARCHAR(32) NOT NULL DEFAULT 'loot',
                    source_creature     INT DEFAULT 0,
                    acquired_at         DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    INDEX idx_bot_inv_guid (bot_guid)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4");

            // --- bot_groups (Session 31 — grouping system) ---
            await conn.ExecuteAsync(@"
                CREATE TABLE IF NOT EXISTS bot_groups (
                    group_id        INT NOT NULL PRIMARY KEY,
                    leader_guid     INT NOT NULL,
                    member_guids    VARCHAR(256) NOT NULL,
                    leader_type     TINYINT NOT NULL DEFAULT 0,
                    formed_at       DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    INDEX idx_bot_groups_leader (leader_guid)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4");

            // --- bot_settings (Session 31 — server-level bot config) ---
            await conn.ExecuteAsync(@"
                CREATE TABLE IF NOT EXISTS bot_settings (
                    setting_key     VARCHAR(64) NOT NULL PRIMARY KEY,
                    setting_value   VARCHAR(256) NOT NULL DEFAULT '',
                    updated_at      DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4");

            _logger.LogInformation("BotBrainDbInit: all tables verified/created");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BotBrainDbInit: failed to initialize tables");
        }
    }
}