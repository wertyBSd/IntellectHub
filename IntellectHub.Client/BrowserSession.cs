using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace IntellectHub.Client;

/// <summary>
/// Single shared WebView2 browser session running on a dedicated STA thread
/// with a hidden WinForms host. Provides navigate, screenshot and click
/// operations for the browser_* client tools.
/// </summary>
internal sealed class BrowserSession : IDisposable
{
    private static readonly Lazy<Task<BrowserSession>> _instance = new(() => CreateAsync());

    public static Task<BrowserSession> Instance => _instance.Value;

    private readonly Thread _staThread;
    private readonly ManualResetEventSlim _initEvent = new();
    private readonly ManualResetEventSlim _disposeEvent = new();
    private Exception? _initError;

    private Form? _form;
    private WebView2? _webView;
    private CoreWebView2? _core;
    private SynchronizationContext? _uiContext;

    private BrowserSession()
    {
        _staThread = new Thread(StaLoop)
        {
            IsBackground = true,
            Name = "BrowserSession-STA",
        };
        _staThread.SetApartmentState(ApartmentState.STA);
        _staThread.Start();
        _initEvent.Wait();
        if (_initError is not null)
            throw new InvalidOperationException("BrowserSession failed to initialize", _initError);
    }

    private void StaLoop()
    {
        try
        {
            _uiContext = new SynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(_uiContext);

            _form = new Form
            {
                ShowInTaskbar = false,
                WindowState = FormWindowState.Minimized,
                Width = 1024,
                Height = 768,
            };

            _webView = new WebView2
            {
                Dock = DockStyle.Fill,
            };
            _form.Controls.Add(_webView);

            // EnsureCoreWebView2Async must be called on the STA thread.
            var ensureTask = _webView.EnsureCoreWebView2Async();
            ensureTask.Wait();

            _core = _webView.CoreWebView2;

            // Set reasonable defaults
            if (_core is not null)
            {
                _core.Settings.AreDefaultScriptDialogsEnabled = false;
                _core.Settings.IsSwipeNavigationEnabled = false;
            }

            _form.Show();
            _initEvent.Set();

            // Pump messages until disposed.
            Application.Run(_form);
        }
        catch (Exception ex)
        {
            _initError = ex;
            _initEvent.Set();
        }
        finally
        {
            _disposeEvent.Set();
        }
    }

    private Task PostAsync(Func<Task> action)
    {
        if (_uiContext is null)
            throw new ObjectDisposedException(nameof(BrowserSession));

        var tcs = new TaskCompletionSource<bool>();
        _uiContext.Post(async _ =>
        {
            try
            {
                await action();
                tcs.SetResult(true);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        }, null);
        return tcs.Task;
    }

    private T Post<T>(Func<T> action)
    {
        if (_uiContext is null)
            throw new ObjectDisposedException(nameof(BrowserSession));

        var tcs = new TaskCompletionSource<T>();
        _uiContext.Post(_ =>
        {
            try
            {
                tcs.SetResult(action());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        }, null);
        return tcs.Task.Result;
    }

    private static async Task<BrowserSession> CreateAsync()
    {
        await Task.Yield(); // ensure the constructor has started the thread
        return new BrowserSession();
    }

    public async Task NavigateAsync(string url)
    {
        await PostAsync(async () =>
        {
            if (_core is null)
                throw new ObjectDisposedException(nameof(BrowserSession));
            _core.Navigate(url);

            // Wait for navigation to complete.
            var completed = new TaskCompletionSource<bool>();
            void Handler(object? s, CoreWebView2NavigationCompletedEventArgs e)
            {
                completed.TrySetResult(e.IsSuccess);
                _core!.NavigationCompleted -= Handler;
            }
            _core.NavigationCompleted += Handler;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            cts.Token.Register(() => completed.TrySetCanceled());
            await completed.Task;
        });
    }

    public async Task ScreenshotAsync(string outputPath)
    {
        await PostAsync(async () =>
        {
            if (_core is null)
                throw new ObjectDisposedException(nameof(BrowserSession));

            // Try CapturePreview first (available on newer WebView2).
            try
            {
                using var ms = new MemoryStream();
                await _core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
                ms.Position = 0;
                using var fs = File.Create(outputPath);
                await ms.CopyToAsync(fs);
                return;
            }
            catch
            {
                // Fall back to rendering via WinForms control.
            }

            // Fallback: render to bitmap via the WinForms control.
            if (_webView is not null && _webView.Width > 0 && _webView.Height > 0)
            {
                using var bmp = new Bitmap(_webView.Width, _webView.Height);
                _webView.DrawToBitmap(bmp, new Rectangle(0, 0, _webView.Width, _webView.Height));
                bmp.Save(outputPath, System.Drawing.Imaging.ImageFormat.Png);
                return;
            }

            throw new InvalidOperationException("Unable to capture browser screenshot.");
        });
    }

    public async Task ClickAsync(string cssSelector)
    {
        await PostAsync(async () =>
        {
            if (_core is null)
                throw new ObjectDisposedException(nameof(BrowserSession));

            // Escape single quotes in the selector.
            var escaped = cssSelector.Replace("'", "\\'");
            var script = $"document.querySelector('{escaped}')?.click();";
            await _core.ExecuteScriptAsync(script);
        });
    }

    public void Dispose()
    {
        if (_uiContext is not null)
        {
            try
            {
                _uiContext.Post(_ =>
                {
                    try { _form?.Close(); } catch { }
                    try { Application.Exit(); } catch { }
                }, null);
            }
            catch { }
        }

        _staThread.Join(TimeSpan.FromSeconds(5));
        _webView?.Dispose();
        _form?.Dispose();
        _initEvent.Dispose();
        _disposeEvent.Dispose();
    }
}
