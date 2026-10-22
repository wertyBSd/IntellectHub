using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace IntellectHub.Client;

public sealed class ToolSpec
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("input_schema")] public JsonNode InputSchema { get; set; } = new JsonObject();
}

public sealed class ToolCall
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Arguments { get; init; } = "{}";
}

public sealed class AssistantReply
{
    public string Text { get; init; } = "";
    public IReadOnlyList<ToolCall> ToolCalls { get; init; } = [];
    public string StopReason { get; init; } = "end_turn";

    public bool HasToolCalls => ToolCalls.Count > 0;
}

/// <summary>
/// The provider answered that the requested model does not exist or has been
/// retired. Carries the replacement the provider itself named in the error, if
/// it did, so the client can offer it in the model picker right away.
/// </summary>
public sealed class ModelUnavailableException : Exception
{
    public ModelUnavailableException(string model, string? suggestion, Exception inner)
        : base(inner.Message, inner)
    {
        Model = model;
        Suggestion = suggestion;
    }

    /// <summary>The model the request named, shortened the way the picker shows it.</summary>
    public string Model { get; }

    /// <summary>The replacement named in the provider's error, or null if it gave none.</summary>
    public string? Suggestion { get; }
}

/// <summary>
/// Роль модели в мультимодельном пайплайне.
/// </summary>
public enum ModelRole
{
    /// <summary>Основная модель (по умолчанию).</summary>
    Default,
    /// <summary>Планирование и декомпозиция задач.</summary>
    Planning,
    /// <summary>Построение Repo Map и анализ кодовой базы.</summary>
    RepoMap,
    /// <summary>Генерация и редактирование кода.</summary>
    Coding,
    /// <summary>Отладка и исправление ошибок.</summary>
    Debug,
    /// <summary>Ответы пользователю в чате.</summary>
    Chat,
}

public sealed class ApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly ClientConfig _config;
    private readonly ConnectionEntry? _connection;
    private readonly ProviderKind _provider;
    private readonly string _model;
    private readonly ModelRoleConfig _modelRoles;

    public ApiClient(ClientConfig config, ConnectionEntry? connection = null)
    {
        _config = config;
        _connection = connection;
        _provider = connection?.Provider ?? ProviderKind.Anthropic;
        _modelRoles = config.ModelRoles ?? new ModelRoleConfig();

        // A named connection carries its own address; otherwise the address comes
        // from the configuration, which is what a hand-typed gateway looks like.
        var baseUrl = string.IsNullOrWhiteSpace(connection?.Url) ? config.BaseUrl : connection.Url;

        // The model follows the connection when it names one, so switching provider
        // does not leave the previous provider's model behind.
        _model = !string.IsNullOrWhiteSpace(connection?.Model) ? connection.Model : config.Model;

        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromMinutes(5),
        };

        // A named connection keeps its credential in secrets.json under its own
        // key, so two providers can be signed in at the same time. The gateway
        // key in client.json stays the fallback for hand-typed addresses.
        // Environment variable IH_API_KEY has highest priority — set it once and
        // forget about secrets.json.
        EffectiveKey = Environment.GetEnvironmentVariable("IH_API_KEY")
            ?? (connection is null ? config.ApiKey : ClientConfig.GetSecret(connection.CredentialKey) ?? config.ApiKey);

        // Диагностика: пишем, откуда берется ключ (маскируем значение)
        var keySource = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("IH_API_KEY"))
            ? (connection is null ? "client.json (apiKey)" : $"secrets.json ({connection.CredentialKey})")
            : "env var IH_API_KEY";
        Wire.Write("auth.debug", "key_source={0} · credential_key={1} · effective_key={2}",
            keySource, connection?.CredentialKey ?? "(none)", Wire.Mask(EffectiveKey));

        // Если ключа нет нигде — не шлём запрос к провайдеру, а возвращаем понятную ошибку.
        if (string.IsNullOrWhiteSpace(EffectiveKey))
        {
            var keyHint = connection?.Name ?? "your provider";
            Wire.Write("auth.err", "no API key for {0}. Set it with: /connect key {1} <your-api-key>", keyHint, keyHint);
            return;
        }

        ApplyAuth(baseUrl);

        Wire.TraceId = null;
        Wire.Write("client.init", "base_url={0} · model={1} · max_tokens={2} · temperature={3} · provider={4} · api_key={5} · timeout=300s",
            baseUrl.TrimEnd('/') + "/",
            _model,
            config.MaxTokens,
            config.Temperature,
            _provider,
            string.IsNullOrWhiteSpace(EffectiveKey) ? "(none)" : Wire.Mask(EffectiveKey));
        Wire.Write("client.init", "trace file → {0}", Wire.LogDirectory);
    }

    /// <summary>The credential in use, from secrets.json or the configuration.</summary>
    private string? EffectiveKey { get; set; }

    /// <summary>Returns the effective API key, or null if none is configured.</summary>
    public string? GetEffectiveKey() => EffectiveKey;

    /// <summary>
    /// Gets the model name for a specific role, falling back to the default model.
    /// </summary>
    public string GetModelForRole(ModelRole role)
    {
        var roleModel = role switch
        {
            ModelRole.Planning => _modelRoles.PlanningModel,
            ModelRole.RepoMap => _modelRoles.RepoMapModel,
            ModelRole.Coding => _modelRoles.CodingModel,
            ModelRole.Debug => _modelRoles.DebugModel,
            ModelRole.Chat => _modelRoles.ChatModel,
            _ => null
        };

        return string.IsNullOrWhiteSpace(roleModel) ? _model : roleModel;
    }

    /// <summary>
    /// Sends the credential the way the provider expects it. Each provider family
    /// reads the key from a different header, and a key sent in the wrong one is
    /// either ignored or rejected as a malformed request.
    /// </summary>
    private void ApplyAuth(string baseUrl)
    {
        var key = EffectiveKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            Wire.Write("auth.warn", "no API key configured for provider {0}", _provider);
            return;
        }

        switch (_connection?.Auth ?? AuthStyle.Bearer)
        {
            case AuthStyle.AnthropicKey:
                _http.DefaultRequestHeaders.TryAddWithoutValidation("x-api-key", key);
                _http.DefaultRequestHeaders.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                break;

            case AuthStyle.GoogleApiKey:
                // Gemini authenticates its two surfaces differently: the native REST
                // API reads the key from x-goog-api-key, while the OpenAI
                // compatibility layer (/v1beta/openai) ignores that header entirely
                // and only accepts Authorization: Bearer. Sending one credential in
                // both headers satisfies whichever surface the request goes to;
                // the native models listing takes the Bearer header back off again.
                _http.DefaultRequestHeaders.TryAddWithoutValidation("x-goog-api-key", key);
                _http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + key);
                break;

            case AuthStyle.Bearer:
                _http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + key);
                break;

            case AuthStyle.None:
                break;
        }

        // Provider-specific headers from the preset (OpenRouter's referrer, etc.).
        if (_connection is not null)
        {
            foreach (var (name, value) in _connection.Headers)
                _http.DefaultRequestHeaders.TryAddWithoutValidation(name, value);
        }

        Wire.Write("auth.applied", "provider={0} · header={1} · key={2}",
            _provider, _connection?.Auth ?? AuthStyle.Bearer, Wire.Mask(key));
    }

    /// <summary>
    /// A GET against the native Gemini API without the Authorization header:
    /// the native endpoint validates that header as an OAuth 2 token, so an API
    /// key sent as Bearer fails the request before the x-goog-api-key credential
    /// is ever read. This uses HttpRequestMessage to avoid mutating
    /// DefaultRequestHeaders (which is not thread-safe for concurrent mutation).
    /// </summary>
    private async Task<HttpResponseMessage> GetWithoutBearerAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        // Copy all default headers except Authorization
        foreach (var header in _http.DefaultRequestHeaders)
        {
            if (!string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }
        // x-goog-api-key is already in default headers from ApplyAuth
        return await _http.SendAsync(request, ct);
    }

    public async Task<string> HealthAsync(CancellationToken ct = default)
    {
        var id = Wire.NewTraceId();
        Wire.TraceId = id;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Wire.Write("http.req", "GET health → {0}", _http.BaseAddress);

        using var response = await _http.GetAsync("health", ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        Wire.Timing("http.resp", clock, "{0} · {1} bytes", (int)response.StatusCode, body.Length);
        Wire.Json("http.body", body);

        response.EnsureSuccessStatusCode();
        return body;
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        var id = Wire.NewTraceId();
        Wire.TraceId = id;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        // Google AI Studio API использует другой эндпоинт для списка моделей
        // (не OpenAI-совместимый /v1/openai/models, а /v1beta/models).
        string modelsEndpoint;
        var baseAddress = _http.BaseAddress.ToString();
        bool isOllama = baseAddress.Contains("localhost:11434", StringComparison.OrdinalIgnoreCase)
                     || baseAddress.Contains("127.0.0.1:11434", StringComparison.OrdinalIgnoreCase)
                     || baseAddress.Contains(":11434", StringComparison.OrdinalIgnoreCase);

        if (baseAddress.Contains("googleapis", StringComparison.OrdinalIgnoreCase))
        {
            // Для Google: base = https://generativelanguage.googleapis.com/v1beta/openai
            // Нужно подняться на уровень выше: https://generativelanguage.googleapis.com/v1beta/models
            var baseWithoutSuffix = baseAddress.Replace("/v1beta/openai", "", StringComparison.OrdinalIgnoreCase);
            modelsEndpoint = baseWithoutSuffix.TrimEnd('/') + "/v1beta/models";
            Wire.Write("http.req", "GET v1beta/models (Google) → {0}", modelsEndpoint);
        }
        else if (isOllama)
        {
            // Ollama: native API uses /api/tags, OpenAI-compatible uses /v1/models
            if (baseAddress.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            {
                modelsEndpoint = "v1/models";
            }
            else
            {
                modelsEndpoint = "api/tags";
            }
            Wire.Write("http.req", "GET {0} (Ollama) → {1}", modelsEndpoint, _http.BaseAddress);
        }
        else
        {
            modelsEndpoint = "v1/models";
            Wire.Write("http.req", "GET v1/models → {0}", _http.BaseAddress);
        }

        using var response = baseAddress.Contains("googleapis", StringComparison.OrdinalIgnoreCase)
            ? await GetWithoutBearerAsync(modelsEndpoint, ct)
            : await _http.GetAsync(modelsEndpoint, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        Wire.Timing("http.resp", clock, "{0} · {1} bytes", (int)response.StatusCode, json.Length);

        response.EnsureSuccessStatusCode();
        var list = new List<string>();
        using var doc = JsonDocument.Parse(json);

        if (baseAddress.Contains("googleapis", StringComparison.OrdinalIgnoreCase))
        {
            // Google: {"models":[{"name":"models/gemini-2.0-flash",...}]}
            if (doc.RootElement.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in models.EnumerateArray())
                    if (item.TryGetProperty("name", out var name) && name.GetString() is { } s)
                        list.Add(ModelName.Short(s));
            }
        }
        else if (isOllama)
        {
            // Ollama: {"models":[{"name":"llama3.2:latest",...}]} (native /api/tags)
            // or {"data":[{"id":"llama3.2:latest",...}]} (OpenAI-compatible /v1/models)
            if (doc.RootElement.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in models.EnumerateArray())
                    if (item.TryGetProperty("name", out var name) && name.GetString() is { } s)
                        list.Add(ModelName.Short(s));
            }
            else if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                    if (item.TryGetProperty("id", out var id2) && id2.GetString() is { } s)
                        list.Add(s);
            }
        }
        else
        {
            // OpenAI: {"data":[{"id":"gpt-4",...}]}
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                    if (item.TryGetProperty("id", out var id2) && id2.GetString() is { } s)
                        list.Add(s);
            }
        }
        Wire.Write("http.body", "models=[{0}]", string.Join(", ", list));
        return list;
    }

    /// <summary>
    /// One agent turn, client to gateway and back. <paramref name="traceId"/> is
    /// minted by the caller so the CLI's tool execution can be labelled with the
    /// same id the gateway and DeepSeek lines carry. <paramref name="sessionKey"/>
    /// is the conversation's identity: the gateway uses it to keep one remote
    /// DeepSeek chat open across the turns instead of creating one per request.
    /// </summary>
    public async Task<AssistantReply> SendAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolSpec>? tools,
        string traceId,
        string sessionKey,
        ModelRole role = ModelRole.Default,
        CancellationToken ct = default)
    {
        Wire.TraceId = traceId;

        if (string.IsNullOrWhiteSpace(EffectiveKey))
        {
            var keyHint = _connection?.Name ?? "your provider";
            Wire.Write("auth.err", "no API key for {0}. Set it with: /connect key {1} <your-api-key>",
                keyHint, keyHint);
            return new AssistantReply
            {
                Text = $"❌ Нет API ключа для {keyHint}. Установите через: /connect key {keyHint} <ваш-ключ>",
                ToolCalls = [],
                StopReason = "end_turn"
            };
        }

        var model = GetModelForRole(role);
        var payload = new JsonObject
        {
            ["model"] = ModelName.Short(model),
            ["max_tokens"] = _config.MaxTokens,
            ["temperature"] = _config.Temperature,
        };

        var messageArray = new JsonArray();
        if (_provider == ProviderKind.Anthropic)
        {
            payload["system"] = _config.System;
        }
        else
        {
            // У OpenAI-совместимого провода нет верхнеуровневого "system": инструкции
            // едут первым сообщением с ролью system. Без него модель не знает
            // протокола клиента (инструменты, JSON-план подзадач) и отвечает
            // простым текстом, выводя содержимое файлов вместо записи в файл.
            messageArray.Add(new JsonObject
            {
                ["role"] = "system",
                ["content"] = _config.System,
            });
        }

        // Нормализация контента для Gemini: Gemini OpenAI-совместимый эндпоинт
        // ожидает, что content будет plain-строкой, а не массивом блоков (Anthropic format).
        // anthropic-формат content=[{type: "text"}, {type: "tool_use", ...}] приведет к 400 ошибке.
        string NormalizeContentForGemini(object content)
        {
            if (content is string s) return s;

            // Если пришел JsonValue — извлекаем строку
            if (content is JsonValue jsonValue)
                return jsonValue.GetValue<string>() ?? jsonValue.ToString();

            // Если пришел JsonArray — обрабатываем как Anthropic-массив блоков
            if (content is JsonArray jsonArray)
            {
                var parts = new List<string>();
                foreach (var block in jsonArray)
                {
                    // block может быть JsonObject или иным типом
                    if (block is not JsonObject blockObj) continue;
                    var blockType = blockObj["type"]?.GetValue<string>();
                    if (blockType == "text")
                    {
                        var textVal = blockObj["text"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(textVal)) parts.Add(textVal);
                    }
                    else if (blockType == "tool_use")
                    {
                        var nameVal = blockObj["name"]?.GetValue<string>();
                        parts.Add($"[Инструмент: {nameVal ?? "неизвестно"}]");
                    }
                    else
                    {
                        // Для неизвестных блоков добавляем сырый JSON фрагмент
                        parts.Add(blockObj.ToJsonString());
                    }
                }
                return string.Join("\n\n", parts);
            }

            // Если пришел JsonObject — пробуем извлечь текст или свойства
            if (content is JsonObject jsonObject)
            {
                // Пытаемся найти свойство "text" или просто преобразуем в строку
                var textVal = jsonObject["text"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(textVal)) return textVal;
                return jsonObject.ToJsonString();
            }

            // fallback: превращаем в JSON строку
            return content?.ToString() ?? "";
        }

        foreach (var message in messages)
        {
            // Приводим контент к строке для Gemini — если пришел массив блоков (Anthropic),
            // собираем текстовые части в одну строку
            string contentStr = NormalizeContentForGemini(message.Content);

            messageArray.Add(new JsonObject
            {
                ["role"] = message.Role,
                ["content"] = contentStr,
            });
        }
        payload["messages"] = messageArray;

        // Формат объявления инструментов обязан совпадать с проводом: Anthropic
        // читает name/input_schema, OpenAI-совместимые провайдеры — включая Gemini
        // OpenAI-совместимые провайдеры (включая Gemini через compatibility-слой)
        // — всегда {"type":"function","function":{...}}.
        // Чужой формат провайдер молча игнорирует, и модель остаётся без рук.
        if (tools is { Count: > 0 })
        {
            var toolArray = new JsonArray();
            foreach (var tool in tools)
            {
                if (_provider == ProviderKind.Anthropic)
                {
                    toolArray.Add(new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["input_schema"] = tool.InputSchema.DeepClone(),
                    });
                }
                else
                {
                    toolArray.Add(new JsonObject
                    {
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = tool.Name,
                            ["description"] = tool.Description,
                            ["parameters"] = tool.InputSchema.DeepClone(),
                        },
                    });
                }
            }
            payload["tools"] = toolArray;
        }

        var json = payload.ToJsonString();
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var endpoint = _provider == ProviderKind.Anthropic ? "v1/messages" : "v1/chat/completions";
        Wire.Write("http.req", "POST {0} → {1} · base={2} · history={3} msgs · tools={4} · {5} bytes",
            endpoint, _http.BaseAddress, _http.BaseAddress, messages.Count, tools?.Count ?? 0, json.Length);
        Wire.Json("http.req.body", json);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("x-trace-id", traceId);

        // Lets the gateway continue the DeepSeek chat this conversation already
        // has open instead of creating a new session for every request.
        if (!string.IsNullOrWhiteSpace(sessionKey))
            request.Headers.TryAddWithoutValidation("x-agent-session", sessionKey);

        using var response = await _http.SendAsync(request, ct);
        Wire.Write("http.resp.hdr", "HTTP {0} · server_trace={1}", (int)response.StatusCode,
            response.Headers.TryGetValues("x-trace-id", out var echoed) ? echoed.First() : "(not echoed)");

        var body = await response.Content.ReadAsStringAsync(ct);
        Wire.Timing("http.resp", clock, "{0} · {1} bytes", (int)response.StatusCode, body.Length);
        Wire.Json("http.resp.body", body);

        if (!response.IsSuccessStatusCode)
        {
            var unavailable = ParseModelUnavailable((int)response.StatusCode, body, _model);
            if (unavailable is not null)
                throw unavailable;

            throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {Truncate(body, 500)}");
        }

        var reply = ParseReply(body);
        Wire.Write("http.parsed", "stop_reason={0} · text={1} chars · tool_calls=[{2}]",
            reply.StopReason, reply.Text.Length,
            string.Join(", ", reply.ToolCalls.Select(c => $"{c.Name} args={c.Arguments.Length} chars")));
        Wire.Block("http.parsed.text", reply.Text);
        foreach (var call in reply.ToolCalls)
            Wire.Json("http.parsed.tool", call.Arguments);

        return reply;
    }

    internal static AssistantReply ParseReply(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // Stop reason: OpenAI / Google compat use "finish_reason", Anthropic
        // gateway uses "stop_reason". Google native uses "finishReason".
        var stopReason = "end_turn";
        if (root.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
            stopReason = fr.GetString() ?? "end_turn";
        else if (root.TryGetProperty("stop_reason", out var sr) && sr.ValueKind == JsonValueKind.String)
            stopReason = sr.GetString() ?? "end_turn";
        else if (root.TryGetProperty("finishReason", out var fn) && fn.ValueKind == JsonValueKind.String)
            stopReason = fn.GetString() ?? "end_turn";

        var text = new StringBuilder();
        var calls = new List<ToolCall>();

        // Google native: candidates[].content.parts[].text + finishReason
        if (root.TryGetProperty("candidates", out var candidates) && candidates.ValueKind == JsonValueKind.Array)
        {
            foreach (var cand in candidates.EnumerateArray())
            {
                if (!cand.TryGetProperty("content", out var candContent)) continue;
                if (!candContent.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array) continue;
                foreach (var part in parts.EnumerateArray())
                {
                    if (part.TryGetProperty("text", out var pt) && pt.ValueKind == JsonValueKind.String)
                        text.Append(pt.GetString());
                }
            }
        }
        // Anthropic: content array with type text/tool_use
        else if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                var type = block.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type == "text" && block.TryGetProperty("text", out var txt))
                {
                    text.Append(txt.GetString());
                }
                else if (type == "tool_use")
                {
                    var id = block.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
                    var name = block.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var input = block.TryGetProperty("input", out var inp) ? inp.GetRawText() : "{}";
                    calls.Add(new ToolCall { Id = id, Name = name, Arguments = input });
                }
            }
        }
        // OpenAI: choices[0].message.content + tool_calls (включая Google через совместимость)
        else if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
        {
            foreach (var choice in choices.EnumerateArray())
            {
                if (choice.TryGetProperty("message", out var message))
                {
                    if (message.TryGetProperty("content", out var msgContent) && msgContent.ValueKind == JsonValueKind.String)
                        text.Append(msgContent.GetString());
                    // content: null или отсутствует — возможно, безопасная фильтрация Google.
                    // Текст остаётся пустым, клиент повторит запрос (см. Program.cs), а в лог
                    // запишем признак безопасности, если finish_reason об этом говорит.

                    if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var tc in toolCalls.EnumerateArray())
                        {
                            var id = tc.TryGetProperty("id", out var tci) ? tci.GetString() ?? "" : "";
                            // JsonElement is a struct, so the absence of the property has
                            // to be spelled as a nullable or the two arms of the
                            // conditional disagree on their type.
                            JsonElement? func = tc.TryGetProperty("function", out var f) ? f : null;
                            var name = func is not null && func.Value.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                            // OpenAI-совместимые провайдеры (в том числе Gemini) кладут arguments
                            // строкой с JSON внутри, а не объектом. Без раскрытия строки клиент
                            // получал бы не объект, а строку, и каждый вызов падал бы с
                            // "Missing required arguments" — модель исправно передавала path/content.
                            var args = func is not null && func.Value.TryGetProperty("arguments", out var a)
                                ? (a.ValueKind == JsonValueKind.String ? a.GetString() ?? "" : a.GetRawText())
                                : "{}";
                            calls.Add(new ToolCall { Id = id, Name = name, Arguments = args });
                        }
                    }
                }
            }
        }

        // Если модель вернула пустой текст и finish_reason говорит о безопасности —
        // это не "модель не прислала содержимое", а отказ провайдера. Пометим это
        // явно, чтобы клиент не пытался бесконечно повторять тот же запрос.
        var replyText = text.ToString();
        if (string.IsNullOrWhiteSpace(replyText) && stopReason == "safety")
        {
            // Google заблокировал ответ фильтром безопасности. Текст пустой — клиент
            // повторит запрос (до 3 попыток, см. Program.cs), но в логе будет видно
            // настоящая причина, а не "модель не прислала содержимое".
            Wire.Write("google.safety", "empty content · finish_reason=safety");
        }

        return new AssistantReply { Text = replyText, ToolCalls = calls, StopReason = stopReason };
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "...";

    /// <summary>Model references inside provider error texts: Google's models/&lt;name&gt; and OpenAI's `&lt;name&gt;`.</summary>
    private static readonly Regex ModelReference = new(@"models/([A-Za-z0-9._\-]+)|`([A-Za-z0-9.\-]+)`", RegexOptions.Compiled);

    /// <summary>
    /// Turns a provider's "model unknown/retired" answer into a typed exception.
    /// Error bodies come in two shapes: OpenAI's flat {"error": {...}} and the
    /// Google compatibility layer's wrapped [{"error": {...}}]. Recognises the
    /// wording that names the model itself as the cause, so ordinary 404s stay
    /// HttpRequestExceptions. Returns null when the answer says nothing about
    /// a model.
    /// </summary>
    internal static ModelUnavailableException? ParseModelUnavailable(int status, string body, string requestedModel)
    {
        string? message = null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var err = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0
                ? root[0]
                : root;
            if (err.ValueKind == JsonValueKind.Object &&
                err.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object &&
                e.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
            {
                message = m.GetString();
            }
        }
        catch (JsonException)
        {
            return null;
        }

        if (message is null) return null;

        // Google: "This model models/gemini-2.0-flash is no longer available. Please
        // update your code to use models/gemini-3.8-flash ...". OpenAI: "The model
        // `x` does not exist ...". Both name the model as the cause, unlike a
        // generic NOT_FOUND for a bad URL.
        var namesTheModel =
            message.Contains("no longer available", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("does not exist", StringComparison.OrdinalIgnoreCase);
        if (!namesTheModel) return null;

        string? dead = null;
        string? suggestion = null;
        foreach (var match in ModelReference.Matches(message).Cast<Match>())
        {
            var group = match.Groups[1].Success ? match.Groups[1] : match.Groups[2];
            if (!group.Success) continue;
            if (dead is null) dead = ModelName.Short(group.Value);
            else if (suggestion is null) suggestion = ModelName.Short(group.Value);
        }

        return new ModelUnavailableException(
            dead ?? ModelName.Short(requestedModel),
            suggestion,
            new HttpRequestException($"HTTP {status}: {Truncate(message, 500)}"));
    }

    public void Dispose() => _http.Dispose();
}
