namespace IntellectHub;

/// <summary>
/// The outcome of one upstream completion, already reduced to what the client is
/// allowed to see. <see cref="ToolCalls"/> is populated only from well-formed calls
/// to tools the client actually offered.
/// </summary>
internal sealed class CompletionResult
{
    public required string Content { get; init; }
    public string ReasoningContent { get; init; } = "";
    public string? MessageId { get; init; }
    public string? FinishReason { get; init; }
    public IReadOnlyList<ParsedToolCall> ToolCalls { get; init; } = [];

    public bool HasToolCalls => ToolCalls.Count > 0;
}

internal sealed class ChatCompletionService
{
    private readonly AccountPool _pool;
    private readonly IDeepSeekCompletion _client;
    private readonly AppConfig _config;
    private readonly SessionRegistry _sessions = new();

    // In-flight upstream work, and the moment the next send is allowed to go out.
    private int _inFlight;
    private readonly SemaphoreSlim _slots;
    private readonly SemaphoreSlim _pace = new(1, 1);
    private long _nextSendAt;

    public ChatCompletionService(AccountPool pool, IDeepSeekCompletion client, AppConfig config)
    {
        _pool = pool;
        _client = client;
        _config = config.Normalize();
        _slots = new SemaphoreSlim(_config.MaxConcurrent, _config.MaxConcurrent);
    }

    public async Task<CompletionResult> CompleteAsync(ChatCompletionRequest request, CancellationToken clientCt)
    {
        // One budget for the whole inbound request: failover hops and the repair
        // round all draw from it, so a slow login cannot stretch a client wait
        // past the deadline.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(clientCt);
        deadline.CancelAfter(TimeSpan.FromSeconds(_config.RequestDeadlineSeconds));

        var ct = deadline.Token;
        if (!await _slots.WaitAsync(0, ct))
        {
            AppLog.Warn($"overloaded: {_inFlight}/{_config.MaxConcurrent} in flight, shedding");
            throw new UpstreamException(
                $"Server busy ({_inFlight}/{_config.MaxConcurrent} requests in flight). Retry shortly.",
                503, retryAfterMs: 2000, errorKind: ErrorKinds.Overloaded);
        }

        Interlocked.Increment(ref _inFlight);
        var trace = new Trace(Interlocked.Increment(ref _seq));

        // Idle and over-depth sessions are dropped here rather than on a timer, so
        // the registry cannot outlive its usefulness.
        var swept = _sessions.Sweep();
        if (swept > 0) AppLog.Muted($"{trace} убрано протухших сессий: {swept}");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var prepared = Prepare(request, _config);
            AppLog.Info($"{trace} запрос · {Describe(request, prepared)}");

            var upstream = await AskWithRecoveryAsync(prepared, request, trace, ct);
            upstream = await ContinueAsync(prepared, request, upstream, trace, ct);

            var resolution = Resolve(prepared, upstream, trace);
            if (resolution.RetryPrompt is null)
            {
                AppLog.Ok($"{trace} итог · {Summarize(resolution.Result!)} · {clock.ElapsedMilliseconds} мс");
                return resolution.Result!;
            }

            AppLog.Warn($"{trace} {RepairReason(resolution)} · повтор со строгим промптом");
            var repaired = await RepairAsync(prepared, request, resolution.RetryPrompt, upstream, trace, ct);

            AppLog.Ok($"{trace} итог · {Summarize(repaired)} · {clock.ElapsedMilliseconds} мс");
            return repaired;
        }
        catch (OperationCanceledException) when (!clientCt.IsCancellationRequested)
        {
            AppLog.Warn($"{trace} дедлайн {_config.RequestDeadlineSeconds}с · {clock.ElapsedMilliseconds} мс");
            throw new UpstreamException(
                $"Request exceeded the {_config.RequestDeadlineSeconds}s deadline.",
                504, errorKind: ErrorKinds.RequestTimeout);
        }
        catch (UpstreamException ex)
        {
            AppLog.Warn($"{trace} ошибка {ex.StatusCode} ({ex.ErrorKind ?? "upstream"}): {ex.Message} · {clock.ElapsedMilliseconds} мс");
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
            _slots.Release();
        }
    }

    /// <summary>A short request tag so concurrent requests stay readable in the log.</summary>
    internal readonly record struct Trace(long Id)
    {
        public override string ToString() => "#" + Id.ToString("D3");
    }

    private int _seq;

    private static string Describe(ChatCompletionRequest request, Prepared prepared)
    {
        var messages = request.Messages?.Count ?? 0;
        var tools = prepared.Tools.Count;
        var model = ModelRegistry.Canonicalize(request.Model);

        return $"модель={model} · сообщений={messages} · промпт={prepared.Prompt.Length} симв." +
               (tools > 0 ? $" · инструментов={tools}" : string.Empty) +
               (request.Stream ? " · поток" : string.Empty);
    }

    private static string Summarize(CompletionResult result) => result.HasToolCalls
        ? $"вызов инструмента: {string.Join(", ", result.ToolCalls.Select(call => call.Name))}"
        : $"ответ {result.Content.Length} симв.";

    private static string RepairReason(Resolution resolution)
    {
        var prompt = resolution.RetryPrompt ?? string.Empty;
        if (prompt == ToolPrompts.MalformedMarkupRepairPrompt) return "разметка вызова инструмента не разобралась";
        if (prompt == ToolPrompts.CodeDumpRepairPrompt) return "модель выдала исходный код вместо вызова";
        return "модель вызвала встроенный инструмент DeepSeek";
    }

    internal sealed record Prepared(
        string Prompt,
        ModelConfig Model,
        IReadOnlyList<ToolDefinition> Tools,
        IReadOnlySet<string> AllowedNames,
        string SystemPrompt = "",
        int TotalMessages = 0);

    /// <summary>Either a client-safe result, or the prompt for one repair round.</summary>
    internal sealed record Resolution(CompletionResult? Result, string? RetryPrompt)
    {
        public bool Unusable => Result is null && RetryPrompt is null;
    }

    internal static Prepared Prepare(ChatCompletionRequest request, AppConfig config, int startIndex = 0)
    {
        var messages = request.Messages ?? [];
        var tools = request.Tools is { Count: > 0 }
            ? request.Tools.Where(tool => tool is not null).ToList()
            : [];

        var (conversation, systemPrompt) = PromptFormatter.Format(messages, startIndex);

        // Upstream has no tool-calling channel, so the manifest is the only way the
        // model learns that gateway tools exist. Both it and the reminder are
        // reserved before the conversation is given a budget: a manifest cut in
        // half silently removes the model's ability to call anything.
        var reserved = tools.Count > 0
            ? new List<string?>
            {
                ToolPrompts.FormatToolDefinitions(tools, config.MaxPromptChars),
                ToolPrompts.FormatToolReminder(tools),
            }
            : [];

        var prompt = PromptBudget.Build(systemPrompt, conversation, reserved, config.MaxPromptChars);

        return new Prepared(prompt, ModelRegistry.Resolve(request.Model), tools,
            ToolPrompts.AllowedToolNames(tools), systemPrompt, messages.Count);
    }

    /// <summary>
    /// Reduces an upstream reply to a client-safe result, or asks for one repair round
    /// when the model emitted tool markup this gateway cannot honour.
    /// </summary>
    internal static Resolution Resolve(Prepared prepared, UpstreamResult upstream, Trace? trace = null)
    {
        var content = upstream.Content;
        var allowed = prepared.AllowedNames;

        var toolCalls = ToolCallParser.CollectAgentToolCalls(content, allowed);
        if (toolCalls.Count > 0)
        {
            foreach (var call in toolCalls)
                AppLog.Info($"{trace} инструмент {call.Name} · аргументы {call.Arguments.Length} симв.");

            return new Resolution(ToResult(prepared, upstream, "tool_calls", toolCalls), null);
        }

        var selected = ToolCallParser.SelectAgentToolCall(content, allowed);

        string? ignoredNative = null;
        if (selected is not null && ToolCallParser.IsDeepSeekNativeTool(selected.Name) && !allowed.Contains(selected.Name))
        {
            ignoredNative = selected.Name;
            AppLog.Info($"{trace} модель вызвала встроенный инструмент DeepSeek: {selected.Name} — шлюз его выполнить не может");
            selected = null;
        }
        else if (selected is not null && allowed.Count > 0 && !allowed.Contains(selected.Name))
        {
            // The model asked for a tool this client never offered.
            AppLog.Info($"{trace} модель вызвала неизвестный инструмент {selected.Name} — клиент его не предлагал");
            selected = null;
        }
        else if (selected is not null && allowed.Count == 0 && ToolCallParser.LooksLikeToolCallMarkup(content))
        {
            // A chat-only client must never receive raw DSML as its answer.
            ignoredNative = selected.Name;
            selected = null;
            AppLog.Info($"{trace} разметка вызова инструмента, но клиент не предлагал инструментов — текстом не отдаём");
        }
        else if (selected is null && ToolCallParser.LooksLikeToolCallMarkup(content))
        {
            AppLog.Warn($"{trace} в ответе есть разметка вызова инструмента, но она не разобралась");
        }

        var dsmlCalls = ToolCallParser.ListDsmlToolCalls(content);
        var nativeOnlyDsml = dsmlCalls.Count > 0 &&
                            dsmlCalls.All(call => ToolCallParser.IsDeepSeekNativeTool(call.Name));

        var shouldRepairNative = selected is null && (ignoredNative is not null || nativeOnlyDsml);
        var shouldRepairMarkup = selected is null && !shouldRepairNative &&
                                 ToolCallParser.LooksLikeToolCallMarkup(content);
        var shouldRepairCodeDump = allowed.Count > 0 && selected is null && !shouldRepairMarkup &&
                                  !shouldRepairNative &&
                                  ToolCallParser.LooksLikeCodeDumpInsteadOfTool(content);

        if (shouldRepairNative || shouldRepairMarkup || shouldRepairCodeDump)
        {
            var retryPrompt = shouldRepairNative
                ? ToolPrompts.NativeToolRepairPrompt(prepared.Model.SearchEnabled, ignoredNative ?? NativeToolName(dsmlCalls))
                : shouldRepairMarkup
                    ? ToolPrompts.MalformedMarkupRepairPrompt
                    : ToolPrompts.CodeDumpRepairPrompt;

            return new Resolution(null, retryPrompt);
        }

        return new Resolution(ToResult(prepared, upstream, upstream.FinishReason, []), null);
    }

    /// <summary>
    /// Decides what to do with the repair round's reply. Mirrors the suppression
    /// rules in <see cref="Resolve"/>: a client that offered no tools cannot run a
    /// tool call, so none is ever emitted for it. Without this the model can talk
    /// itself into a native tool and the client retries the same impossible call
    /// until it gives up.
    /// </summary>
    internal static Resolution ResolveRepair(Prepared prepared, UpstreamResult retry)
    {
        var retryCalls = ToolCallParser.CollectAgentToolCalls(retry.Content, prepared.AllowedNames);
        if (retryCalls.Count > 0)
            return new Resolution(ToResult(prepared, retry, "tool_calls", retryCalls), null);

        if (prepared.AllowedNames.Count > 0)
        {
            // The client has tools, so a call to one of them is a real answer.
            var retrySelected = ToolCallParser.SelectAgentToolCall(retry.Content, prepared.AllowedNames);
            if (retrySelected is not null && prepared.AllowedNames.Contains(retrySelected.Name))
                return new Resolution(ToResult(prepared, retry, "tool_calls", [retrySelected]), null);

            if (!ToolCallParser.LooksLikeToolCallMarkup(retry.Content))
                return new Resolution(ToResult(prepared, retry, retry.FinishReason, []), null);

            return new Resolution(null, null);
        }

        // No tools were offered: any remaining markup is for a tool the client does
        // not have. Never surface it as an executable call.
        if (ToolCallParser.LooksLikeToolCallMarkup(retry.Content) ||
            ToolCallParser.LooksLikeCodeDumpInsteadOfTool(retry.Content))
            return new Resolution(null, null);

        return new Resolution(ToResult(prepared, retry, retry.FinishReason, []), null);
    }

    private async Task<CompletionResult> RepairAsync(Prepared prepared, ChatCompletionRequest request, string retryPrompt,
        UpstreamResult original, Trace trace, CancellationToken ct)
    {
        UpstreamResult retry;
        try
        {
            retry = await AskAsync(prepared, request, 0, retryPrompt, trace, ct);
        }
        catch (UpstreamException ex) when (ex.StatusCode is 429 or 503)
        {
            // The repair never ran. Fall back to the original reply, unless that
            // reply is itself tool markup.
            if (ToolCallParser.LooksLikeToolCallMarkup(original.Content))
            {
                AppLog.Warn($"{trace} повтор не удался из-за троттлинга ({ex.Message})");
                throw Unusable(ex.Message);
            }

            AppLog.Info($"{trace} повтор не удался ({ex.Message}), возвращаем исходный ответ");
            return ToResult(prepared, original, original.FinishReason, []);
        }

        var resolved = ResolveRepair(prepared, retry);
        if (resolved.Result is not null) return resolved.Result;

        // Still unusable after one repair: fail loudly rather than pass markup on.
        AppLog.Warn($"{trace} повтор не помог — разметка вызова инструмента осталась неиспользуемой");
        throw Unusable();
    }

    private static MalformedToolCallException Unusable(string? detail = null) =>
        new(detail is null
            ? "DeepSeek returned malformed or native-only tool-call markup after one repair attempt"
            : "DeepSeek returned malformed or native-only tool-call markup after one repair attempt " +
              $"(the repair retry failed: {detail})");

    private static string? NativeToolName(IReadOnlyList<ParsedToolCall> dsmlCalls)
    {
        foreach (var call in dsmlCalls)
            if (ToolCallParser.IsDeepSeekNativeTool(call.Name))
                return call.Name;

        return null;
    }

    private static CompletionResult ToResult(Prepared prepared, UpstreamResult upstream, string? finishReason,
        IReadOnlyList<ParsedToolCall> toolCalls) => new()
    {
        Content = toolCalls.Count > 0 ? "" : upstream.Content,
        ReasoningContent = upstream.ReasoningContent,
        MessageId = upstream.MessageId,
        FinishReason = finishReason,
        ToolCalls = toolCalls,
    };

    /// <summary>
    /// Sends the request, rotating to another login when this one is throttled. A
    /// throttled login is already on cooldown, so the next hop picks a different
    /// one; failover hops deliberately do not consume the repair budget.
    ///
    /// The prompt is rebuilt at send time rather than passed in, because a live
    /// remote session changes which part of the conversation is even relevant.
    /// </summary>
    private async Task<UpstreamResult> AskAsync(Prepared prepared, ChatCompletionRequest? request, int budget,
        string? explicitPrompt, Trace trace, CancellationToken ct)
    {
        var maxHops = Math.Max(1, _pool.All.Count + 1);
        SessionState? session = null;
        var startIndex = 0;

        for (var hop = 0; hop < maxHops; hop++)
        {
            ct.ThrowIfCancellationRequested();

            // Pace before taking an account, so the wait does not sit on top of a
            // login another request could have used.
            await PaceAsync(ct);

            var account = await AcquireAsync(trace, ct);

            if (hop == 0)
            {
                // A failover hop must never reuse the previous account's remote
                // session: a DeepSeek chat belongs to the login that created it.
                if (explicitPrompt is null && request is not null)
                {
                    var reuse = ResolveSession(request, prepared, account, trace);
                    session = reuse.Session;
                    startIndex = reuse.StartIndex;
                }
            }
            else
            {
                session = null;
                startIndex = 0;
            }

            var prompt = explicitPrompt ?? BuildPrompt(request, prepared, startIndex, budget);

            AppLog.Info($"{trace} аккаунт {account.Id} · отправка в DeepSeek ({prompt.Length} симв." +
                        (session is null ? ")" : $") · сессия {Shorten(session.ChatSessionId)}, только новые ходы"));
            var hopClock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var result = await _client.CompleteAsync(account, prepared.Model, prompt,
                    session?.ChatSessionId, session?.ParentMessageId, ct);

                _pool.MarkSuccess(account);
                RememberSession(prepared, request, account, result, session);
                AppLog.Ok($"{trace} ответ · {result.Content.Length} симв. · finish={result.FinishReason ?? "stop"} · {hopClock.ElapsedMilliseconds} мс");
                return result;
            }
            catch (UpstreamRateLimitException ex)
            {
                _pool.MarkRateLimited(account, ex.RetryAfterMs);
                account.Failures++;
                account.TotalFailures++;
                account.LastError = ex.Message;

                var pauseSec = Math.Max(1, (int)(_pool.NextCooldownMs() / 1000));
                AppLog.Warn($"{trace} троттлинг · аккаунт {account.Id} на паузе {pauseSec}с · {ex.Message}");

                // Rotate. Once every login is sidelined the next AcquireAsync raises
                // a pool-level 429 carrying the real Retry-After, which is a far more
                // useful thing to hand the client than the last login's raw error.
                continue;
            }
            catch (Exception ex)
            {
                Fail(account, ex);
                throw;
            }
            finally
            {
                _pool.Release(account);
            }
        }

        throw new UpstreamException("No DeepSeek account available. Add credentials via the Accounts dialog.", 503);
    }

    /// <summary>Rebuilds the prompt for exactly what this turn should carry.</summary>
    private string BuildPrompt(ChatCompletionRequest? request, Prepared prepared, int startIndex, int budget)
    {
        if (startIndex <= 0 && budget >= prepared.Prompt.Length) return prepared.Prompt;

        var messages = request?.Messages ?? [];
        var (conversation, systemPrompt) = PromptFormatter.Format(messages, startIndex);

        var reserved = prepared.Tools.Count > 0
            ? new List<string?>
            {
                ToolPrompts.FormatToolDefinitions(prepared.Tools, budget),
                ToolPrompts.FormatToolReminder(prepared.Tools),
            }
            : [];

        return PromptBudget.Build(systemPrompt, conversation, reserved, budget);
    }

    /// <summary>
    /// Decides whether a live remote session can be continued, and from where.
    ///
    /// Every check that cannot be proven is a reset. A remote session we cannot
    /// trust would silently corrupt the conversation, which is strictly worse than
    /// paying for one extra round trip.
    /// </summary>
    private (SessionState? Session, int StartIndex) ResolveSession(ChatCompletionRequest request, Prepared prepared,
        Account account, Trace trace)
    {
        var key = request.SessionKey;
        if (string.IsNullOrEmpty(key)) return (null, 0);

        var state = _sessions.Get(key);
        if (state is null) return (null, 0);

        var systemFingerprint = SystemFingerprintOf(prepared);

        if (state.AccountId != account.Id)
        {
            _sessions.Reset(key);
            AppLog.Muted($"{trace} сессия привязана к другому аккаунту · начинаем заново");
            return (null, 0);
        }

        if (state.SystemFingerprint != systemFingerprint)
        {
            _sessions.Reset(key);
            AppLog.Info($"{trace} инструкции или набор инструментов изменились · начинаем заново");
            return (null, 0);
        }

        var messages = request.Messages ?? [];
        if (state.SentMessages <= 0 || state.SentMessages >= messages.Count)
        {
            _sessions.Reset(key);
            return (null, 0);
        }

        // Prove the client's history still matches what we forwarded. This is the
        // check that makes reuse safe: if the client edited, reordered or rewound
        // the conversation, the remote session no longer describes it.
        var last = messages[state.SentMessages - 1];
        var actual = SessionFingerprint.Of(last.Role ?? "user", PromptFormatter.NormalizeContent(last.Content));
        if (actual != state.SentFingerprint)
        {
            _sessions.Reset(key);
            AppLog.Info($"{trace} история изменилась · начинаем заново");
            return (null, 0);
        }

        // The assistant turn we produced is already in the remote chat as DeepSeek's
        // own reply; replaying it would show the model its own answer twice.
        var start = state.SentMessages;
        while (start < messages.Count && (messages[start].Role ?? "user") == "assistant") start++;

        if (start >= messages.Count)
        {
            // Nothing new to say. A remote session cannot answer an empty turn, and
            // guessing would risk re-running an old request.
            _sessions.Reset(key);
            return (null, 0);
        }

        return (state, start);
    }

    private void RememberSession(Prepared prepared, ChatCompletionRequest? request, Account account,
        UpstreamResult result, SessionState? session)
    {
        var key = request?.SessionKey;
        if (string.IsNullOrEmpty(key) || result.MessageId is null) return;

        // Without a session id there is nothing to continue next turn.
        if (result.ChatSessionId is null) return;
        if (request is null) return;

        var messages = request.Messages ?? [];
        if (messages.Count == 0) return;

        if (session is null)
        {
            // First turn of a conversation: start recording the remote chat that was
            // just created for it.
            _sessions.Save(new SessionState
            {
                AgentId = key,
                AccountId = account.Id,
                ChatSessionId = result.ChatSessionId,
                SystemFingerprint = SystemFingerprintOf(prepared),
                ParentMessageId = result.MessageId,
                SentMessages = messages.Count,
                SentFingerprint = Fingerprint(messages[^1]),
                MessageCount = 1,
            });
            return;
        }

        session.ParentMessageId = result.MessageId;
        session.SentMessages = messages.Count;
        session.SentFingerprint = Fingerprint(messages[^1]);
        session.MessageCount++;
        _sessions.Save(session);
    }

    private static string SystemFingerprintOf(Prepared prepared) =>
        SessionFingerprint.OfSystem(prepared.SystemPrompt, ToolPrompts.ToolFunctionNames(prepared.Tools));

    private static string Fingerprint(ChatMessage message) =>
        SessionFingerprint.Of(message.Role ?? "user", PromptFormatter.NormalizeContent(message.Content));

    private static string Shorten(string id) => id.Length <= 12 ? id : id[..12];

    /// <summary>
    /// Waits for a usable login instead of failing immediately. Several client
    /// connections at once is the normal case for an agent harness, so a busy pool
    /// queues instead of answering 429.
    /// </summary>
    private async Task<Account> AcquireAsync(Trace trace, CancellationToken ct)
    {
        var file = _pool.Reload();
        _pool.LogReload(file);

        var (ready, total) = _pool.Snapshot();
        AppLog.Muted($"{trace} пул аккаунтов: {ready}/{total} готовы");

        var maxWait = TimeSpan.FromSeconds(_config.LockWaitSeconds);
        var deadline = DateTime.UtcNow + maxWait;
        var queued = false;
        var queuedAt = DateTime.UtcNow;

        while (true)
        {
            var account = _pool.Select(null);
            if (account is not null && _pool.TryAcquire(account))
            {
                if (queued)
                    AppLog.Ok($"{trace} вышел из очереди за {Math.Max(1, (int)(DateTime.UtcNow - queuedAt).TotalSeconds)}с");
                return account;
            }

            if (account is null)
            {
                var waitMs = _pool.NextCooldownMs();
                if (waitMs > 0)
                {
                    // Every login is sidelined. Tell the client when to come back.
                    var waitSec = Math.Max(1, (int)(waitMs / 1000));
                    AppLog.Warn($"{trace} все аккаунты на паузе · освободится через ~{waitSec}с");
                    throw new UpstreamRateLimitException(
                        $"Все аккаунты DeepSeek на паузе. Повторите через ~{waitSec}с.", waitMs);
                }

                AppLog.Warn($"{trace} нет доступных аккаунтов DeepSeek");
                throw new UpstreamException(
                    "Нет доступных аккаунтов DeepSeek. Добавьте учётные данные через диалог Аккаунты.", 503);
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                AppLog.Warn($"{trace} все аккаунты заняты дольше {maxWait.TotalSeconds:0}с");
                throw new UpstreamException(
                    $"Все аккаунты DeepSeek заняты другими запросами. Один логин обслуживает один чат.",
                    429, retryAfterMs: 1000, errorKind: ErrorKinds.ConcurrentChatBlocked);
            }

            if (!queued)
            {
                queued = true;
                queuedAt = DateTime.UtcNow;
                AppLog.Info($"{trace} все аккаунты заняты · в очередь, ждём до {maxWait.TotalSeconds:0}с");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(50, Math.Max(1, remaining.TotalMilliseconds))), ct);
        }
    }

    /// <summary>
    /// Optional pacing between consecutive upstream sends. DeepSeek punishes bursts
    /// on one login; this spaces sends across the whole process.
    /// </summary>
    private async Task PaceAsync(CancellationToken ct)
    {
        var interval = _config.MinRequestIntervalMs;
        if (interval <= 0) return;

        await _pace.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var wait = _nextSendAt - now;
            if (wait > 0) await Task.Delay(TimeSpan.FromMilliseconds(wait), ct);

            _nextSendAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + interval;
        }
        finally
        {
            _pace.Release();
        }
    }

    // ---- self-healing ----------------------------------------------------------
    // Both budgets mirror the original gateway: a request may re-ask a few times
    // with a smaller prompt before the client is told the request failed.

    private const int MaxContinuationRounds = 2;
    private const int ContinuationDelayMs = 500;

    /// <summary>A reply this long is treated as cut off even without finish=length.</summary>
    private const int ContinuationLengthThreshold = 25_000;

    /// <summary>Ceiling for the flattened prompt; every shrink is a share of it.</summary>
    private const int MinShrinkChars = 8_000;

    /// <summary>How much of a cut-off answer is quoted back for the continuation.</summary>
    private const int ContinuationQuoteChars = 20_000;

    private const string CompactionMarker = "\n\n[Earlier context compacted by IntellectHub]\n\n";

    private enum RecoveryReason
    {
        None,
        Empty,
        ContextTooLong,
    }

    /// <summary>
    /// Asks, then heals the two failures that would otherwise reach the client as a
    /// broken answer: an empty reply, and a prompt the model cannot fit. Both are
    /// retried with a progressively smaller prompt rather than surfaced as an error.
    /// </summary>
    private async Task<UpstreamResult> AskWithRecoveryAsync(Prepared prepared, ChatCompletionRequest request, Trace trace,
        CancellationToken ct)
    {
        var budget = _config.MaxPromptChars;
        var reason = RecoveryReason.None;
        var maxRetries = _config.MaxRetries;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var result = await AskAsync(prepared, request, budget, null, trace, ct);

                if (!IsEmptyReply(result))
                {
                    if (reason != RecoveryReason.None)
                        AppLog.Ok($"{trace} восстановились с попытки {attempt} · бюджет {budget} симв.");

                    return result;
                }

                reason = RecoveryReason.Empty;
                if (attempt > maxRetries)
                {
                    AppLog.Warn($"{trace} DeepSeek {attempt} раз подряд ответил пустотой");
                    return result;
                }

                AppLog.Warn($"{trace} пустой ответ · попытка {attempt}/{maxRetries}");
            }
            catch (UpstreamException ex) when (ex.ErrorKind == ErrorKinds.ContextTooLong)
            {
                reason = RecoveryReason.ContextTooLong;
                if (attempt > maxRetries) throw;

                AppLog.Warn($"{trace} контекст не помещается · попытка {attempt}/{maxRetries} с усечением");
            }

            budget = ShrinkBudget(ShrinkRatio(reason, attempt));
            await BackoffAsync(attempt, trace, ct);
        }
    }

    /// <summary>
    /// Completes a cut-off answer. The original leans on DeepSeek remembering the
    /// turn; this gateway re-sends the whole transcript on a fresh session every
    /// time, so the partial answer has to be quoted back explicitly.
    /// </summary>
    private async Task<UpstreamResult> ContinueAsync(Prepared prepared, ChatCompletionRequest request,
        UpstreamResult first, Trace trace, CancellationToken ct)
    {
        var content = first.Content;
        var finish = first.FinishReason;
        var messageId = first.MessageId;
        var rounds = 0;

        while (rounds < MaxContinuationRounds && IsTruncated(finish, content, rounds))
        {
            ct.ThrowIfCancellationRequested();
            rounds++;

            AppLog.Info($"{trace} ответ обрезан (finish={finish ?? "stop"}, {content.Length} симв.) " +
                        $"· продолжение {rounds}/{MaxContinuationRounds}");
            await Task.Delay(ContinuationDelayMs, ct);

            UpstreamResult next;
            try
            {
                next = await AskAsync(prepared, request, 0, ContinuationPrompt(content), trace, ct);
            }
            catch (UpstreamRateLimitException ex)
            {
                // Handing back the partial answer beats failing the whole request.
                AppLog.Warn($"{trace} продолжение отменено троттлингом ({ex.Message}) · " +
                            $"отдаём {content.Length} симв. как есть");
                break;
            }

            if (string.IsNullOrWhiteSpace(next.Content))
            {
                AppLog.Warn($"{trace} продолжение вернуло пустоту · отдаём {content.Length} симв. как есть");
                break;
            }

            content += "\n" + next.Content;
            finish = next.FinishReason;
            messageId = next.MessageId ?? messageId;
        }

        return new UpstreamResult(content, first.ReasoningContent, messageId, finish);
    }

    private static bool IsEmptyReply(UpstreamResult result) =>
        string.IsNullOrWhiteSpace(result.Content) && string.IsNullOrWhiteSpace(result.ReasoningContent);

    private static bool IsTruncated(string? finishReason, string content, int rounds) =>
        // The length heuristic only applies to the first reply. After a
        // continuation the combined text is long by definition, and treating that
        // as "cut off" would buy round after round of requests nobody asked for.
        finishReason == "length" || (rounds == 0 && content.Length > ContinuationLengthThreshold);

    private static double ShrinkRatio(RecoveryReason reason, int attempt) => reason switch
    {
        // An oversized prompt needs a far deeper cut than a merely empty reply.
        RecoveryReason.ContextTooLong => Math.Max(0.15, 0.45 - 0.15 * attempt),
        _ => Math.Max(0.5, 1 - 0.2 * attempt),
    };

    private int ShrinkBudget(double ratio) =>
        Math.Max(MinShrinkChars, (int)(_config.MaxPromptChars * ratio));

    private static string ContinuationPrompt(string partial)
    {
        var quoted = partial.Length <= ContinuationQuoteChars ? partial : partial[^ContinuationQuoteChars..];

        return "[CONTINUATION] Your previous answer was cut off. Continue it exactly where it stopped. " +
               "Do not repeat what is already written, do not restart, do not add a preamble.\n\n" +
               "Text so far:\n" + quoted;
    }

    private static async Task BackoffAsync(int attempt, Trace trace, CancellationToken ct)
    {
        var delay = Math.Min(500 * attempt, 1500);
        AppLog.Muted($"{trace} пауза {delay} мс перед повтором");
        await Task.Delay(delay, ct);
    }

    private void Fail(Account account, Exception ex)
    {
        account.Failures++;
        account.TotalFailures++;
        account.LastError = ex.Message;
        if (ex is UpstreamException { StatusCode: 429 } rateLimited)
            _pool.MarkRateLimited(account, rateLimited.RetryAfterMs);
        else if (ex is UpstreamException { StatusCode: 401 or 403 })
            _pool.MarkAuthFailed(account);
    }
}