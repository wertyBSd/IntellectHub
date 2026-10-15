using System.Net.Http;

namespace IntellectHub;

internal sealed class AppServices
{
    public AppConfig Config { get; }
    public AccountPool Pool { get; }
    public DeepSeekClient Client { get; }

    public AppServices(AppConfig config)
    {
        Config = config;
        Pool = new AccountPool(AppDataRoot);
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        Client = new DeepSeekClient(http, new WasmtimePowSolverProvider(http));
    }

    public static string AppDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "IntellectHub");
}
