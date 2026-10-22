namespace IntellectHub.Client;

public sealed class Project
{
    public const string FolderName = ".IntellectHub";

    public string Root { get; }
    public string HubDir { get; }

    private Project(string root)
    {
        Root = root;
        HubDir = Path.Combine(root, FolderName);
    }

    public static Project Open(string path)
    {
        var root = Path.GetFullPath(path);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"project folder not found: {root}");

        var project = new Project(root);
        Directory.CreateDirectory(project.HubDir);
        return project;
    }

    public string LogPath => Path.Combine(HubDir, "agent_log.md");
}
