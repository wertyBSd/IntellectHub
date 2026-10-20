using System.Text.Json;
using IntellectHub.Client;
using Xunit;

namespace IntellectHub.Tests;

/// <summary>
/// Provider presets and the credentials behind them. Two things have to hold: the
/// preset names the right address and model for its provider, and the credential
/// for one provider never leaks into another's slot.
/// </summary>
public sealed class ProviderPresetTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ih-providers-" + Guid.NewGuid().ToString("N"));
    private readonly string _configDirBefore = ClientConfig.ConfigDir;
    private readonly string _configPathBefore = ClientConfig.ConfigPath;
    private readonly string _secretsPathBefore = ClientConfig.SecretsPath;

    public ProviderPresetTests()
    {
        Directory.CreateDirectory(_dir);
        ClientConfig.ConfigDir = _dir;
        ClientConfig.ConfigPath = Path.Combine(_dir, "client.json");
        ClientConfig.SecretsPath = Path.Combine(_dir, "secrets.json");
    }

    [Fact]
    public void EveryPresetNamesAProviderAddressAndModel()
    {
        foreach (var preset in ClientConfig.Providers.Presets)
        {
            Assert.False(string.IsNullOrWhiteSpace(preset.Name));
            Assert.StartsWith("https://", preset.Url);

            // Storage is not a chat provider, so it has no model to answer with.
            if (preset.Provider == ProviderKind.CloudflareR2) continue;
            Assert.False(string.IsNullOrWhiteSpace(preset.Model));
        }
    }

    [Fact]
    public void PresetsAreFoundByName()
    {
        Assert.Equal("openrouter", ClientConfig.Providers.Find("OpenRouter")?.Name);
        Assert.Equal("google", ClientConfig.Providers.Find("GOOGLE")?.Name);
        Assert.Equal("deepseek", ClientConfig.Providers.Find("deepseek")?.Name);
        Assert.Null(ClientConfig.Providers.Find("nowhere"));
    }

    [Fact]
    public void OpenRouterCarriesTheHeadersItInsistsOn()
    {
        var openrouter = ClientConfig.Providers.OpenRouter();
        Assert.True(openrouter.Headers.ContainsKey("HTTP-Referer"));
        Assert.True(openrouter.Headers.ContainsKey("X-Title"));
    }

    [Fact]
    public void GoogleAndAnthropicUseTheirOwnAuthHeaders()
    {
        Assert.Equal(AuthStyle.GoogleApiKey, ClientConfig.Providers.Google().Auth);
        Assert.Equal(AuthStyle.AnthropicKey, ClientConfig.Providers.Anthropic().Auth);
        Assert.Equal(AuthStyle.Bearer, ClientConfig.Providers.OpenRouter().Auth);
        Assert.Equal(AuthStyle.Bearer, ClientConfig.Providers.DeepSeek().Auth);
    }

    [Fact]
    public void PresetsAreSeededOnLoadSoAConnectionCanBeSwitchedToRightAway()
    {
        var config = ClientConfig.Load();
        Assert.Contains(config.AllConnections, c => c.Name == "openrouter");
        Assert.Contains(config.AllConnections, c => c.Name == "deepseek");
    }

    [Fact]
    public void ASavedConnectionOfTheSameNameWinsOverThePreset()
    {
        File.WriteAllText(ClientConfig.ConfigPath, """
            {
              "connections": [
                { "name": "openrouter", "url": "https://proxy.internal/v1", "model": "local-model" }
              ]
            }
            """);

        var config = ClientConfig.Load();
        var openrouter = config.AllConnections.First(c => c.Name == "openrouter");
        Assert.Equal("https://proxy.internal/v1", openrouter.Url);
    }

    [Fact]
    public void ApplyingAConnectionAdoptsItsAddressAndModel()
    {
        var config = ClientConfig.Load();
        var preset = ClientConfig.Providers.OpenRouter();

        config.ApplyConnection(preset);

        Assert.Equal(preset.Url, config.BaseUrl);
        Assert.Equal(preset.Model, config.Model);
        Assert.Equal("openrouter", config.ConnectionName);
        Assert.Equal("openrouter", config.ActiveConnection?.Name);
    }

    [Fact]
    public void ApplyingAConnectionClearsTheSavedGatewayName()
    {
        var config = ClientConfig.Load();
        config.ServerName = "my-gateway";
        config.ServerLabel.ToString();

        config.ApplyConnection(ClientConfig.Providers.DeepSeek());

        // Both name spaces label the same address; only one may be in charge.
        Assert.Equal("", config.ServerName);
    }

    [Fact]
    public void TheActiveConnectionIsNullWhenTheAddressWasTypedByHand()
    {
        var config = ClientConfig.Load();
        config.BaseUrl = "http://127.0.0.1:9656";
        Assert.Null(config.ActiveConnection);
    }

    [Fact]
    public void TheActiveConnectionSurvivesASaveAndReload()
    {
        var config = ClientConfig.Load();
        config.ApplyConnection(ClientConfig.Providers.Google());
        config.Save();

        var reloaded = ClientConfig.Load();
        Assert.Equal("google", reloaded.ActiveConnection?.Name);
    }

    [Fact]
    public void EachProviderKeepsItsOwnKey()
    {
        ClientConfig.SaveSecret("apiKey:openrouter", "or-key");
        ClientConfig.SaveSecret("apiKey:deepseek", "ds-key");

        Assert.Equal("or-key", ClientConfig.GetSecret("apiKey:openrouter"));
        Assert.Equal("ds-key", ClientConfig.GetSecret("apiKey:deepseek"));
        Assert.Null(ClientConfig.GetSecret("apiKey:google"));
    }

    [Fact]
    public void SavingOneKeyLeavesTheOthersAlone()
    {
        ClientConfig.SaveSecret("apiKey:openrouter", "or-key");
        ClientConfig.SaveSecret("apiKey:deepseek", "ds-key");

        Assert.True(ClientConfig.DeleteSecret("apiKey:openrouter"));
        Assert.Null(ClientConfig.GetSecret("apiKey:openrouter"));
        Assert.Equal("ds-key", ClientConfig.GetSecret("apiKey:deepseek"));
        Assert.False(ClientConfig.DeleteSecret("apiKey:openrouter"));
    }

    [Fact]
    public void SecretsAreStoredOutsideTheReadableConfiguration()
    {
        ClientConfig.SaveSecret("apiKey:openrouter", "or-key");

        var config = ClientConfig.Load();
        config.Save();

        var text = File.ReadAllText(ClientConfig.ConfigPath);
        Assert.DoesNotContain("or-key", text);
        Assert.True(File.Exists(ClientConfig.SecretsPath));
    }

    [Fact]
    public void ACorruptSecretsFileDoesNotStopTheClient()
    {
        File.WriteAllText(ClientConfig.SecretsPath, "{ not json at all");
        Assert.Empty(ClientConfig.LoadSecrets());
    }

    public void Dispose()
    {
        ClientConfig.ConfigDir = _configDirBefore;
        ClientConfig.ConfigPath = _configPathBefore;
        ClientConfig.SecretsPath = _secretsPathBefore;

        try { Directory.Delete(_dir, true); } catch { }
    }
}
