using System.Collections.Concurrent;
using System.Text;
using Wasmtime;

namespace IntellectHub;

internal sealed class WasmtimePowSolverProvider : IPowSolverProvider
{
    public IPowSolver? Solver { get; }

    public WasmtimePowSolverProvider(HttpClient http) => Solver = new WasmtimePowSolver(http);
}

internal sealed class WasmtimePowSolver : IPowSolver
{
    private static readonly TimeSpan WasmFetchTimeout = TimeSpan.FromSeconds(15);

    private readonly HttpClient _http;
    private readonly Engine _engine = new();
    private readonly ConcurrentDictionary<string, Task<Module>> _modules = new();

    public WasmtimePowSolver(HttpClient http) => _http = http;

    public async Task<long> SolveAsync(DeepSeekChallenge challenge, string wasmUrl, CancellationToken ct)
    {
        var module = await GetModuleAsync(wasmUrl, ct);

        using var store = new Store(_engine);
        using var linker = new Linker(_engine);
        var instance = linker.Instantiate(store, module);

        var memory = instance.GetMemory("memory")
            ?? throw new InvalidOperationException("PoW: module exports no memory");
        var malloc = instance.GetFunction<int, int, int>("__wbindgen_export_0")
            ?? throw new InvalidOperationException("PoW: module exports no __wbindgen_export_0");
        var adjust = instance.GetFunction<int, int>("__wbindgen_add_to_stack_pointer")
            ?? throw new InvalidOperationException("PoW: module exports no __wbindgen_add_to_stack_pointer");
        var solve = instance.GetFunction("wasm_solve")
            ?? throw new InvalidOperationException("PoW: module exports no wasm_solve");

        var challengeBytes = Encoding.UTF8.GetBytes(challenge.Challenge ?? string.Empty);
        var prefixBytes = Encoding.UTF8.GetBytes($"{challenge.Salt}_{challenge.ExpireAt}_");

        var challengePtr = malloc(challengeBytes.Length, 1) >>> 0;
        var prefixPtr = malloc(prefixBytes.Length, 1) >>> 0;
        challengeBytes.CopyTo(memory.GetSpan(challengePtr, challengeBytes.Length));
        prefixBytes.CopyTo(memory.GetSpan(prefixPtr, prefixBytes.Length));

        var stackPtr = adjust(-16) >>> 0;
        solve.Invoke(
            (ValueBox)stackPtr,
            (ValueBox)challengePtr,
            (ValueBox)challengeBytes.Length,
            (ValueBox)prefixPtr,
            (ValueBox)prefixBytes.Length,
            (ValueBox)challenge.Difficulty);

        var code = memory.ReadInt32(stackPtr);
        var answer = memory.ReadDouble(stackPtr + 8);
        adjust(16);

        if (code == 0 || !double.IsFinite(answer) || answer <= 0)
            throw new InvalidOperationException("PoW: solver returned no answer");

        return (long)Math.Floor(answer);
    }

    private async Task<Module> GetModuleAsync(string wasmUrl, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(wasmUrl)) throw new InvalidOperationException("PoW: missing wasmUrl");

        var entry = _modules.GetOrAdd(wasmUrl, url => LoadModuleAsync(url, ct));
        try
        {
            return await entry;
        }
        catch
        {
            _modules.TryRemove(wasmUrl, out _);
            throw;
        }
    }

    private async Task<Module> LoadModuleAsync(string wasmUrl, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(WasmFetchTimeout);
        var bytes = await _http.GetByteArrayAsync(wasmUrl, timeout.Token);
        return Module.FromBytes(_engine, wasmUrl, bytes);
    }
}
