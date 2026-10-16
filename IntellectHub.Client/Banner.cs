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

        var info = new Markup(
            $"[bold white]I N T E L L E C T   H U B[/] [grey]v{Version}[/]\n" +
            $"[grey]server [/] [silver]{Markup.Escape(config.BaseUrl)}[/]  [grey]model[/] [silver]{Markup.Escape(config.Model)}[/]\n" +
            $"[grey]project[/] [silver]{Markup.Escape(project.Root)}[/]");

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
}
