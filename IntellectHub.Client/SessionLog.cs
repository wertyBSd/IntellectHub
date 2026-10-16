namespace IntellectHub.Client;

public sealed class SessionLog
{
    private readonly string _path;

    public SessionLog(string path)
    {
        _path = path;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (!File.Exists(path))
            File.AppendAllText(path, "# IntellectHub session log\n");
    }

    public void User(string text) => Append($"\n### You\n\n{text}\n");

    public void Thought(string text) => Append($"\n### Thought\n\n{text}\n");

    public void Action(string name, string arguments) =>
        Append($"\n### Action `{name}`\n\n```json\n{arguments}\n```\n");

    public void Result(string output) => Append($"\n### Result\n\n```\n{output}\n```\n");

    private void Append(string markdown) => File.AppendAllText(_path, markdown);
}
