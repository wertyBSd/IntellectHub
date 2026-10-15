namespace IntellectHub;

internal sealed class ChatCompletionService
{
    private readonly AccountPool _pool;
    private readonly DeepSeekClient _client;

    public ChatCompletionService(AccountPool pool, DeepSeekClient client)
    {
        _pool = pool;
        _client = client;
    }

    public async Task<UpstreamResult> CompleteAsync(ChatCompletionRequest request, CancellationToken ct)
    {
        var (prompt, model) = Prepare(request);

        var account = Acquire();
        try
        {
            var result = await _client.CompleteAsync(account, model, prompt, null, null, null, null, ct);
            _pool.MarkSuccess(account);
            return result;
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

    public async Task StreamAsync(ChatCompletionRequest request, Func<SseDelta, Task> onDelta, CancellationToken ct)
    {
        var (prompt, model) = Prepare(request);

        var account = Acquire();
        try
        {
            await _client.StreamAsync(account, model, prompt, onDelta, ct);
            _pool.MarkSuccess(account);
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

    private Account Acquire()
    {
        _pool.Reload();
        var account = _pool.Select(null)
            ?? throw new UpstreamException("No DeepSeek account available. Add credentials via the Accounts dialog.", 503);

        if (!_pool.TryAcquire(account))
            throw new UpstreamException("DeepSeek account is busy, try again.", 429);

        return account;
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

    private static (string Prompt, ModelConfig Model) Prepare(ChatCompletionRequest request)
    {
        var messages = request.Messages ?? new List<ChatMessage>();
        var (prompt, systemPrompt) = PromptFormatter.Format(messages);
        if (!string.IsNullOrEmpty(systemPrompt))
            prompt = prompt.Length == 0 ? systemPrompt : systemPrompt + "\n\n" + prompt;

        return (prompt, ModelRegistry.Resolve(request.Model));
    }
}
