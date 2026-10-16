using System.Net.Http;

namespace IntellectHub;

internal sealed class AppServices
{
    public AppConfig Config { get; }
    public AccountPool Pool { get; }
    public ApiKeyStore ApiKeys { get; }
    public DeepSeekClient Client { get; }

    public AppServices(AppConfig config)
    {
        Config = config.Normalize();

        Pool = new AccountPool(AppDataRoot) { CooldownMs = Config.AccountCooldownSeconds * 1000L };
        ApiKeys = new ApiKeyStore(AppDataRoot);

        // Timeout is a safety net; the per-attempt budget is enforced by the caller,
        // because HttpClient.Timeout does not cover reading a streamed body.
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(Config.UpstreamTimeoutSeconds) };
        Client = new DeepSeekClient(http, new WasmtimePowSolverProvider(http), Config.UpstreamTimeoutSeconds);
    }

    public static string AppDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "IntellectHub");
}
