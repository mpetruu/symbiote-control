using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace SymbioteControl;

// Optional, explicitly started native helper. Nothing in FeatureBackend uses it.
// Mutation timeouts are unknown outcomes, not permission to repeat a command.
internal sealed class NativeBridge : IDisposable, IAsyncDisposable
{
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    { "b6c5c279a896d94b", "a8e14599ded2e3fd", "96cf9952f3b72010", "9118f67431e68a1d" };
    private readonly Process helper;
    private readonly GameProcess target;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> pending = new();
    private readonly SemaphoreSlim writer = new(1, 1);
    private readonly SemaphoreSlim mutation = new(1, 1);
    private readonly object stateLock = new();
    private readonly StringBuilder diagnostics = new();
    private readonly TaskCompletionSource<JsonElement> startup = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? uncertainMutation;
    private bool disposed;
    private volatile bool expectedOutputClose;

    private NativeBridge(Process helper, GameProcess target)
    {
        this.helper = helper;
        this.target = target;
        _ = ReadStdout();
        _ = ReadStderr();
    }

    public string? UnknownCommandId { get { lock (stateLock) return uncertainMutation; } }
    public string Diagnostics { get { lock (stateLock) return diagnostics.ToString(); } }
    public JsonElement? LastCleanup { get; private set; }
    public JsonElement? LastShutdown { get; private set; }

    public static async Task<NativeBridge> StartAsync(GameProcess game, string? nodePath = null,
        string? bridgePath = null, string? fridaPackagePath = null)
    {
        using (var process = Process.GetProcessById(game.ProcessId))
            GameFingerprint.Validate(process.MainModule?.FileName ?? throw new InvalidOperationException("Cannot identify the native target."));
        nodePath ??= Path.Combine(AppContext.BaseDirectory, "native", "runtime", "node.exe");
        bridgePath ??= Path.Combine(AppContext.BaseDirectory, "native", "bridge.mjs");
        fridaPackagePath ??= Path.Combine(Path.GetDirectoryName(bridgePath)!, "runtime", "node_modules", "frida");
        if (!File.Exists(nodePath) || !File.Exists(bridgePath) || !File.Exists(Path.Combine(fridaPackagePath, "package.json")))
            throw new FileNotFoundException("The optional native runtime is not packaged. Supply explicit existing runtime paths.");
        var start = new ProcessStartInfo(nodePath)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(bridgePath)!
        };
        start.ArgumentList.Add(bridgePath);
        start.ArgumentList.Add("--pid"); start.ArgumentList.Add(game.ProcessId.ToString());
        start.ArgumentList.Add("--frida"); start.ArgumentList.Add(fridaPackagePath);
        var processHelper = Process.Start(start) ?? throw new InvalidOperationException("Cannot start the native helper.");
        var bridge = new NativeBridge(processHelper, game);
        var ready = await Task.WhenAny(bridge.startup.Task, Task.Delay(TimeSpan.FromSeconds(30))).ConfigureAwait(false);
        if (ready != bridge.startup.Task)
            throw new NativeBridgeStartupException("Native helper startup timed out. Its attachment state is unknown; retain Bridge and inspect Status before cleanup.", bridge);
        JsonElement result;
        try { result = await bridge.startup.Task.ConfigureAwait(false); }
        catch (Exception error) { throw new NativeBridgeStartupException($"Native helper startup failed: {error.Message}", bridge); }
        if (!result.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
        {
            var cleaned = result.TryGetProperty("cleaned", out var clean) && clean.GetBoolean();
            if (cleaned)
            {
                bridge.disposed = true;
                processHelper.StandardInput.Close();
                try { await processHelper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                finally { processHelper.Dispose(); }
            }
            throw new NativeBridgeStartupException($"Native helper startup failed: {result}", bridge, cleaned);
        }
        return bridge;
    }

    public Task<JsonElement> Grant(uint handle, string[] ids) => Mutate("grant", handle, ids);
    public Task<JsonElement> Remove(uint handle, string[] ids) => Mutate("remove", handle, ids);
    public Task<JsonElement> Probe(uint handle) => Send("probe", handle, Allowed.ToArray(), null);

    public async Task<JsonElement> Status(string? commandId = null)
    {
        var result = await Send("status", null, null, commandId).ConfigureAwait(false);
        if (result.TryGetProperty("commands", out var commands))
        {
            lock (stateLock)
            {
                foreach (var entry in commands.EnumerateArray())
                    if (entry.TryGetProperty("id", out var id) && id.GetString() == uncertainMutation && Terminal(entry))
                        uncertainMutation = null;
            }
        }
        return result;
    }

    private async Task<JsonElement> Mutate(string operation, uint handle, string[] ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var normalized = ids.Select(id => id.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? id[2..].ToLowerInvariant() : id.ToLowerInvariant()).ToArray();
        if (normalized.Length == 0 || normalized.Length > 4 || normalized.Distinct().Count() != normalized.Length || normalized.Any(id => !Allowed.Contains(id)))
            throw new ArgumentException("Only the four approved unique symbiote asset IDs are allowed.");
        await mutation.WaitAsync().ConfigureAwait(false);
        try
        {
            if (UnknownCommandId != null)
                throw new InvalidOperationException($"Command {UnknownCommandId} has an unknown outcome. Query Status; do not repeat the action.");
            return await Send(operation, handle, normalized, null, true).ConfigureAwait(false);
        }
        finally { mutation.Release(); }
    }

    private async Task<JsonElement> Send(string operation, uint? handle, string[]? ids, string? queryId, bool isMutation = false)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        if (isMutation) { lock (stateLock) uncertainMutation = id; }
        try
        {
            await writer.WaitAsync().ConfigureAwait(false);
            try
            {
                await helper.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { id, command = operation, handle, ids, queryId })).ConfigureAwait(false);
                await helper.StandardInput.FlushAsync().ConfigureAwait(false);
            }
            finally { writer.Release(); }
        }
        catch
        {
            // A pipe failure can occur after a command reached the helper.
            // Retain the command ID and fail closed for every later mutation.
            if (!isMutation) pending.TryRemove(id, out _);
            throw;
        }
        var ready = await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(30))).ConfigureAwait(false);
        if (ready != completion.Task)
        {
            // Keep the completion registered so a late response can reconcile it.
            return JsonSerializer.SerializeToElement(new { id, ok = false, state = "unknown", outcome = "unknown", command = operation,
                error = "No acknowledgement within 30 seconds. Query status with this ID; never repeat the action." });
        }
        var result = await completion.Task.ConfigureAwait(false);
        if (isMutation && Terminal(result)) { lock (stateLock) if (uncertainMutation == id) uncertainMutation = null; }
        return result;
    }

    private async Task ReadStdout()
    {
        try
        {
            while (await helper.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                using var json = JsonDocument.Parse(line);
                var result = json.RootElement.Clone();
                if (!result.TryGetProperty("id", out var identifier)) continue;
                var id = identifier.GetString();
                if ((result.TryGetProperty("detached", out var detached) && detached.ValueKind == JsonValueKind.True)
                    || (result.TryGetProperty("cleaned", out var cleaned) && cleaned.ValueKind == JsonValueKind.True))
                    expectedOutputClose = true;
                if (id == "startup") { startup.TrySetResult(result); continue; }
                if (id != null && pending.TryRemove(id, out var completion)) completion.TrySetResult(result);
                if (Terminal(result)) { lock (stateLock) if (uncertainMutation == id) uncertainMutation = null; }
            }
            if (expectedOutputClose || disposed) return;
            throw new IOException(UnknownCommandId == null
                ? "Native helper output closed unexpectedly."
                : $"Native helper output closed while command {UnknownCommandId} has an unknown outcome.");
        }
        catch (Exception error)
        {
            if ((expectedOutputClose || disposed) && error is ObjectDisposedException) return;
            startup.TrySetException(error);
            foreach (var completion in pending.Values) completion.TrySetException(error);
            lock (stateLock) diagnostics.AppendLine(error.Message);
        }
    }

    private async Task ReadStderr()
    {
        try
        {
            while (await helper.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
                lock (stateLock)
                {
                    diagnostics.AppendLine(line);
                    if (diagnostics.Length > 16384) diagnostics.Remove(0, diagnostics.Length - 16384);
                }
        }
        catch (Exception error) { lock (stateLock) diagnostics.AppendLine(error.Message); }
    }

    private static bool Terminal(JsonElement response) => response.TryGetProperty("state", out var state)
        && state.GetString() is "completed" or "rejected" or "disabled";

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        if (!target.IsAlive)
        {
            var exited = await Send("target-exited", null, null, null).ConfigureAwait(false);
            LastCleanup = exited;
            LastShutdown = exited;
            if (!exited.TryGetProperty("targetExited", out var targetExited) || !targetExited.GetBoolean()
                || !exited.TryGetProperty("detached", out var exitedDetached) || !exitedDetached.GetBoolean())
                throw new InvalidOperationException($"Target exit cleanup was not confirmed; retry after the helper observes process termination: {exited}");
            await CloseHelper().ConfigureAwait(false);
            return;
        }
        if (UnknownCommandId != null)
        {
            await Status(UnknownCommandId).ConfigureAwait(false);
            if (UnknownCommandId != null) throw new InvalidOperationException("Cannot unload while a native command outcome is unknown.");
        }
        var cleanup = await Send("cleanup", null, null, null).ConfigureAwait(false);
        LastCleanup = cleanup;
        if (!cleanup.TryGetProperty("cleanupAcknowledged", out var ack) || !ack.GetBoolean())
            throw new InvalidOperationException($"Native cleanup has not been acknowledged. Remove bridge-owned powers first: {cleanup}");
        var shutdown = await Send("shutdown", null, null, null).ConfigureAwait(false);
        LastShutdown = shutdown;
        if (!shutdown.TryGetProperty("detached", out var detached) || !detached.GetBoolean())
            throw new InvalidOperationException($"Native helper did not confirm script unload and detachment: {shutdown}");
        await CloseHelper().ConfigureAwait(false);
    }

    private async Task CloseHelper()
    {
        disposed = true;
        helper.StandardInput.Close();
        await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        helper.Dispose();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}

internal sealed class NativeBridgeStartupException(string message, NativeBridge bridge, bool cleaned = false) : Exception(message)
{
    public NativeBridge Bridge { get; } = bridge;
    public bool Cleaned { get; } = cleaned;
}
