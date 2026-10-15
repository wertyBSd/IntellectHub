using System.Text.Json;

namespace IntellectHub;

internal sealed class AppConfig
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 9656;
    public string ApiKey { get; set; } = string.Empty;
    public string AuthPath { get; set; } = "deepseek-auth.json";
    public bool AutoStartServer { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public bool StartMinimized { get; set; } = true;
    public bool AutoScrollLog { get; set; } = true;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "IntellectHub",
        "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), Options) ?? new AppConfig();
        }
        catch
        {
        }
        return new AppConfig();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch
        {
        }
    }
}
