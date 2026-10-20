using System.Text.Json;
using IntellectHub.Client;
using Xunit;

namespace IntellectHub.Tests;

/// <summary>
/// Config persistence. Environment variables are runtime overrides, not settings:
/// a run with IH_BASE_URL set must not rewrite the stored address, or the next
/// plain launch points at a server that was only meant for one test run.
/// </summary>
public sealed class ConfigPersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ih-config-" + Guid.NewGuid().ToString("N"));
    private readonly string _configDirBefore = ClientConfig.ConfigDir;
    private readonly string _configPathBefore = ClientConfig.ConfigPath;

    public ConfigPersistenceTests()
    {
        Directory.CreateDirectory(_dir);
        ClientConfig.ConfigDir = _dir;
        ClientConfig.ConfigPath = Path.Combine(_dir, "client.json");
    }

    [Fact]
    public void EnvironmentOverridesAreNotSavedToTheFile()
    {
        File.WriteAllText(ClientConfig.ConfigPath, """
            {
              "baseUrl": "http://127.0.0.1:9656",
              "model": "stored-model",
              "apiKey": "stored-key"
            }
            """);

        Environment.SetEnvironmentVariable("IH_BASE_URL", "http://127.0.0.1:1");
        Environment.SetEnvironmentVariable("IH_MODEL", "env-model");
        Environment.SetEnvironmentVariable("IH_API_KEY", "env-key");
        try
        {
            var config = ClientConfig.Load();
            Assert.Equal("http://127.0.0.1:1", config.BaseUrl);
            Assert.Equal("env-model", config.Model);
            Assert.Equal("env-key", config.ApiKey);

            // Something unrelated changes at runtime and triggers a save.
            config.Project = "C:/work";
            config.Save();

            var saved = JsonSerializer.Deserialize<ClientConfig>(File.ReadAllText(ClientConfig.ConfigPath))!;
            Assert.Equal("http://127.0.0.1:9656", saved.BaseUrl);
            Assert.Equal("stored-model", saved.Model);
            Assert.Equal("stored-key", saved.ApiKey);
            Assert.Equal("C:/work", saved.Project);
        }
        finally
        {
            Environment.SetEnvironmentVariable("IH_BASE_URL", null);
            Environment.SetEnvironmentVariable("IH_MODEL", null);
            Environment.SetEnvironmentVariable("IH_API_KEY", null);
        }
    }

    [Fact]
    public void ARuntimeChangeToAnOverriddenValueIsSaved()
    {
        File.WriteAllText(ClientConfig.ConfigPath, """
            { "baseUrl": "http://127.0.0.1:9656" }
            """);

        Environment.SetEnvironmentVariable("IH_BASE_URL", "http://127.0.0.1:1");
        try
        {
            var config = ClientConfig.Load();

            // The user switched servers inside the client; that is a real change.
            config.BaseUrl = "http://127.0.0.1:9657";
            config.Save();

            var saved = JsonSerializer.Deserialize<ClientConfig>(File.ReadAllText(ClientConfig.ConfigPath))!;
            Assert.Equal("http://127.0.0.1:9657", saved.BaseUrl);
        }
        finally
        {
            Environment.SetEnvironmentVariable("IH_BASE_URL", null);
        }
    }

    public void Dispose()
    {
        ClientConfig.ConfigDir = _configDirBefore;
        ClientConfig.ConfigPath = _configPathBefore;

        try { Directory.Delete(_dir, true); } catch { }
    }
}
