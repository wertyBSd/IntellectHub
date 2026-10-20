using Spectre.Console;
using Spectre.Console.Rendering;
using SpectreColor = Spectre.Console.Color;

namespace IntellectHub.Client;

public static class Banner
{
    public const string Version = "0.4.0";

    /// <summary>
    /// A compact, quiet header: identity on the left, where it is talking to on the
    /// right. The old boxed banner repeated the server and project on every frame;
    /// this keeps one line for each so the conversation gets the height instead.
    /// </summary>
    public static IRenderable Header(ClientConfig config, Project project)
    {
        var title = new Markup(
            $"[gold1]✻[/] [bold white]Intellect Hub[/] [grey]v{Version}[/]   " +
            $"[silver]{Markup.Escape(config.Model)}[/] [grey]·[/] " +
            $"[silver]{Markup.Escape(ProjectLabel(project.Root))}[/]");

        var endpoint = new Markup(
            $"[grey]{Markup.Escape(config.ServerLabel)}[/] [grey]·[/] " +
            $"[grey]{Markup.Escape(config.BaseUrl)}[/]");

        return new Rows(title, endpoint, new Rule { Style = new Style(SpectreColor.Grey23) });
    }

    /// <summary>
    /// The last folder in a path, with enough of its parents to stay recognisable
    /// when two workspaces share a folder name.
    /// </summary>
    public static string ProjectLabel(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return "(none)";

        var separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
        var trimmed = root.TrimEnd(separators);
        if (trimmed.Length == 0) return root;

        // A drive prefix is not a folder. Without this, "C:\work" labels as
        // "C:/work" and a bare "C:\" loses its root separator entirely.
        var rest = trimmed.Length >= 2 && trimmed[1] == ':'
            ? trimmed[2..].TrimStart(separators)
            : trimmed;

        var parts = rest.Split(separators, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return trimmed + Path.DirectorySeparatorChar;
        if (parts.Length == 1) return parts[0];

        return $"{parts[^2]}/{parts[^1]}";
    }
}
