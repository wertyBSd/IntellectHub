using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace IntellectHub;

internal sealed class LoginForm : Form
{
    private const string StartUrl = "https://chat.deepseek.com";
    private const string DefaultWasm = "https://fe-static.deepseek.com/chat/static/sha3_wasm_bg.7b9ca65ddd.wasm";

    private readonly WebView2 _view = new();
    private readonly Label _status = new();
    private readonly Button _save = new();
    private readonly string _targetFile;

    private string _hifDliq = string.Empty;
    private string _hifLeim = string.Empty;
    private bool _initialized;

    public LoginForm(string targetFile)
    {
        _targetFile = targetFile;

        Text = "Вход в DeepSeek";
        MinimumSize = new Size(720, 560);
        Size = new Size(900, 680);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = Theme.UiFont;

        BuildLayout();
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
            Text = "Вход в DeepSeek",
            Font = Theme.TitleFont,
            ForeColor = Theme.Text,
            AutoSize = true,
            Location = new Point(18, 15)
        };

        _status.AutoSize = true;
        _status.Location = new Point(180, 18);
        _status.ForeColor = Theme.Muted;
        _status.Text = "войдите в аккаунт, затем нажмите «Сохранить»";

        _save.Text = "Сохранить";
        _save.FlatStyle = FlatStyle.Flat;
        _save.FlatAppearance.BorderColor = Theme.Border;
        _save.BackColor = Theme.PanelAlt;
        _save.ForeColor = Theme.Text;
        _save.Size = new Size(120, 30);
        _save.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _save.Location = new Point(header.Width - 138, 11);
        _save.Enabled = false;
        _save.Click += async (_, _) => await CaptureAsync();

        header.Controls.AddRange(new Control[] { title, _status, _save });
        header.Resize += (_, _) => _save.Left = header.Width - _save.Width - 18;

        _view.Dock = DockStyle.Fill;
        _view.DefaultBackgroundColor = Theme.Background;

        var wrap = new Panel { Dock = DockStyle.Fill, Padding = new Padding(1, 0, 1, 1), BackColor = Theme.Border };
        wrap.Controls.Add(_view);

        Controls.Add(wrap);
        Controls.Add(header);

        Load += async (_, _) => await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            var dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "IntellectHub",
                "webview");
            Directory.CreateDirectory(dataDir);

            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: dataDir);
            await _view.EnsureCoreWebView2Async(env);

            var core = _view.CoreWebView2;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += OnWebResourceRequested;
            core.NavigationCompleted += (_, e) =>
            {
                _save.Enabled = e.IsSuccess;
                if (e.IsSuccess)
                    SetStatus("готово — проверьте вход и сохраните", Theme.Green);
                else
                    SetStatus("страница не загрузилась", Theme.Red);
            };

            _initialized = true;
            SetStatus("загрузка chat.deepseek.com…", Theme.Muted);
            core.Navigate(StartUrl);
        }
        catch (Exception ex)
        {
            AppLog.Error($"WebView2: {ex.Message}");
            SetStatus("не удалось запустить браузер", Theme.Red);
            MessageBox.Show(this, ex.Message, "WebView2", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var headers = e.Request.Headers;
        if (headers.Contains("x-hif-dliq"))
        {
            var v = headers.GetHeader("x-hif-dliq");
            if (!string.IsNullOrEmpty(v)) _hifDliq = v;
        }
        if (headers.Contains("x-hif-leim"))
        {
            var v = headers.GetHeader("x-hif-leim");
            if (!string.IsNullOrEmpty(v)) _hifLeim = v;
        }
    }

    private async Task CaptureAsync()
    {
        if (!_initialized) return;

        try
        {
            var pageState = await ReadPageStateAsync();
            var cookie = await ReadCookieAsync();

            var token = ExtractToken(pageState);
            if (string.IsNullOrEmpty(token))
            {
                SetStatus("токен не найден — войдите заново", Theme.Yellow);
                return;
            }

            var auth = new AuthFile
            {
                Token = token,
                HifDliq = _hifDliq,
                HifLeim = _hifLeim,
                Cookie = cookie,
                WasmUrl = pageState?.WasmUrl is { Length: > 0 } w ? w : DefaultWasm
            };

            var dir = Path.GetDirectoryName(_targetFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_targetFile, JsonSerializer.Serialize(auth, AuthFile.Options));

            AppLog.Ok($"Данные входа сохранены: {Path.GetFileName(_targetFile)}");
            SetStatus("сохранено", Theme.Green);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            AppLog.Error($"Сохранить вход: {ex.Message}");
            SetStatus("ошибка сохранения", Theme.Red);
        }
    }

    private async Task<PageState?> ReadPageStateAsync()
    {
        const string script = @"(() => {
          const out = { localStorage:{}, sessionStorage:{}, wasmUrl:'' };
          try {
            for (let i=0;i<localStorage.length;i++){ const k=localStorage.key(i); out.localStorage[k]=localStorage.getItem(k); }
            for (let i=0;i<sessionStorage.length;i++){ const k=sessionStorage.key(i); out.sessionStorage[k]=sessionStorage.getItem(k); }
            const res = performance.getEntriesByType('resource').map(r => r.name);
            out.wasmUrl = res.find(u => /sha3.*\.wasm/.test(u)) || '';
          } catch {}
          return JSON.stringify(out);
        })()";

        var raw = await _view.CoreWebView2.ExecuteScriptAsync(script);
        var json = JsonSerializer.Deserialize<string>(raw);
        if (string.IsNullOrEmpty(json)) return null;
        return JsonSerializer.Deserialize<PageState>(json);
    }

    private async Task<string> ReadCookieAsync()
    {
        var cookies = await _view.CoreWebView2.CookieManager.GetCookiesAsync(StartUrl);
        return string.Join("; ", cookies
            .Where(c => c.Domain.EndsWith("deepseek.com", StringComparison.OrdinalIgnoreCase))
            .Select(c => $"{c.Name}={c.Value}"));
    }

    private static string ExtractToken(PageState? state)
    {
        if (state is null) return string.Empty;

        string[] keys = { "userToken", "token", "auth_token", "access_token", "accessToken" };
        foreach (var store in new[] { state.LocalStorage, state.SessionStorage })
        {
            foreach (var key in keys)
                if (store.TryGetValue(key, out var v) && NormalizeToken(v) is { Length: > 0 } t)
                    return t;
        }
        foreach (var store in new[] { state.LocalStorage, state.SessionStorage })
        {
            foreach (var (k, v) in store)
                if (k.Contains("token", StringComparison.OrdinalIgnoreCase) && NormalizeToken(v) is { Length: > 0 } t)
                    return t;
        }
        return string.Empty;
    }

    private static string NormalizeToken(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var trimmed = raw.Trim();
        if (trimmed.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                foreach (var name in new[] { "value", "token", "access_token", "accessToken" })
                    if (doc.RootElement.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String)
                        return el.GetString()?.Trim() ?? string.Empty;
            }
            catch (JsonException)
            {
            }
        }
        return trimmed;
    }

    private void SetStatus(string text, Color color)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => SetStatus(text, color)); return; }
        _status.Text = text;
        _status.ForeColor = color;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _view.Dispose();
        base.OnFormClosed(e);
    }

    private sealed class PageState
    {
        [JsonPropertyName("localStorage")] public Dictionary<string, string> LocalStorage { get; set; } = new();
        [JsonPropertyName("sessionStorage")] public Dictionary<string, string> SessionStorage { get; set; } = new();
        [JsonPropertyName("wasmUrl")] public string? WasmUrl { get; set; }
    }

    private sealed class AuthFile
    {
        public static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

        [JsonPropertyName("token")] public string Token { get; set; } = string.Empty;
        [JsonPropertyName("hif_dliq")] public string HifDliq { get; set; } = string.Empty;
        [JsonPropertyName("hif_leim")] public string HifLeim { get; set; } = string.Empty;
        [JsonPropertyName("cookie")] public string Cookie { get; set; } = string.Empty;
        [JsonPropertyName("wasmUrl")] public string WasmUrl { get; set; } = string.Empty;
    }
}
