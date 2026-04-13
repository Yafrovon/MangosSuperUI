namespace MangosSuperUI.Services;

public class VmangosSettings
{
    public string BinDirectory { get; set; } = "";
    public string LogDirectory { get; set; } = "";
    public string ConfigDirectory { get; set; } = "";
    public string MangosdProcess { get; set; } = "mangosd";
    public string RealmdProcess { get; set; } = "realmd";
    public string MangosdConfPath { get; set; } = "";
    public string LogsDir { get; set; } = "";
    public string DbcPath { get; set; } = "/home/wowvmangos/vmangos/run/data/5875/dbc";
    public string MapsDataPath { get; set; } = "/home/wowvmangos/vmangos/run/data/maps";
    public string BackupDirectory { get; set; } = "/home/wowvmangos/backups";
    public string VmangosSourcePath { get; set; } = "/home/wowvmangos/vmangos/src";
    public string VmangosSqlPath { get; set; } = "/home/wowvmangos/vmangos/sql";
}

public class RemoteAccessSettings
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 3443;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public int ReconnectDelayMs { get; set; } = 3000;
    public int CommandTimeoutMs { get; set; } = 5000;
}