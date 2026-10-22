using System.Text.Json;
using Xunit;

namespace IntellectHub.Tests;

/// <summary>
/// Тесты ToolRegistry для bash инструмента.
/// </summary>
public sealed class ToolRegistryBashTests
{
    private string CreateTempProject()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ih-bash-test",
            Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public void Bash_Returns_Command_Output()
    {
        var root = CreateTempProject();
        try
        {
            var registry = new IntellectHub.Client.ToolRegistry(root);
            var result = registry.ExecuteAsync(new IntellectHub.Client.ToolCall
            {
                Name = "bash",
                Arguments = "{\"command\":\"echo hello world\"}"
            }).Result;

            Assert.False(result.IsError);
            Assert.Contains("hello", result.Output);
            Assert.Contains("world", result.Output);
        }
        finally
        {
            System.IO.Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Bash_Checks_File_Existence()
    {
        var root = CreateTempProject();
        try
        {
            var registry = new IntellectHub.Client.ToolRegistry(root);

            // Файл не существует
            var result1 = registry.ExecuteAsync(new IntellectHub.Client.ToolCall
            {
                Name = "bash",
                Arguments = "{\"command\":\"if (Test-Path styles.css) { 'EXISTS' } else { 'NO_FILE' }\"}"
            }).Result;
            Assert.Contains("NO_FILE", result1.Output);

            // Создаём файл
            System.IO.File.WriteAllText(System.IO.Path.Combine(root, "styles.css"), "body{}");

            // Файл существует
            var result2 = registry.ExecuteAsync(new IntellectHub.Client.ToolCall
            {
                Name = "bash",
                Arguments = "{\"command\":\"if (Test-Path styles.css) { 'EXISTS' } else { 'NO_FILE' }\"}"
            }).Result;
            Assert.Contains("EXISTS", result2.Output);
        }
        finally
        {
            System.IO.Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Bash_Checks_File_Content_Length()
    {
        var root = CreateTempProject();
        try
        {
            var registry = new IntellectHub.Client.ToolRegistry(root);
            System.IO.File.WriteAllText(System.IO.Path.Combine(root, "test.txt"), "hello");

            var result = registry.ExecuteAsync(new IntellectHub.Client.ToolCall
            {
                Name = "bash",
                Arguments = "{\"command\":\"if (Test-Path test.txt) { (Get-Content test.txt -Raw).Length } else { 0 }\"}"
            }).Result;

            Assert.Contains("5", result.Output);
        }
        finally
        {
            System.IO.Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Bash_Command_Error_Returns_IsError()
    {
        var root = CreateTempProject();
        try
        {
            var registry = new IntellectHub.Client.ToolRegistry(root);

            var result = registry.ExecuteAsync(new IntellectHub.Client.ToolCall
            {
                Name = "bash",
                Arguments = "{\"command\":\"& { throw 'command failed' }\"}"
            }).Result;

            Assert.True(result.IsError);
            Assert.NotEmpty(result.Output);
        }
        finally
        {
            System.IO.Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Bash_Empty_Command_Returns_No_Crash()
    {
        var root = CreateTempProject();
        try
        {
            var registry = new IntellectHub.Client.ToolRegistry(root);

            var result = registry.ExecuteAsync(new IntellectHub.Client.ToolCall
            {
                Name = "bash",
                Arguments = "{\"command\":\"\"}"
            }).Result;

            // Пустая команда не должна уронить клиент
            Assert.True(result.Output.Length >= 0);
        }
        finally
        {
            System.IO.Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ToolSpec_Includes_Bash()
    {
        var root = CreateTempProject();
        try
        {
            var registry = new IntellectHub.Client.ToolRegistry(root);
            var bashSpec = registry.Specs.FirstOrDefault(s => s.Name == "bash");

            Assert.NotNull(bashSpec);
            Assert.Contains("command", bashSpec.Description);
        }
        finally
        {
            System.IO.Directory.Delete(root, true);
        }
    }
}
