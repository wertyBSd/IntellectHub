using System.Drawing.Drawing2D;

namespace IntellectHub;

internal sealed class MainForm : Form
{
    private const int MaxLogLines = 2000;

    private readonly AppConfig _config = AppConfig.Load();
    private readonly ProxyServer _server;
    private readonly NotifyIcon _tray;

    private readonly RichTextBox _log = new();
    private readonly TextBox _host = new();
    private readonly TextBox _port = new();
    private readonly TextBox _apiKey = new();
    private readonly CheckBox _autoStart = new();
    private readonly CheckBox _minimizeToTray = new();
    private readonly CheckBox _startMinimized = new();
    private readonly CheckBox _autoScroll = new();
    private readonly Label _status = new();
    private readonly Button _toggle = new();

    private SplitContainer _split = null!;
    private bool _reallyExit;
    private bool _startHidden;
    private bool _splitReady;

    public MainForm()
    {
        _server = new ProxyServer(_config);
        _startHidden = _config.StartMinimized;

        Text = "IntellectHub";
        MinimumSize = new Size(760, 560);
        Size = new Size(880, 620);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = Theme.UiFont;
        DoubleBuffered = true;

        _tray = new NotifyIcon
        {
            Icon = BuildTrayIcon(),
            Visible = true,
            Text = "IntellectHub"
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Открыть", null, (_, _) => RestoreWindow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Закрыть", null, (_, _) => ExitApp());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => RestoreWindow();

        if (_startHidden)
        {
            WindowState = FormWindowState.Minimized;
            ShowInTaskbar = false;
        }

        BuildLayout();
        CreateHandle();

        AppLog.Written += OnLog;
        AppLog.Info("IntellectHub запущен");

        if (_config.AutoStartServer)
            StartServer();
        else
            AppLog.Muted("Автозапуск сервера отключён");
    }

    private void BuildLayout()
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 52, BackColor = Theme.Panel };
        header.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1);
        };

        var title = new Label
        {
            Text = "IntellectHub",
            Font = Theme.TitleFont,
            ForeColor = Theme.Text,
            AutoSize = true,
            Location = new Point(18, 15)
        };
        _status.AutoSize = true;
        _status.Location = new Point(160, 18);
        _status.Font = Theme.UiFont;

        _toggle.Text = "Стоп";
        _toggle.FlatStyle = FlatStyle.Flat;
        _toggle.FlatAppearance.BorderColor = Theme.Border;
        _toggle.BackColor = Theme.PanelAlt;
        _toggle.ForeColor = Theme.Text;
        _toggle.Size = new Size(96, 30);
        _toggle.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _toggle.Location = new Point(header.Width - 114, 11);
        _toggle.Click += (_, _) => ToggleServer();

        header.Controls.AddRange(new Control[] { title, _status, _toggle });
        header.Resize += (_, _) => _toggle.Left = header.Width - _toggle.Width - 18;

        _split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = 1,
            BackColor = Theme.Border,
            FixedPanel = FixedPanel.Panel1
        };
        _split.Panel1.BackColor = Theme.Panel;
        _split.Panel2.BackColor = Theme.Background;

        _split.Panel1.Controls.Add(BuildSettings());
        _split.Panel2.Controls.Add(BuildConsole());

        Controls.Add(_split);
        Controls.Add(header);

        Load += (_, _) => UpdateStatus();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_splitReady) return;

        var max = _split.Width - _split.Panel2MinSize;
        if (max > _split.Panel1MinSize)
        {
            _split.SplitterDistance = Math.Clamp(300, _split.Panel1MinSize, max);
            _splitReady = true;
        }
    }

    private Control BuildSettings()
    {
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(18, 16, 18, 16),
            BackColor = Theme.Panel
        };

        flow.Controls.Add(SectionLabel("СЕРВЕР"));

        _host.Text = _config.Host;
        _port.Text = _config.Port.ToString();
        _apiKey.Text = _config.ApiKey;
        _apiKey.UseSystemPasswordChar = true;

        flow.Controls.Add(Field("Хост", _host));
        flow.Controls.Add(Field("Порт", _port));
        flow.Controls.Add(Field("API-ключ", _apiKey));

        flow.Controls.Add(SectionLabel("ПОВЕДЕНИЕ"));

        _autoStart.Text = "Запускать сервер при старте";
        _minimizeToTray.Text = "Сворачивать в трей при закрытии";
        _startMinimized.Text = "Запускать свёрнутым в трей";
        _autoScroll.Text = "Автопрокрутка лога";

        _autoStart.Checked = _config.AutoStartServer;
        _minimizeToTray.Checked = _config.MinimizeToTray;
        _startMinimized.Checked = _config.StartMinimized;
        _autoScroll.Checked = _config.AutoScrollLog;

        foreach (var cb in new[] { _autoStart, _minimizeToTray, _startMinimized, _autoScroll })
        {
            cb.ForeColor = Theme.Text;
            cb.FlatStyle = FlatStyle.Flat;
            cb.AutoSize = true;
            cb.Margin = new Padding(0, 4, 0, 4);
            flow.Controls.Add(cb);
        }

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            Margin = new Padding(0, 14, 0, 0)
        };

        var login = new Button
        {
            Text = "Аккаунты",
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.PanelAlt,
            ForeColor = Theme.Text,
            Size = new Size(120, 30),
            Margin = new Padding(0, 0, 8, 0)
        };
        login.FlatAppearance.BorderColor = Theme.Border;
        login.Click += (_, _) => OpenLogin();

        var save = new Button
        {
            Text = "Сохранить",
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.PanelAlt,
            ForeColor = Theme.Text,
            Size = new Size(120, 30),
            Margin = new Padding(0)
        };
        save.FlatAppearance.BorderColor = Theme.Border;
        save.Click += (_, _) => SaveSettings();

        buttons.Controls.Add(login);
        var keys = new Button
        {
            Text = "Ключи",
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.PanelAlt,
            ForeColor = Theme.Text,
            Size = new Size(120, 30),
            Margin = new Padding(0, 0, 8, 0)
        };
        keys.FlatAppearance.BorderColor = Theme.Border;
        keys.Click += (_, _) => OpenKeys();

        buttons.Controls.Add(keys);
        buttons.Controls.Add(save);
        flow.Controls.Add(buttons);

        return flow;
    }

    private Control BuildConsole()
    {
        _log.Dock = DockStyle.Fill;
        _log.BackColor = Theme.Background;
        _log.ForeColor = Theme.Text;
        _log.Font = Theme.MonoFont;
        _log.BorderStyle = BorderStyle.None;
        _log.ReadOnly = true;
        _log.WordWrap = false;
        _log.ScrollBars = RichTextBoxScrollBars.Both;
        _log.DetectUrls = false;

        var wrap = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 12, 14, 14), BackColor = Theme.Background };
        wrap.Controls.Add(_log);
        return wrap;
    }

    private static Label SectionLabel(string text) => new()
    {
        Text = text,
        ForeColor = Theme.Muted,
        Font = Theme.UiBold,
        AutoSize = true,
        Margin = new Padding(0, 12, 0, 6)
    };

    private static Control Field(string label, TextBox box)
    {
        var wrap = new Panel { Width = 240, Height = 48, Margin = new Padding(0, 2, 0, 2) };
        var lbl = new Label { Text = label, ForeColor = Theme.Muted, AutoSize = true, Location = new Point(0, 0) };
        box.Location = new Point(0, 20);
        box.Width = 240;
        box.BackColor = Theme.PanelAlt;
        box.ForeColor = Theme.Text;
        box.BorderStyle = BorderStyle.FixedSingle;
        wrap.Controls.Add(lbl);
        wrap.Controls.Add(box);
        return wrap;
    }

    private void OnLog(LogLevel level, string message)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(() => OnLog(level, message)); }
            catch (InvalidOperationException) { }
            return;
        }

        var color = level switch
        {
            LogLevel.Ok => Theme.Green,
            LogLevel.Warn => Theme.Yellow,
            LogLevel.Error => Theme.Red,
            LogLevel.Muted => Theme.Muted,
            _ => Theme.Text
        };
        var prefix = level switch
        {
            LogLevel.Ok => "  ok ",
            LogLevel.Warn => "warn ",
            LogLevel.Error => " err ",
            LogLevel.Muted => "     ",
            _ => " info"
        };
        var stamp = DateTime.Now.ToString("HH:mm:ss");

        _log.SelectionStart = _log.TextLength;
        _log.SelectionLength = 0;
        _log.SelectionColor = Theme.Muted;
        _log.AppendText($"{stamp} ");
        _log.SelectionColor = color;
        _log.AppendText($"[{prefix}] {message}\n");

        if (_log.Lines.Length > MaxLogLines)
        {
            var cut = _log.Lines.Length - MaxLogLines;
            var first = _log.GetFirstCharIndexFromLine(cut);
            if (first > 0)
            {
                _log.Select(0, first);
                _log.SelectedText = string.Empty;
            }
        }

        if (_config.AutoScrollLog && _autoScroll.Checked)
            _log.ScrollToCaret();
    }

    private void ToggleServer()
    {
        if (_server.IsRunning)
        {
            _server.Stop();
            AppLog.Warn("Сервер остановлен");
        }
        else
        {
            StartServer();
        }
        UpdateStatus();
    }

    private void StartServer()
    {
        _config.Host = _host.Text.Trim();
        _config.Port = int.TryParse(_port.Text.Trim(), out var p) ? p : _config.Port;

        try
        {
            _server.Start();
            AppLog.Ok($"Сервер слушает http://{_config.Host}:{_config.Port}");
        }
        catch (Exception ex)
        {
            AppLog.Error($"Не удалось запустить сервер: {ex.Message}");
        }
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var running = _server.IsRunning;
        _status.Text = running ? "\u25cf онлайн" : "\u25cf офлайн";
        _status.ForeColor = running ? Theme.Green : Theme.Muted;
        _toggle.Text = running ? "Стоп" : "Старт";
    }

    private void OpenLogin()
    {
        var target = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "IntellectHub",
            _config.AuthPath);

        using var login = new LoginForm(target);
        if (login.ShowDialog(this) == DialogResult.OK)
            AppLog.Ok($"Аккаунт добавлен: {Path.GetFileName(target)}");
    }
    private void OpenKeys()
    {
        using var keys = new KeysForm(_server.ApiKeys);
        keys.ShowDialog(this);
    }

    private void SaveSettings()
    {
        _config.Host = _host.Text.Trim();
        _config.Port = int.TryParse(_port.Text.Trim(), out var p) ? p : _config.Port;
        _config.ApiKey = _apiKey.Text;
        _config.AutoStartServer = _autoStart.Checked;
        _config.MinimizeToTray = _minimizeToTray.Checked;
        _config.StartMinimized = _startMinimized.Checked;
        _config.AutoScrollLog = _autoScroll.Checked;
        _config.Save();
        AppLog.Ok("Настройки сохранены");
    }

        private void RestoreWindow()
    {
        if (_reallyExit || IsDisposed || Disposing) return;
        ShowInTaskbar = true;
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    protected override void SetVisibleCore(bool value)
    {
        if (_startHidden)
        {
            _startHidden = false;
            base.SetVisibleCore(false);
            return;
        }
        base.SetVisibleCore(value);
    }

        private void ExitApp()
    {
        if (_reallyExit) return;
        _reallyExit = true;

        if (IsDisposed || Disposing)
        {
            _tray.Visible = false;
            Application.Exit();
            return;
        }

        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_reallyExit && _config.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            _tray.ShowBalloonTip(1500, "IntellectHub", "Свёрнуто в трей. Правый клик по иконке — закрыть.", ToolTipIcon.Info);
            return;
        }

        _server.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        base.OnFormClosing(e);
    }

    private static Icon BuildTrayIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var brush = new LinearGradientBrush(
                new Rectangle(0, 0, 32, 32), Theme.Accent, Color.FromArgb(70, 90, 190), 45f);
            g.FillEllipse(brush, 2, 2, 28, 28);
            using var font = new Font("Segoe UI", 15f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("i", font, Brushes.White, new RectangleF(2, 2, 28, 28), sf);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
