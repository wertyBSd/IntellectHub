using System.Diagnostics;

namespace IntellectHub;

internal sealed class KeysForm : Form
{
    private readonly ApiKeyStore _store;
    private readonly ListView _list = new();
    private readonly TextBox _name = new();
    private readonly TextBox _daily = new();
    private readonly TextBox _monthly = new();
    private readonly ComboBox _expiry = new();
    private readonly Label _status = new();
    private readonly TextBox _reveal = new();

    public KeysForm(ApiKeyStore store)
    {
        _store = store;

        Text = "API-ключи";
        MinimumSize = new Size(720, 520);
        Size = new Size(880, 600);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = Theme.UiFont;

        BuildLayout();
        RefreshList();
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
            Text = "API-ключи",
            Font = Theme.TitleFont,
            ForeColor = Theme.Text,
            AutoSize = true,
            Location = new Point(18, 15)
        };

        _status.AutoSize = true;
        _status.Location = new Point(160, 18);
        _status.ForeColor = Theme.Muted;
        _status.Text = "лимиты проверяются при каждом запросе";

        header.Controls.AddRange(new Control[] { title, _status });

        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.GridLines = false;
        _list.BackColor = Theme.Background;
        _list.ForeColor = Theme.Text;
        _list.Font = Theme.MonoFont;
        _list.BorderStyle = BorderStyle.None;
        _list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        _list.Columns.Add("Ключ", 110);
        _list.Columns.Add("Имя", 150);
        _list.Columns.Add("Лимит/день", 90, HorizontalAlignment.Right);
        _list.Columns.Add("Лимит/мес", 90, HorizontalAlignment.Right);
        _list.Columns.Add("Исп.", 80, HorizontalAlignment.Right);
        _list.Columns.Add("Истекает", 150);
        _list.Columns.Add("Статус", 110);

        var listWrap = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 12, 14, 8), BackColor = Theme.Background };
        listWrap.Controls.Add(_list);

        var form = new Panel { Dock = DockStyle.Bottom, Height = 150, BackColor = Theme.Panel, Padding = new Padding(14, 10, 14, 10) };

        _name.Width = 180;
        _daily.Width = 90;
        _monthly.Width = 90;

        _expiry.DropDownStyle = ComboBoxStyle.DropDownList;
        _expiry.FlatStyle = FlatStyle.Flat;
        _expiry.BackColor = Theme.PanelAlt;
        _expiry.ForeColor = Theme.Text;
        _expiry.Width = 150;
        _expiry.Items.AddRange(new object[] { "Бессрочный", "1 день", "7 дней", "30 дней", "90 дней", "365 дней" });
        _expiry.SelectedIndex = 0;

        var nameWrap = Field("Имя", _name, 190);
        var dailyWrap = Field("Запросов в день", _daily, 100);
        var monthlyWrap = Field("Запросов в месяц", _monthly, 100);
        var expiryWrap = Field("Срок действия", _expiry, 160);

        var generate = new Button
        {
            Text = "Сгенерировать",
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.PanelAlt,
            ForeColor = Theme.Text,
            Size = new Size(140, 30)
        };
        generate.FlatAppearance.BorderColor = Theme.Border;
        generate.Click += (_, _) => Generate();

        var revoke = new Button
        {
            Text = "Удалить",
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.PanelAlt,
            ForeColor = Theme.Text,
            Size = new Size(110, 30)
        };
        revoke.FlatAppearance.BorderColor = Theme.Border;
        revoke.Click += (_, _) => RevokeSelected();

        var toggle = new Button
        {
            Text = "Вкл/выкл",
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.PanelAlt,
            ForeColor = Theme.Text,
            Size = new Size(110, 30)
        };
        toggle.FlatAppearance.BorderColor = Theme.Border;
        toggle.Click += (_, _) => ToggleSelected();

        var copy = new Button
        {
            Text = "Копировать",
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.PanelAlt,
            ForeColor = Theme.Text,
            Size = new Size(110, 30)
        };
        copy.FlatAppearance.BorderColor = Theme.Border;
        copy.Click += (_, _) => CopyReveal();

        var fields = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 58,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Theme.Panel
        };
        fields.Controls.AddRange(new Control[] { nameWrap, dailyWrap, monthlyWrap, expiryWrap });

        _reveal.Dock = DockStyle.Bottom;
        _reveal.ReadOnly = true;
        _reveal.BackColor = Theme.PanelAlt;
        _reveal.ForeColor = Theme.Green;
        _reveal.Font = Theme.MonoFont;
        _reveal.BorderStyle = BorderStyle.FixedSingle;
        _reveal.Height = 26;
        _reveal.Text = "сгенерированный ключ появится здесь";

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 40,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Theme.Panel
        };
        actions.Controls.AddRange(new Control[] { generate, revoke, toggle, copy });

        form.Controls.Add(actions);
        form.Controls.Add(_reveal);
        form.Controls.Add(fields);

        Controls.Add(listWrap);
        Controls.Add(form);
        Controls.Add(header);
    }

    private static Control Field(string label, Control input, int width)
    {
        var wrap = new Panel { Width = width, Height = 52, Margin = new Padding(0, 0, 10, 0) };
        var lbl = new Label { Text = label, ForeColor = Theme.Muted, AutoSize = true, Location = new Point(0, 0) };
        input.Location = new Point(0, 20);
        input.Width = width - 10;
        wrap.Controls.Add(lbl);
        wrap.Controls.Add(input);
        return wrap;
    }

    private void RefreshList()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _list.BeginUpdate();
        _list.Items.Clear();

        foreach (var key in _store.All)
        {
            var item = new ListViewItem(key.Prefix + "…");
            item.SubItems.Add(key.Name ?? "—");
            item.SubItems.Add(key.DailyLimit?.ToString() ?? "∞");
            item.SubItems.Add(key.MonthlyLimit?.ToString() ?? "∞");
            item.SubItems.Add(key.UsedTotal.ToString());
            item.SubItems.Add(key.ExpiresAt is { } at
                ? DateTimeOffset.FromUnixTimeMilliseconds(at).ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                : "бессрочный");
            item.SubItems.Add(StatusOf(key, now));
            item.Tag = key.Id;
            item.ForeColor = key.Enabled && !key.IsExpired(now) ? Theme.Text : Theme.Muted;
            _list.Items.Add(item);
        }

        _list.EndUpdate();
    }

    private static string StatusOf(ApiKeyRecord key, long now)
    {
        if (!key.Enabled) return "выключен";
        if (key.IsExpired(now)) return "истёк";
        if (key.DailyLimit is { } dl && key.UsedToday >= dl) return "дневной лимит";
        if (key.MonthlyLimit is { } ml && key.UsedMonth >= ml) return "месячный лимит";
        return "активен";
    }

    private void Generate()
    {
        long? daily = ParseLimit(_daily.Text);
        long? monthly = ParseLimit(_monthly.Text);
        long? expires = ExpiryToUnix(_expiry.SelectedIndex);

        var (record, raw) = _store.Create(_name.Text, daily, monthly, expires);
        _reveal.Text = raw;
        SetStatus("ключ создан — скопируйте его сейчас", Theme.Green);
        AppLog.Ok($"API-ключ создан: {record.Prefix}… ({record.Name ?? "без имени"})");
        RefreshList();
    }

    private static long? ParseLimit(string text) =>
        long.TryParse(text.Trim(), out var value) && value > 0 ? value : null;

    private static long? ExpiryToUnix(int index)
    {
        var days = index switch { 1 => 1, 2 => 7, 3 => 30, 4 => 90, 5 => 365, _ => 0 };
        return days == 0 ? null : DateTimeOffset.UtcNow.AddDays(days).ToUnixTimeMilliseconds();
    }

    private void RevokeSelected()
    {
        if (_list.SelectedItems.Count == 0) return;
        var id = (string?)_list.SelectedItems[0].Tag;
        if (id is null) return;

        if (_store.Revoke(id))
        {
            SetStatus("ключ удалён", Theme.Yellow);
            AppLog.Warn($"API-ключ удалён: {id}");
            RefreshList();
        }
    }

    private void ToggleSelected()
    {
        if (_list.SelectedItems.Count == 0) return;
        var id = (string?)_list.SelectedItems[0].Tag;
        if (id is null) return;

        var record = _store.All.FirstOrDefault(k => k.Id == id);
        if (record is null) return;

        _store.SetEnabled(id, !record.Enabled);
        SetStatus(record.Enabled ? "ключ выключен" : "ключ включён", Theme.Muted);
        RefreshList();
    }

    private void CopyReveal()
    {
        if (string.IsNullOrEmpty(_reveal.Text) || !_reveal.Text.StartsWith("ih_", StringComparison.Ordinal)) return;
        try
        {
            Clipboard.SetText(_reveal.Text);
            SetStatus("ключ скопирован в буфер", Theme.Green);
        }
        catch (Exception ex)
        {
            SetStatus($"не удалось скопировать: {ex.Message}", Theme.Red);
        }
    }

    private void SetStatus(string text, Color color)
    {
        if (IsDisposed) return;
        _status.Text = text;
        _status.ForeColor = color;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _store.Flush();
        base.OnFormClosed(e);
    }
}
