using Spectre.Console;
using Spectre.Console.Rendering;

namespace IntellectHub.Client;

public static class Banner
{
    public const string Version = "0.3.1";

    private static readonly string[] Logo =
    {
        "╲   │   ╱",
        "────◆────",
        "╱   │   ╲",
    };

    public static IRenderable Header(ClientConfig config, Project project)
    {
        var logo = new Markup(string.Join('\n', Logo.Select(line => $"[aqua]{Markup.Escape(line)}[/]")));

        // The folder name, not the whole path. A full path is long, repeats on every
        // redraw, and the tail is the only part that tells two projects apart.
        var info = new Markup(
            $"[bold white]I N T E L L E C T   H U B[/] [grey]v{Version}[/]\n" +
            $"[grey]server [/] [gold1]{Markup.Escape(config.ServerLabel)}[/] [grey]{Markup.Escape(config.BaseUrl)}[/]  " +
            $"[grey]model[/] [silver]{Markup.Escape(config.Model)}[/]\n" +
            $"[grey]project[/] [silver]{Markup.Escape(ProjectLabel(project.Root))}[/]");

        var grid = new Grid();
        grid.AddColumn(new GridColumn().NoWrap().Padding(0, 0, 2, 0));
        grid.AddColumn(new GridColumn());
        grid.AddRow(logo, info);

        return new Panel(grid)
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Gold1),
            Padding = new Padding(1, 0, 1, 0),
        };
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
