using IntellectHub.Client;
using Xunit;

namespace IntellectHub.Tests;

/// <summary>
/// Запись артефактов модов.
///
/// Пустая строка вместо пути — это ошибка модели, а не путь. Раньше она роняла
/// весь ход исключением "The value cannot be an empty string. (Parameter 'path')",
/// потому что артефакт записывался до того, как инструмент успевал проверить
/// аргументы. Теперь такой вызов просто игнорируется, а инструмент возвращает
/// модели внятную ошибку.
/// </summary>
public sealed class ModeArtifactTests
{
    private sealed class TempProject : IDisposable
    {
        public string Root { get; }
        public Project Project { get; }
        public ModeStore Store { get; }
        public ModeManager Manager { get; }

        public TempProject(string subFolder)
        {
            Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ih-artifact-test", subFolder);
            if (System.IO.Directory.Exists(Root))
                System.IO.Directory.Delete(Root, true);
            System.IO.Directory.CreateDirectory(Root);
            Project = Project.Open(Root);
            Store = new ModeStore(Project);
            Manager = new ModeManager(Project, Store);
        }

        public void Dispose()
        {
            Manager.Persist();
            try { System.IO.Directory.Delete(Root, true); } catch { }
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]
    public void AnEmptyArtifactPathIsIgnoredInsteadOfThrowing(string path)
    {
        using var project = new TempProject("empty-path");

        project.Manager.RecordArtifact(Mode.Code, path, "content");

        Assert.False(project.Manager.State.CodeReady);
    }

    [Fact]
    public void ARelativeArtifactIsWrittenUnderTheProjectRoot()
    {
        using var project = new TempProject("relative");

        project.Manager.RecordArtifact(Mode.Code, "index.html", "<html></html>");

        Assert.True(File.Exists(System.IO.Path.Combine(project.Root, "index.html")));
        Assert.True(project.Manager.State.CodeReady);
    }

    [Fact]
    public void ARootRelativePathMeansTheProjectRootNotTheDriveRoot()
    {
        using var project = new TempProject("root-relative");

        project.Manager.RecordArtifact(Mode.Code, "/index.html", "<html></html>");

        Assert.True(File.Exists(System.IO.Path.Combine(project.Root, "index.html")));
    }

    [Fact]
    public void APathClimbingOutOfTheProjectIsRefused()
    {
        using var project = new TempProject("escape");

        project.Manager.RecordArtifact(Mode.Code, "../escape.txt", "x");

        Assert.False(File.Exists(System.IO.Path.Combine(project.Root, "..", "escape.txt")));
        Assert.False(project.Manager.State.CodeReady);
    }

    [Fact]
    public void TryReadArtifactReadsOnlyInsideTheProject()
    {
        using var project = new TempProject("read");

        Assert.False(project.Store.TryReadArtifact("missing.html", out _));
        Assert.False(project.Store.TryReadArtifact("../escape.txt", out _));

        File.WriteAllText(System.IO.Path.Combine(project.Root, "index.html"), "<html></html>");
        Assert.True(project.Store.TryReadArtifact("index.html", out var text));
        Assert.Equal("<html></html>", text);
    }
}
