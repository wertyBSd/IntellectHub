using System.Drawing.Drawing2D;

namespace IntellectHub;

internal sealed class MainForm : Form
{
    private const int LogFlushMs = 150;
    private const int RefreshMs = 1500;

    private readonly AppConfig _config = AppConfig.Load();
    private readonly ProxyServer _server;
    private readonly NotifyIcon _tray;
    private readonly LogBuffer _logBuffer = new();

    // Server
    private readonly TextBox _host = new();
    private readonly TextBox _port = new();
    private readonly TextBox _apiKey = new();

    // Behaviour
    private readonly CheckBox _autoStart = new();
    private readonly CheckBox _minimizeToTray = new();
    private readonly CheckBox _startMinimized = new();

    // Throttling
    private readonly TextBox _minInterval = new();
    private readonly TextBox _maxConcurrent = new();
    private readonly TextBox _cooldown = new();
    private readonly TextBox _lockWait = new();
    private readonly TextBox _upstreamTimeout = new();
    private readonly TextBox _deadline = new();

    // Prompt
    private readonly TextBox _maxPromptChars = new();
    private readonly TextBox _maxRetries = new();

    // Console
    private readonly RichTextBox _log = new();
    private readonly LogFilter _filter = new();
    private readonly CheckBox _fInfo = new();
    private readonly CheckBox _fOk = new();
    private readonly CheckBox _fWarn = new();
    private readonly CheckBox _fError = new();
    private readonly CheckBox _fMuted = new();
    private readonly TextBox _search = new();
    private readonly CheckBox _autoScroll = new();
    private readonly Label _logStats = new();
    private readonly Label _problems = new();

    // Accounts
    private readonly ListView _accounts = new();

    private readonly Label _status = new();
    private readonly Label _poolStatus = new();
    private readonly Button _toggle = new();

    private readonly Queue<(LogLevel Level, string Text)> _pending = new();
    private readonly System.Windows.Forms.Timer _flushTimer = new() { Interval = LogFlushMs };
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = RefreshMs };

    private readonly Dictionary<string, TextBox> _numericFields = new();
    private readonly HashSet<string> _invalid = new();

    private bool _reallyExit;
    private bool _startHidden;
    private bool _splitReady;
    private bool _rendering;

    /// <summary>How many filtered entries the control is currently showing.</summary>
    private int _renderedCount;

    public MainForm()
    {
        _server = new ProxyServer(_config);
        _startHidden = _config.StartMinimized;

        Text = "IntellectHub";
        MinimumSize = new Size(1000, 680);
        Size = new Size(1220, 840);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = Theme.UiFont;
        DoubleBuffered = true;

        _tray = new NotifyIcon
        {
            Icon = BuildTrayIcon(),
            Visible = true,
            Text = "IntellectHub",
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

        _flushTimer.Tick += (_, _) => FlushLog();
        _refreshTimer.Tick += (_, _) => RefreshStatus();
        _flushTimer.Start();
        _refreshTimer.Start();

        AppLog.Written += OnLog;
        AppLog.Info("IntellectHub запущен");

        if (_config.AutoStartServer) StartServer();
        else AppLog.Muted("Автозапуск сервера отключён");

        RefreshStatus();
    }

    // ---- layout ------------------------------------------------------------------

    private void BuildLayout()
    {
        Controls.Add(BuildBody());
        Controls.Add(BuildHeader());
    }

    private Control BuildHeader()
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 56, BackColor = Theme.Panel };
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
            Location = new Point(18, 17),
        };

        _status.AutoSize = true;
        _status.Location = new Point(150, 20);
        _status.Font = Theme.UiFont;

        _poolStatus.AutoSize = true;
        _poolStatus.Location = new Point(280, 20);
        _poolStatus.Font = Theme.UiFont;
        _poolStatus.ForeColor = Theme.Muted;

        _toggle.Text = "Старт";
        StyleButton(_toggle, new Size(100, 32));
        _toggle.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _toggle.Location = new Point(header.Width - 118, 12);
        _toggle.Click += (_, _) => ToggleServer();

        header.Controls.AddRange([title, _status, _poolStatus, _toggle]);
        header.Resize += (_, _) => _toggle.Left = header.Width - _toggle.Width - 18;

        return header;
    }

    private Control BuildBody()
    {
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = 1,
            BackColor = Theme.Border,
            FixedPanel = FixedPanel.Panel1,
            // The real minimums are set once the control has a width. Assigning
            // 520 here validates against a not-yet-laid-out size and throws.
            Panel1MinSize = 80,
            Panel2MinSize = 80,
        };

        split.Panel1.Controls.Add(BuildSettingsTabs());
        split.Panel2.Controls.Add(BuildConsole());

        Shown += (_, _) => ApplyInitialSplitter(split);

        return split;
    }

    private void ApplyInitialSplitter(SplitContainer split)
    {
        if (_splitReady) return;

        try
        {
            split.Panel1MinSize = 440;
            split.Panel2MinSize = 520;

            var max = split.Width - split.Panel2MinSize;
            if (max > split.Panel1MinSize)
            {
                split.SplitterDistance = Math.Clamp(460, split.Panel1MinSize, max);
                _splitReady = true;
            }
        }
        catch (InvalidOperationException ex)
        {
            // Too small to satisfy both minimums; the defaults are still usable.
            AppLog.Muted($"разделитель панелей оставлен по умолчанию: {ex.Message}");
        }
    }

    private Control BuildSettingsTabs()
    {
        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ForeColor = Theme.Text,
            Padding = new Point(14, 6),
        };

        var general = new TabPage("Основное") { BackColor = Theme.Panel, Padding = new Padding(14) };
        var accounts = new TabPage("Аккаунты") { BackColor = Theme.Panel, Padding = new Padding(14) };
        var advanced = new TabPage("Продвинутое") { BackColor = Theme.Panel, Padding = new Padding(14) };

        BuildGeneralTab(general);
        BuildAccountsTab(accounts);
        BuildAdvancedTab(advanced);

        tabs.TabPages.AddRange([general, accounts, advanced]);
        return tabs;
    }

    private void BuildGeneralTab(TabPage page)
    {
        var flow = PageFlow(page);

        flow.Controls.Add(SectionLabel("СЕРВЕР"));
        BindText(_host, _config.Host);
        BindText(_port, _config.Port.ToString());
        BindText(_apiKey, _config.ApiKey);
        _apiKey.UseSystemPasswordChar = true;

        flow.Controls.Add(Field("Хост или IP", _host, "host"));
        flow.Controls.Add(Field("Порт", _port, "port"));
        flow.Controls.Add(Field("API-ключ (пусто — без проверки)", _apiKey));

        flow.Controls.Add(SectionLabel("ПОВЕДЕНИЕ"));

        BindCheck(_autoStart, "Запускать сервер при старте", _config.AutoStartServer);
        BindCheck(_minimizeToTray, "Сворачивать в трей при закрытии", _config.MinimizeToTray);
        BindCheck(_startMinimized, "Запускать свёрнутым в трей", _config.StartMinimized);
        foreach (var box in new[] { _autoStart, _minimizeToTray, _startMinimized })
            flow.Controls.Add(box);

        flow.Controls.Add(BuildButtons(save: true, apply: true));
        flow.Controls.Add(BuildProblemLabel());
    }

    private void BuildAccountsTab(TabPage page)
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ColumnCount = 1,
            RowCount = 3,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _accounts.Dock = DockStyle.Fill;
        _accounts.View = View.Details;
        _accounts.FullRowSelect = true;
        _accounts.HideSelection = false;
        _accounts.GridLines = true;
        _accounts.BorderStyle = BorderStyle.FixedSingle;
        _accounts.BackColor = Theme.PanelAlt;
        _accounts.ForeColor = Theme.Text;
        _accounts.Columns.Add("Аккаунт", 130);
        _accounts.Columns.Add("Состояние", 110);
        _accounts.Columns.Add("Ошибок", 60, HorizontalAlignment.Right);
        _accounts.Columns.Add("Последняя активность", 150);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            Margin = new Padding(0, 10, 0, 0),
        };
        buttons.Controls.Add(MakeButton("Войти в DeepSeek", 150, (_, _) => OpenLogin()));
        buttons.Controls.Add(MakeButton("Ключи доступа", 140, (_, _) => OpenKeys()));
        buttons.Controls.Add(MakeButton("Обновить", 110, (_, _) => RefreshAccounts(force: true)));
        buttons.Controls.Add(MakeButton("Снять паузу со всех", 180, (_, _) => ClearCooldowns()));

        layout.Controls.Add(_accounts, 0, 0);
        layout.Controls.Add(buttons, 0, 1);
        layout.Controls.Add(BuildProblemLabel(), 0, 2);

        page.Controls.Add(layout);
        RefreshAccounts();
    }

    private void BuildAdvancedTab(TabPage page)
    {
        var flow = PageFlow(page);

        flow.Controls.Add(SectionLabel("ЗАЩИТА ОТ ТРОТТТЛИНГА"));
        flow.Controls.Add(Hint("DeepSeek банит логин за слишком частые запросы. " +
                               "Задержка выдерживается между отправками."));

        BindText(_minInterval, _config.MinRequestIntervalMs.ToString());
        BindText(_maxConcurrent, _config.MaxConcurrent.ToString());
        BindText(_cooldown, _config.AccountCooldownSeconds.ToString());
        BindText(_lockWait, _config.LockWaitSeconds.ToString());
        BindText(_upstreamTimeout, _config.UpstreamTimeoutSeconds.ToString());
        BindText(_deadline, _config.RequestDeadlineSeconds.ToString());

        flow.Controls.Add(Field("Задержка между запросами, мс (0 = выкл)", _minInterval, "MinRequestIntervalMs"));
        flow.Controls.Add(Field("Максимум одновременных запросов", _maxConcurrent, "MaxConcurrent"));
        flow.Controls.Add(Field("Пауза аккаунта после троттлинга, с", _cooldown, "AccountCooldownSeconds"));
        flow.Controls.Add(Field("Ожидание занятого аккаунта, с", _lockWait, "LockWaitSeconds"));
        flow.Controls.Add(Field("Таймаут одного запроса к DeepSeek, с", _upstreamTimeout, "UpstreamTimeoutSeconds"));
        flow.Controls.Add(Field("Общий дедлайн запроса, с", _deadline, "RequestDeadlineSeconds"));

        flow.Controls.Add(SectionLabel("ПРОМПТ"));
        flow.Controls.Add(Hint("При длинной беседе старая переписка убирается из середины, " +
                               "начало и конец сохраняются. Манифест инструментов не обрезается."));

        BindText(_maxPromptChars, _config.MaxPromptChars.ToString());
        BindText(_maxRetries, _config.MaxRetries.ToString());

        flow.Controls.Add(Field("Лимит промпта, символов", _maxPromptChars, "MaxPromptChars"));
        flow.Controls.Add(Field("Повторов при пустом или длинном ответе", _maxRetries, "MaxRetries"));
    }

    private static FlowLayoutPanel PageFlow(TabPage page)
    {
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = Theme.Panel,
        };
        page.Controls.Add(flow);
        return flow;
    }

    private Control BuildConsole()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Background,
            ColumnCount = 1,
            RowCount = 3,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(BuildLogToolbar(), 0, 0);

        _log.Dock = DockStyle.Fill;
        _log.BackColor = Theme.Background;
        _log.ForeColor = Theme.Text;
        _log.Font = Theme.MonoFont;
        _log.BorderStyle = BorderStyle.None;
        _log.ReadOnly = true;
        _log.WordWrap = false;
        _log.ScrollBars = RichTextBoxScrollBars.Both;
        _log.DetectUrls = false;

        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 6, 14, 6), BackColor = Theme.Background };
        host.Controls.Add(_log);
        layout.Controls.Add(host, 0, 1);

        layout.Controls.Add(BuildLogStatus(), 0, 2);
        return layout;
    }

    private Control BuildLogToolbar()
    {
        var bar = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            Padding = new Padding(10, 6, 10, 6),
            Margin = new Padding(0),
        };

        BindCheck(_fInfo, "инфо", true, compact: true);
        BindCheck(_fOk, "ok", true, compact: true);
        BindCheck(_fWarn, "warn", true, compact: true);
        BindCheck(_fError, "ошибки", true, compact: true);
        BindCheck(_fMuted, "подробно", false, compact: true);

        foreach (var box in new[] { _fInfo, _fOk, _fWarn, _fError, _fMuted })
        {
            box.Margin = new Padding(0, 4, 10, 4);
            box.CheckedChanged += (_, _) => { ReadFilter(); RerenderLog(); };
            bar.Controls.Add(box);
        }

        _search.Width = 170;
        _search.BackColor = Theme.PanelAlt;
        _search.ForeColor = Theme.Text;
        _search.BorderStyle = BorderStyle.FixedSingle;
        _search.Margin = new Padding(6, 3, 8, 3);
        _search.TextChanged += (_, _) => { ReadFilter(); RerenderLog(); };
        bar.Controls.Add(_search);

        bar.Controls.Add(MakeButton("Очистить", 90, (_, _) => ClearLog(), compact: true));
        bar.Controls.Add(MakeButton("Копировать", 100, (_, _) => CopyLog(), compact: true));

        BindCheck(_autoScroll, "прокрутка", _config.AutoScrollLog, compact: true);
        _autoScroll.Margin = new Padding(6, 4, 0, 4);
        bar.Controls.Add(_autoScroll);

        return bar;
    }

    private Control BuildLogStatus()
    {
        var bar = new Panel { Dock = DockStyle.Fill, Height = 26, BackColor = Theme.Panel };
        bar.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawLine(pen, 0, 0, bar.Width, 0);
        };

        _logStats.AutoSize = true;
        _logStats.Location = new Point(14, 5);
        _logStats.ForeColor = Theme.Muted;
        _logStats.Font = Theme.UiFont;
        _logStats.Text = "0 строк";

        bar.Controls.Add(_logStats);
        return bar;
    }

    // ---- small widget helpers -----------------------------------------------------

    private static Label SectionLabel(string text) => new()
    {
        Text = text,
        ForeColor = Theme.Accent,
        Font = Theme.UiBold,
        AutoSize = true,
        Margin = new Padding(0, 14, 0, 6),
    };

    private static Label Hint(string text) => new()
    {
        Text = text,
        ForeColor = Theme.Muted,
        Font = Theme.UiFont,
        AutoSize = false,
        Width = 400,
        Height = 32,
        Margin = new Padding(0, 0, 0, 8),
    };

    private Control Field(string label, TextBox box, string? key = null)
    {
        const int width = 380;

        if (key is not null) _numericFields[key] = box;

        var wrap = new Panel { Width = width, Height = 52, Margin = new Padding(0, 2, 0, 2) };

        var lbl = new Label
        {
            Text = label,
            ForeColor = Theme.Muted,
            AutoSize = false,
            Width = width,
            Height = 22,
            Location = new Point(0, 0),
            TextAlign = ContentAlignment.MiddleLeft,
        };

        box.Location = new Point(0, 22);
        box.Width = width;
        box.BackColor = Theme.PanelAlt;
        box.ForeColor = Theme.Text;
        box.BorderStyle = BorderStyle.FixedSingle;

        wrap.Controls.Add(lbl);
        wrap.Controls.Add(box);
        return wrap;
    }

    private Label BuildProblemLabel()
    {
        _problems.ForeColor = Theme.Red;
        _problems.Font = Theme.UiFont;
        _problems.AutoSize = false;
        _problems.Width = 400;
        _problems.Height = 34;
        _problems.Margin = new Padding(0, 10, 0, 0);
        _problems.Text = "";
        return _problems;
    }

    private FlowLayoutPanel BuildButtons(bool save, bool apply)
    {
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            Margin = new Padding(0, 14, 0, 0),
        };

        if (save) buttons.Controls.Add(MakeButton("Сохранить", 130, (_, _) => SaveSettings()));
        if (apply) buttons.Controls.Add(MakeButton("Сохранить и перезапустить", 220, (_, _) => SaveSettings(restart: true)));
        return buttons;
    }

    private static void StyleButton(Button button, Size size)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = Theme.Border;
        button.BackColor = Theme.PanelAlt;
        button.ForeColor = Theme.Text;
        button.Size = size;
        button.UseVisualStyleBackColor = false;
    }

    private static Button MakeButton(string text, int width, EventHandler onClick, bool compact = false)
    {
        var button = new Button { Text = text };
        StyleButton(button, new Size(width, compact ? 26 : 32));
        button.Margin = new Padding(0, 0, 8, 0);
        button.Click += onClick;
        return button;
    }

    private void BindCheck(CheckBox box, string text, bool value, bool compact = false)
    {
        box.Text = text;
        box.Checked = value;
        box.ForeColor = Theme.Text;
        box.FlatStyle = FlatStyle.Flat;
        box.AutoSize = true;
        box.Margin = new Padding(0, compact ? 4 : 6, 0, compact ? 4 : 6);
    }

    private void BindText(TextBox box, string value)
    {
        box.Text = value;
        box.Anchor = AnchorStyles.Left | AnchorStyles.Right;
    }

    // ---- console rendering --------------------------------------------------------

    private void OnLog(LogLevel level, string message)
    {
        if (IsDisposed || Disposing) return;

        // Producer threads never touch the control: they queue and the UI thread
        // drains on a timer. Appending from a worker re-enters the message loop
        // once per line and interleaves badly with the rest of the window.
        lock (_pending) _pending.Enqueue((level, message));

        if (!_flushTimer.Enabled && !IsDisposed) _flushTimer.Start();
    }

    private void FlushLog()
    {
        List<(LogLevel Level, string Text)> batch;
        lock (_pending)
        {
            if (_pending.Count == 0) return;
            batch = [.. _pending];
            _pending.Clear();
        }

        if (IsDisposed || Disposing || !IsHandleCreated) return;

        var now = DateTime.Now;
        foreach (var (level, text) in batch) _logBuffer.Add(level, now, text);

        AppendNewVisibleLines();
        UpdateLogStats();
    }

    /// <summary>
    /// Appends only what the filter let through since the last render. Rewriting the
    /// whole box on every tick would mean re-laying out thousands of lines several
    /// times a second, which is what made the console crawl.
    /// </summary>
    private void AppendNewVisibleLines()
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;

        var view = _logBuffer.View(_filter);

        // A filter change rewrites the view from scratch; a new line just extends it.
        if (_renderedCount > view.Count)
        {
            _log.Clear();
            _renderedCount = 0;
        }

        for (var i = _renderedCount; i < view.Count; i++) WriteLine(view[i]);

        // Trimming removes lines from the front of the buffer, so the control can
        // hold more entries than the current view. A full rebuild is the only way
        // back in sync, and it happens once per trim rather than per line.
        if (_logBuffer.Count + _renderedCount > view.Count + 64 && _renderedCount > 0)
        {
            _log.Clear();
            _renderedCount = 0;
            for (var i = 0; i < view.Count; i++) WriteLine(view[i]);
        }

        _renderedCount = view.Count;

        if (_config.AutoScrollLog && _autoScroll.Checked) _log.ScrollToCaret();
    }

    /// <summary>Forces the next render to rebuild from the buffer.</summary>
    private void RerenderLog()
    {
        if (_rendering) return;
        _rendering = true;
        try
        {
            _log.Clear();
            _renderedCount = 0;
            AppendNewVisibleLines();
            UpdateLogStats();
        }
        finally
        {
            _rendering = false;
        }
    }

    private void WriteLine(LogEntry entry)
    {
        var target = _log;
        target.SelectionStart = target.TextLength;
        target.SelectionLength = 0;
        target.SelectionColor = Theme.Muted;
        target.AppendText($"{entry.At:HH:mm:ss} ");
        target.SelectionColor = ColorFor(entry.Level);
        target.AppendText($"[{LogBuffer.Prefix(entry.Level)}] {entry.Message}{Environment.NewLine}");
    }

    private void UpdateLogStats()
    {
        var shown = _logBuffer.View(_filter).Count;
        var text = $"{shown} строк";
        if (_logBuffer.Dropped > 0) text += $" · старых убрано {_logBuffer.Dropped}";
        if (shown < _logBuffer.Count) text += $" · скрыто фильтром {_logBuffer.Count - shown}";
        _logStats.Text = text;
    }

    private void ClearLog()
    {
        _logBuffer.Clear();
        _log.Clear();
        _renderedCount = 0;
        UpdateLogStats();
    }

    private void CopyLog()
    {
        try
        {
            Clipboard.SetText(_log.Text);
            AppLog.Ok("лог скопирован в буфер обмена");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"не удалось скопировать лог: {ex.Message}");
        }
    }

    private void ReadFilter()
    {
        _filter.ShowInfo = _fInfo.Checked;
        _filter.ShowOk = _fOk.Checked;
        _filter.ShowWarn = _fWarn.Checked;
        _filter.ShowError = _fError.Checked;
        _filter.ShowMuted = _fMuted.Checked;
        _filter.Search = _search.Text.Trim();
    }

    private static Color ColorFor(LogLevel level) => level switch
    {
        LogLevel.Ok => Theme.Green,
        LogLevel.Warn => Theme.Yellow,
        LogLevel.Error => Theme.Red,
        LogLevel.Muted => Theme.Muted,
        _ => Theme.Text,
    };

    // ---- accounts -----------------------------------------------------------------

    private void RefreshAccounts(bool force = false)
    {
        if (!IsHandleCreated) return;

        try
        {
            var pool = _server.Pool;
            if (force) pool.Reload();

            var accounts = pool.All;
            _accounts.BeginUpdate();
            _accounts.Items.Clear();

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var account in accounts.OrderBy(a => a.Id, StringComparer.Ordinal))
            {
                var item = new ListViewItem(account.Id);
                item.SubItems.Add(DescribeState(account, now));
                item.SubItems.Add(account.TotalFailures.ToString());
                item.SubItems.Add(DescribeLastActivity(account.LastUsedAt, now));
                item.Tag = account;
                _accounts.Items.Add(item);
            }

            _accounts.EndUpdate();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"аккаунты: {ex.Message}");
        }
    }

    private static string DescribeState(Account account, long now)
    {
        if (!account.HasCredentials) return "нет данных";
        if (!account.Enabled) return "выключен";
        if (account.CooldownUntil > now)
        {
            var seconds = Math.Max(1, (account.CooldownUntil - now) / 1000);
            return $"пауза {seconds / 60} мин";
        }

        return "готов";
    }

    private static string DescribeLastActivity(long lastUsedAt, long now)
    {
        if (lastUsedAt == 0) return "ещё не использовался";
        var minutes = (now - lastUsedAt) / 60_000;
        return minutes < 1 ? "только что" : $"{minutes} мин назад";
    }

    private void ClearCooldowns()
    {
        var pool = _server.Pool;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var cleared = 0;

        foreach (var account in pool.All)
        {
            if (account.CooldownUntil <= now) continue;
            account.CooldownUntil = 0;
            account.CooldownReason = null;
            cleared++;
        }

        if (cleared == 0) AppLog.Muted("пауз активных аккаунтов нет");
        else AppLog.Ok($"снята пауза с {cleared} аккаунт(ов)");

        RefreshAccounts(force: true);
    }

    // ---- settings -----------------------------------------------------------------

    private void SaveSettings(bool restart = false)
    {
        ReadConfigFromFields();

        var problems = SettingsValidator.Validate(_config);
        if (problems.Count > 0)
        {
            _invalid.Clear();
            foreach (var problem in problems) _invalid.Add(problem.Field);

            _problems.Text = string.Join(Environment.NewLine, problems.Select(p => "• " + p.Message));
            AppLog.Warn($"настройки не сохранены: {problems[0].Message}");
            HighlightInvalid();
            return;
        }

        _invalid.Clear();
        _problems.Text = "";
        HighlightInvalid();

        _config.Normalize();
        _config.Save();
        AppLog.Ok("настройки сохранены");

        if (restart)
        {
            _server.Stop();
            StartServer();
        }
        else if (_server.IsRunning)
        {
            // Timing knobs are read by the service, which is built once at startup.
            AppLog.Warn("настройки задержек применятся после перезапуска сервера");
        }
    }

    private void HighlightInvalid()
    {
        foreach (var (key, box) in _numericFields)
        {
            var bad = _invalid.Contains(key);
            box.BackColor = bad ? Color.FromArgb(60, 26, 30) : Theme.PanelAlt;
        }
    }

    private void ReadConfigFromFields()
    {
        _config.Host = _host.Text.Trim();
        _config.Port = ReadInt(_port, _config.Port);
        _config.ApiKey = _apiKey.Text;
        _config.AutoStartServer = _autoStart.Checked;
        _config.MinimizeToTray = _minimizeToTray.Checked;
        _config.StartMinimized = _startMinimized.Checked;
        _config.AutoScrollLog = _autoScroll.Checked;

        _config.MinRequestIntervalMs = ReadInt(_minInterval, _config.MinRequestIntervalMs);
        _config.MaxConcurrent = ReadInt(_maxConcurrent, _config.MaxConcurrent);
        _config.AccountCooldownSeconds = ReadInt(_cooldown, _config.AccountCooldownSeconds);
        _config.LockWaitSeconds = ReadInt(_lockWait, _config.LockWaitSeconds);
        _config.UpstreamTimeoutSeconds = ReadInt(_upstreamTimeout, _config.UpstreamTimeoutSeconds);
        _config.RequestDeadlineSeconds = ReadInt(_deadline, _config.RequestDeadlineSeconds);
        _config.MaxPromptChars = ReadInt(_maxPromptChars, _config.MaxPromptChars);
        _config.MaxRetries = ReadInt(_maxRetries, _config.MaxRetries);
    }

    private static int ReadInt(TextBox box, int fallback) =>
        int.TryParse(box.Text.Trim(), out var value) ? value : fallback;

    // ---- server lifecycle ---------------------------------------------------------

    private void ToggleServer()
    {
        if (_server.IsRunning)
        {
            _server.Stop();
            AppLog.Warn("сервер остановлен");
        }
        else
        {
            StartServer();
        }

        RefreshStatus();
    }

    private void StartServer()
    {
        ReadConfigFromFields();
        _config.Normalize();
        WriteConfigToFields();

        try
        {
            _server.Start();
            AppLog.Ok($"сервер слушает http://{_config.Host}:{_config.Port}");
        }
        catch (Exception ex)
        {
            AppLog.Error($"не удалось запустить сервер: {ex.Message}");
        }

        RefreshStatus();
    }

    private void WriteConfigToFields()
    {
        _host.Text = _config.Host;
        _port.Text = _config.Port.ToString();
        _minInterval.Text = _config.MinRequestIntervalMs.ToString();
        _maxConcurrent.Text = _config.MaxConcurrent.ToString();
        _cooldown.Text = _config.AccountCooldownSeconds.ToString();
        _lockWait.Text = _config.LockWaitSeconds.ToString();
        _upstreamTimeout.Text = _config.UpstreamTimeoutSeconds.ToString();
        _deadline.Text = _config.RequestDeadlineSeconds.ToString();
        _maxPromptChars.Text = _config.MaxPromptChars.ToString();
        _maxRetries.Text = _config.MaxRetries.ToString();
    }

    private void RefreshStatus()
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;

        try
        {
            var running = _server.IsRunning;
            _status.Text = running ? "● онлайн" : "● офлайн";
            _status.ForeColor = running ? Theme.Green : Theme.Muted;
            _toggle.Text = running ? "Стоп" : "Старт";

            var (ready, total) = _server.Pool.Snapshot();
            _poolStatus.Text = $"аккаунтов {ready}/{total} готовы";
            _poolStatus.ForeColor = total == 0 ? Theme.Yellow : ready > 0 ? Theme.Muted : Theme.Red;

            RefreshAccounts();
        }
        catch
        {
            // A status refresh must never take the window down.
        }
    }

    private void OpenLogin()
    {
        var target = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "IntellectHub",
            _config.AuthPath);

        using var login = new LoginForm(target);
        if (login.ShowDialog(this) == DialogResult.OK)
        {
            AppLog.Ok($"аккаунт добавлен: {Path.GetFileName(target)}");
            RefreshAccounts(force: true);
        }
    }

    private void OpenKeys()
    {
        using var keys = new KeysForm(_server.ApiKeys);
        keys.ShowDialog(this);
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
            _tray.ShowBalloonTip(1500, "IntellectHub",
                "Свёрнуто в трей. Правый клик по иконке — закрыть.", ToolTipIcon.Info);
            return;
        }

        _server.Stop();
        _flushTimer.Stop();
        _flushTimer.Dispose();
        _refreshTimer.Stop();
        _refreshTimer.Dispose();
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