using IntellectHub;
using Xunit;

namespace IntellectHub.Tests;

public class ModelRegistryTests
{
    [Theory]
    [InlineData(null, "deepseek-v4-flash")]
    [InlineData("", "deepseek-v4-flash")]
    [InlineData("  ", "deepseek-v4-flash")]
    [InlineData("DEEPSEEK-V4-FLASH", "deepseek-v4-flash")]
    [InlineData("deepseek-flash", "deepseek-v4-flash")]
    [InlineData("deepseek-flash-thinking", "deepseek-v4-flash-thinking")]
    [InlineData("deepseek-v4-flash[1m]", "deepseek-v4-flash")]
    [InlineData("deepseek-v4-flash-search", "deepseek-v4-flash")]
    [InlineData("claude-3-opus", "deepseek-v4-flash-thinking")]
    [InlineData("claude-sonnet-4", "deepseek-v4-flash")]
    [InlineData("claude-haiku", "deepseek-v4-flash")]
    public void Canonicalize_MapsAliases(string? input, string expected)
    {
        Assert.Equal(expected, ModelRegistry.Canonicalize(input));
    }

    [Fact]
    public void Resolve_UnknownModel_FallsBackToDefault()
    {
        var cfg = ModelRegistry.Resolve("totally-unknown-model");
        Assert.Equal("default", cfg.ModelType);
        Assert.True(cfg.SearchEnabled);
    }

    [Theory]
    [InlineData("deepseek-v4-flash", false, true)]
    [InlineData("deepseek-v4-flash-thinking", true, true)]
    [InlineData("deepseek-v4-flash-nosearch", false, false)]
    [InlineData("deepseek-v4-flash-thinking-nosearch", true, false)]
    public void Resolve_FlagsMatchTable(string id, bool thinking, bool search)
    {
        var cfg = ModelRegistry.Resolve(id);
        Assert.Equal(thinking, cfg.ThinkingEnabled);
        Assert.Equal(search, cfg.SearchEnabled);
    }

    [Fact]
    public void SupportedIds_ReturnsAllFour()
    {
        Assert.Equal(4, ModelRegistry.SupportedIds.Count);
    }

    [Fact]
    public void IsKnown_DetectsKnownAndUnknown()
    {
        Assert.True(ModelRegistry.IsKnown("deepseek-v4-flash"));
        Assert.False(ModelRegistry.IsKnown("gpt-4o"));
    }
}
