using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace IntellectHub.Client;

/// <summary>
/// Локальный HTTP-сервер для preview статических файлов проекта.
/// Нужен, потому что ES-модули (<c>type="module"</c>) не работают по <c>file://</c>,
/// а браузер блокирует их с CORS-ошибкой.
///
/// Сервер также инжектит в каждый отдаваемый HTML скрипт перехвата JS-ошибок
/// (<c>window.onerror</c> + <c>unhandledrejection</c>), который слабёт собранные
/// ошибки на endpoint <c>/__ih_errors__</c>. Клиент может спросить этот endpoint
/// перед тем, как пометить подзадачу как выполненную.
/// </summary>
public sealed class PreviewServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _root;
    private readonly int _port;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentBag<string> _jsErrors = new();
    private Task? _serverTask;
    private bool _disposed;

    public PreviewServer(string root, int port = 0)
    {
        _root = Path.GetFullPath(root);
        // Если порт 0 — ОС выделит свободный; мы прочитаем его после Start().
        _port = port;
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
    }

    public int Port
    {
        get
        {
            if (_port > 0) return _port;
            // Для динамического порта читаем из первого префикса после старта.
            foreach (var prefix in _listener.Prefixes)
            {
                var m = Regex.Match(prefix, @":(\d+)/");
                if (m.Success) return int.Parse(m.Groups[1].Value);
            }
            return 0;
        }
    }

    public Uri BaseUrl => new($"http://127.0.0.1:{Port}/");

    public IReadOnlyCollection<string> JsErrors => _jsErrors.ToArray();

    public void ClearErrors()
    {
        // ConcurrentBag has no Clear; drain it by reading all.
        while (_jsErrors.TryTake(out _)) { }
    }

    public void Start()
    {
        _listener.Start();
        _serverTask = Task.Run(() => ListenLoop(_cts.Token), _cts.Token);
    }

    private async Task ListenLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = HandleRequestAsync(context);
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                Wire.Write("preview.server.err", ex.ToString());
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        var path = request.Url!.AbsolutePath;

        // Endpoint для чтения собранных JS-ошибок.
        if (path == "/__ih_errors__")
        {
            response.ContentType = "application/json";
            var json = "[" + string.Join(",", _jsErrors.Select(e => "\"" + JsStringEscape(e) + "\"")) + "]";
            var buf = Encoding.UTF8.GetBytes(json);
            response.ContentLength64 = buf.Length;
            await response.OutputStream.WriteAsync(buf, 0, buf.Length);
            response.OutputStream.Close();
            return;
        }

        // Endpoint для очистки ошибок перед новой проверкой.
        if (path == "/__ih_errors_clear__")
        {
            _jsErrors.Clear();
            response.StatusCode = 204;
            response.OutputStream.Close();
            return;
        }

        // Статические файлы из корня проекта.
        var filePath = Path.GetFullPath(Path.Combine(_root, path.TrimStart('/').Replace("/", Path.DirectorySeparatorChar.ToString())));
        if (!filePath.StartsWith(_root + Path.DirectorySeparatorChar) && filePath != _root)
        {
            response.StatusCode = 403;
            response.OutputStream.Close();
            return;
        }

        if (!File.Exists(filePath))
        {
            response.StatusCode = 404;
            response.OutputStream.Close();
            return;
        }

        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        response.ContentType = extension switch
        {
            ".html" => "text/html; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".js" => "application/javascript; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".png" => "image/png",
            ".jpg" => "image/jpeg",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            ".woff2" => "font/woff2",
            ".woff" => "font/woff",
            ".ttf" => "font/ttf",
            _ => "application/octet-stream"
        };

        if (extension == ".html")
        {
            var html = File.ReadAllText(filePath);
            html = InjectErrorCapture(html);
            var buf = Encoding.UTF8.GetBytes(html);
            response.ContentLength64 = buf.Length;
            await response.OutputStream.WriteAsync(buf, 0, buf.Length);
        }
        else
        {
            using var fs = File.OpenRead(filePath);
            response.ContentLength64 = fs.Length;
            await fs.CopyToAsync(response.OutputStream);
        }
        response.OutputStream.Close();
    }

    /// <summary>
    /// Инжектит в HTML скрипт, который перехватывает JS-ошибки и отправляет их
    /// на сервер через fetch (beacon). Работает даже если остальной JS сломан,
    /// так как вставляется в голову документа перед другими скриптами.
    /// </summary>
    private static string InjectErrorCapture(string html)
    {
        var script = @"
<script>(function(){var e=[];window.onerror=function(msg,url,line,col,err){e.push({msg:String(msg),url:String(url||''),line:line,col:col,err:err?err.stack:String(err)});return false;};window.addEventListener('unhandledrejection',function(ev){e.push({msg:'unhandledrejection:'+String(ev.reason),url:location.href,line:0,col:0,err:ev.reason?ev.reason.stack:String(ev.reason)});});setInterval(function(){if(e.length){fetch('/__ih_errors__',{method:'POST',body:JSON.stringify(e),keepalive:true}).then(function(){e.length=0;});}},1500);})();</script>";

        // Вставляем сразу после открывающего <head>, если есть, иначе в начало <body> или всего документа.
        if (html.Contains("<head", StringComparison.OrdinalIgnoreCase))
        {
            return Regex.Replace(html, @"(<head[^>]*>)", "$1" + script, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
        }
        if (html.Contains("<body", StringComparison.OrdinalIgnoreCase))
        {
            return Regex.Replace(html, @"(<body[^>]*>)", "$1" + script, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
        }
        return script + html;
    }

    private static string JsStringEscape(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 32) sb.Append("\\u" + ((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        try { _serverTask?.Wait(2000); } catch { }
        _listener.Stop();
        _listener.Close();
        _cts.Dispose();
    }
}
