using Xunit;
using System.Text.Json;
using IntellectHub.Client;

// The client and the gateway each define a ToolCall; this suite is about the
// client's, so the type is named rather than left to whichever wins.
using ClientToolCall = IntellectHub.Client.ToolCall;

namespace IntellectHub.Tests;

/// <summary>
/// Where a tool call's paths land.
///
/// The path guard exists to keep the agent inside its project, and it fired on a
/// path that should have been fine: the model asked for "/index.html", meaning the
/// project root, and Windows read that as the drive root instead. Every write to
/// a root-relative path failed with "Path escapes workspace".
/// </summary>
public sealed class ToolPathTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "intellecthub-tests", Guid.NewGuid().ToString("N"));

    public ToolPathTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private ToolResult Write(string path)
    {
        var arguments = JsonSerializer.Serialize(new { path, content = "x" });
        return new ToolRegistry(_root)
            .ExecuteAsync(new ClientToolCall { Name = "write", Arguments = arguments })
            .GetAwaiter().GetResult();
    }

    [Theory]
    [InlineData("/index.html")]
    [InlineData("index.html")]
    [InlineData("./index.html")]
    [InlineData("assets/app.js")]
    [InlineData("/assets/app.js")]
    [InlineData("src\\deep\\file.txt")]
    public void RootRelativePathsLandInsideTheProject(string path)
    {
        var result = Write(path);

        Assert.False(result.IsError, $"{path} was refused: {result.Output}");
    }

    [Fact]
    public void ARootRelativePathIsWrittenUnderTheWorkspaceNotTheDriveRoot()
    {
        Write("/index.html");

        // The exact failure: Path.Combine discarded the workspace on a leading
        // separator and the write landed at C:\index.html.
        Assert.True(File.Exists(Path.Combine(_root, "index.html")));
        Assert.False(File.Exists(Path.Combine(Path.GetPathRoot(_root)!, "index.html")));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("../../escape.txt")]
    [InlineData("a/../../escape.txt")]
    public void ClimbingOutIsStillRefused(string path)
    {
        var result = Write(path);

        Assert.True(result.IsError, $"{path} should have been refused");
        Assert.Contains("escapes workspace", result.Output);
    }

    [Fact]
    public void AnAbsolutePathOutsideTheWorkspaceIsRefused()
    {
        var outside = Path.Combine(Path.GetTempPath(), "intellecthub-outside-" + Guid.NewGuid().ToString("N")[..6] + ".txt");
        var result = Write(outside);

        Assert.True(result.IsError);
        Assert.Contains("escapes workspace", result.Output);
    }

    [Fact]
    public void ASiblingDirectorySharingThePrefixIsNotInside()
    {
        // "…\proj-evil" must not pass a check written as "starts with …\proj".
        var sibling = _root + "-evil";
        Directory.CreateDirectory(sibling);
        try
        {
            var result = Write(Path.Combine(sibling, "x.txt"));
            Assert.True(result.IsError);
        }
        finally
        {
            try { Directory.Delete(sibling, true); } catch { }
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]
    public void AnEmptyPathIsRejectedRatherThanWritingSomewhereOdd(string path)
    {
        Assert.True(Write(path).IsError);
    }

    [Fact]
    public void ReadingOutsideTheWorkspaceIsRefusedToo()
    {
        var result = new ToolRegistry(_root)
            .ExecuteAsync(new ClientToolCall { Name = "read", Arguments = """{"path":"../../secrets.txt"}""" })
            .GetAwaiter().GetResult();

        Assert.True(result.IsError);
        Assert.Contains("escapes workspace", result.Output);
    }
}