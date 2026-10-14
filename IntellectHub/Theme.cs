namespace IntellectHub;

internal static class Theme
{
    public static readonly Color Background = Color.FromArgb(13, 15, 20);
    public static readonly Color Panel = Color.FromArgb(20, 23, 30);
    public static readonly Color PanelAlt = Color.FromArgb(16, 19, 25);
    public static readonly Color Border = Color.FromArgb(38, 43, 54);
    public static readonly Color Text = Color.FromArgb(214, 220, 232);
    public static readonly Color Muted = Color.FromArgb(122, 130, 148);
    public static readonly Color Accent = Color.FromArgb(94, 129, 244);
    public static readonly Color Green = Color.FromArgb(126, 200, 130);
    public static readonly Color Yellow = Color.FromArgb(226, 192, 110);
    public static readonly Color Red = Color.FromArgb(230, 118, 118);

    public static readonly Font UiFont = new("Segoe UI", 9f);
    public static readonly Font UiBold = new("Segoe UI", 9f, FontStyle.Bold);
    public static readonly Font TitleFont = new("Segoe UI Semibold", 11f);
    public static readonly Font MonoFont = new("Cascadia Mono", 9.5f);
}
