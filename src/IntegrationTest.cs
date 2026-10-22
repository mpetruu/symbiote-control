using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace SymbioteControl;

internal static class IntegrationTest
{
    private static readonly string[] RequiredAbilities =
    ["Abilities/SymbiotePunch", "Abilities/SymbioteSpikeBall", "Abilities/SymbioteStrike", "Abilities/SymbioteYank"];

    public static async Task RunNativeProbe(string reportPath)
    {
        reportPath = Path.GetFullPath(reportPath);
        var report = new Dictionary<string, object?>
        {
            ["mode"] = "native-probe (read-only native Find/Quantity/metadata calls; attaches helper)",
            ["started"] = DateTimeOffset.UtcNow, ["status"] = "running"
        };
        var errors = new List<string>();
        GameProcess? game = null;
        NativeBridge? bridge = null;
        var cleanupSucceeded = false;
        Save();
        try
        {
            var candidates = Process.GetProcessesByName("Spider-Man2");
            if (candidates.Length != 1)
            {
                foreach (var candidate in candidates) candidate.Dispose();
                throw new InvalidOperationException($"Expected one game process; found {candidates.Length}.");
            }
            var process = candidates[0];
            try
            {
                GameFingerprint.Validate(process.MainModule?.FileName ?? throw new InvalidOperationException("Cannot identify game executable."));
                game = GameProcess.Open(process);
            }
            catch { process.Dispose(); throw; }
            var hero = new GameWorld(game).ResolvePeter() ?? throw new InvalidOperationException("Load Peter in normal gameplay before running the native probe.");
            report["processId"] = game.ProcessId;
            report["hero"] = hero;
            bridge = await NativeBridge.StartAsync(game).ConfigureAwait(false);
            report["initialStatus"] = await bridge.Status().ConfigureAwait(false);
            Save();
            report["probe"] = await bridge.Probe(hero.Handle).ConfigureAwait(false);
            report["finalStatus"] = await bridge.Status().ConfigureAwait(false);
            if (report["probe"] is JsonElement result && (!result.TryGetProperty("ok", out var ok) || !ok.GetBoolean()))
                throw new InvalidOperationException($"Native probe was not acknowledged: {result}");
        }
        catch (NativeBridgeStartupException error)
        {
            bridge = error.Bridge;
            cleanupSucceeded = error.Cleaned;
            report["startupCleaned"] = error.Cleaned;
            errors.Add(error.ToString());
        }
        catch (Exception error) { errors.Add(error.ToString()); }
        finally
        {
            if (bridge != null)
            {
                try
                {
                    await bridge.DisposeAsync().ConfigureAwait(false);
                    report["cleanup"] = bridge.LastCleanup;
                    report["shutdown"] = bridge.LastShutdown;
                    cleanupSucceeded = true;
                }
                catch (Exception error) { errors.Add($"Native probe cleanup: {error}"); }
                report["diagnostics"] = bridge.Diagnostics;
            }
            game?.Dispose();
            report["errors"] = errors;
            report["cleanupSucceeded"] = cleanupSucceeded;
            report["passed"] = errors.Count == 0 && cleanupSucceeded;
            report["status"] = errors.Count == 0 && cleanupSucceeded ? "passed" : "failed";
            report["finished"] = DateTimeOffset.UtcNow;
            if (errors.Count != 0 || !cleanupSucceeded) Environment.ExitCode = 1;
            Save();
        }
        void Save() => File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    internal sealed record Check(string Name, bool Passed, object? Expected, object? Actual);
    internal sealed record Ability(string Address, string Name, uint Installed, byte Flags);
    internal sealed record Snapshot(
        string Stage, DateTimeOffset At, HeroState? Hero, string Availability, bool SupportsSurge, bool SupportsBlackSuit,
        bool SurgeRequested, bool BlackSuitRequested, bool? SurgeActive, float? Meter, float? Maximum,
        byte? RageFlags, byte? ForceFlags, string? StateQueue, string? BlackAsset,
        string[] VanityLayers, byte? CharacterFlags, string? AbilityManager, uint? AbilityFlags,
        Ability[] Cache, Ability?[] ActiveAbilities);

    internal sealed class Report
    {
        public string Mode { get; init; } = "";
        public string Status { get; set; } = "running";
        public bool? Passed { get; set; }
        public DateTimeOffset Started { get; init; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? Finished { get; set; }
        public int? ProcessId { get; set; }
        public string? Executable { get; set; }
        public string ExpectedVersion { get; init; } = GameFingerprint.ExpectedVersion;
        public string ExpectedSha256 { get; init; } = GameFingerprint.ExpectedSha256;
        public List<Snapshot> Snapshots { get; } = [];
        public List<Check> Checks { get; } = [];
        public List<string> Warnings { get; } = [];
        public List<string> Errors { get; } = [];
        public bool CleanupSucceeded { get; set; }
    }

    public static void Run(string reportPath, bool mutate)
    {
        reportPath = Path.GetFullPath(reportPath);
        var report = new Report { Mode = mutate ? "integration-test (game writes)" : "probe (read-only)" };
        GameProcess? game = null;
        FeatureBackend? backend = null;
        GameWorld? world = null;
        Save();
        try
        {
            var processes = Process.GetProcessesByName("Spider-Man2");
            if (processes.Length != 1)
            {
                foreach (var candidate in processes) candidate.Dispose();
                throw new InvalidOperationException($"Expected one Spider-Man2.exe process; found {processes.Length}.");
            }
            var process = processes[0];
            try
            {
                var path = process.MainModule?.FileName ?? throw new InvalidOperationException("Cannot identify the game executable.");
                GameFingerprint.Validate(path);
                report.ProcessId = process.Id;
                report.Executable = path;
                game = GameProcess.Open(process);
            }
            catch { process.Dispose(); throw; }
            world = new GameWorld(game);
            backend = new FeatureBackend(game);
            backend.Tick();
            var before = Capture("before");
            Record("Fingerprint and backend code guards accepted", true, "verified build", GameFingerprint.ExpectedVersion);
            if (!mutate)
            {
                Record("Probe leaves both requested toggles off", !backend.SurgeEnabled && !backend.BlackSuitEnabled,
                    "both false", new { backend.SurgeEnabled, backend.BlackSuitEnabled });
                return;
            }
            if (before.Hero == null || !before.SupportsSurge || !before.SupportsBlackSuit)
                throw new InvalidOperationException("Peter and both feature mechanisms must be available before integration testing.");
            if (before.SurgeActive == true)
                throw new InvalidOperationException("Start this test with Surge inactive so the stop test can verify restoration.");

            backend.SetBlackSuit(true);
            WaitUntil(IsBlackReady, "Black suit and all four native powers", TimeSpan.FromSeconds(20));
            var black = Capture("black suit and four powers confirmed ready");
            var originalLayers = before.VanityLayers;
            var installedLayer = Array.FindIndex(black.VanityLayers, layer =>
                layer.Length == 64 && black.BlackAsset != null && layer[..16] == black.BlackAsset);
            var ownsLayer = installedLayer >= 0 && black.Hero != null
                && black.VanityLayers[installedLayer][56..64] == Convert.ToHexString(BitConverter.GetBytes(black.Hero.Handle));
            Record("Black override asset and actor owner", ownsLayer,
                new { black.BlackAsset, before.Hero.Handle }, new { installedLayer, black.VanityLayers });
            Record("Symbiote ability mode enabled", (black.AbilityFlags & 0x80) != 0, "mode bit 0x80 set", Hex(black.AbilityFlags));
            var activeNames = black.ActiveAbilities.Select(entry => entry?.Name).ToArray();
            Record("Four active slots contain the four symbiote abilities",
                activeNames.Length == 4 && RequiredAbilities.All(name => activeNames.Count(item => item == name) == 1),
                RequiredAbilities, activeNames);
            Record("Symbiote story blocks cleared", RequiredAbilities.All(name => black.Cache.Any(entry => entry.Name == name && (entry.Flags & 2) == 0)),
                "bit 2 clear for all four installed powers", black.Cache);

            backend.SetSurge(true);
            Capture("Surge requested");
            var activation = Stopwatch.StartNew();
            var warned = false;
            while (!Active())
            {
                if (activation.Elapsed > TimeSpan.FromSeconds(2) && !warned)
                {
                    report.Warnings.Add("Surge has not activated after two seconds. The game may be paused or unfocused; waiting up to 30 seconds for native updates.");
                    warned = true;
                    Save();
                }
                if (activation.Elapsed > TimeSpan.FromSeconds(30))
                    throw new TimeoutException("Surge did not activate within 30 seconds. Confirm the game is focused and normal gameplay is updating.");
                Pump(TimeSpan.FromMilliseconds(100));
            }
            var sampleClock = Stopwatch.StartNew();
            foreach (var seconds in new[] { 0, 5, 10, 15 })
            {
                var remaining = TimeSpan.FromSeconds(seconds) - sampleClock.Elapsed;
                if (remaining > TimeSpan.Zero) Pump(remaining);
                var sample = Capture($"Surge active sample {seconds}s");
                Record($"Surge remains active at {seconds}s", sample.SurgeActive == true, true, sample.SurgeActive);
                Record($"Surge meter is 100 at {seconds}s", sample.Meter is >= 99.99f and <= 100.01f, 100f, sample.Meter);
                Record($"Forced duration is owned at {seconds}s", (sample.ForceFlags & 6) == 2, "masked value 2", sample.ForceFlags);
            }

            backend.SetSurge(false);
            Pump(TimeSpan.FromSeconds(2));
            var stopped = Capture("Surge disabled, after 2s");
            Record("Surge gracefully stops when disabled", stopped.SurgeActive == false, false, stopped.SurgeActive);
            Record("Forced duration bits restore", (stopped.ForceFlags & 6) == (before.ForceFlags & 6), before.ForceFlags & 6, stopped.ForceFlags & 6);

            backend.SetBlackSuit(false);
            WaitUntil(() => !backend.CleanupPending, "Native cleanup acknowledgement", TimeSpan.FromSeconds(20));
            Pump(TimeSpan.FromSeconds(2));
            var restored = Capture("Black suit disabled and cleanup acknowledged");
            Record("Both vanity override slots restore exactly", restored.VanityLayers.SequenceEqual(originalLayers), originalLayers, restored.VanityLayers);
            Record("Original ability mode bit restores", (restored.AbilityFlags & 0x80) == (before.AbilityFlags & 0x80), before.AbilityFlags & 0x80, restored.AbilityFlags & 0x80);
            Record("Original active ability choices restore",
                restored.ActiveAbilities.Select(entry => entry?.Name).SequenceEqual(before.ActiveAbilities.Select(entry => entry?.Name)),
                before.ActiveAbilities, restored.ActiveAbilities);
            Record("Original symbiote story block bits restore", before.Cache.Where(entry => RequiredAbilities.Contains(entry.Name)).All(original =>
                    restored.Cache.Any(entry => entry.Name == original.Name && (entry.Flags & 2) == (original.Flags & 2))),
                before.Cache.Where(entry => RequiredAbilities.Contains(entry.Name)).ToArray(), restored.Cache.Where(entry => RequiredAbilities.Contains(entry.Name)).ToArray());
            Record("Temporary native powers return to baseline ownership", RequiredAbilities.All(name =>
                    before.Cache.Any(entry => entry.Name == name && entry.Installed == 1) == restored.Cache.Any(entry => entry.Name == name && entry.Installed == 1)),
                before.Cache.Where(entry => RequiredAbilities.Contains(entry.Name)).ToArray(), restored.Cache.Where(entry => RequiredAbilities.Contains(entry.Name)).ToArray());
            Record("Original suit lock bit restores", (restored.CharacterFlags & 4) == (before.CharacterFlags & 4), before.CharacterFlags & 4, restored.CharacterFlags & 4);

            bool Active()
            {
                var hero = world.ResolvePeter() ?? throw new InvalidOperationException("Peter became unavailable during the integration test.");
                world.Validate(hero);
                return (world.U8(hero.Rage + 0x90) & 1) != 0;
            }
        }
        catch (Exception error)
        {
            report.Errors.Add(error.ToString());
        }
        finally
        {
            var cleanupErrors = new List<string>();
            if (backend != null)
            {
                if (mutate)
                {
                    TryCleanup("Disable Surge", () => backend.SetSurge(false));
                    TryCleanup("Disable black suit", () => backend.SetBlackSuit(false));
                }
                var cleanupClock = Stopwatch.StartNew();
                while (true)
                {
                    try { backend.Dispose(); break; }
                    catch (InvalidOperationException error) when (backend.CleanupPending && cleanupClock.Elapsed < TimeSpan.FromSeconds(20))
                    {
                        if (!report.Warnings.Contains("Waiting for asynchronous native cleanup acknowledgement."))
                            report.Warnings.Add("Waiting for asynchronous native cleanup acknowledgement.");
                        try { Pump(TimeSpan.FromMilliseconds(100)); }
                        catch (Exception pumpError) { cleanupErrors.Add($"Cleanup update: {pumpError}"); break; }
                        _ = error;
                    }
                    catch (Exception error) { cleanupErrors.Add($"Dispose backend: {error}"); break; }
                }
            }
            if (game != null) TryCleanup("Close process handle", game.Dispose);
            report.CleanupSucceeded = cleanupErrors.Count == 0;
            report.Errors.AddRange(cleanupErrors);
            report.Passed = report.Errors.Count == 0 && report.Checks.All(check => check.Passed);
            report.Status = report.Passed == true ? "passed" : "failed";
            report.Finished = DateTimeOffset.UtcNow;
            if (report.Passed != true) Environment.ExitCode = 1;
            Save();

            void TryCleanup(string operation, Action action)
            {
                try { action(); }
                catch (Exception error) { cleanupErrors.Add($"{operation}: {error}"); }
            }
        }

        Snapshot Capture(string stage)
        {
            if (game == null || world == null || backend == null) throw new InvalidOperationException("Not attached.");
            var hero = world.ResolvePeter();
            Snapshot snapshot;
            if (hero == null)
            {
                snapshot = new Snapshot(stage, DateTimeOffset.UtcNow, null, backend.AvailabilityReason,
                    backend.SupportsSurge, backend.SupportsBlackSuit, backend.SurgeEnabled, backend.BlackSuitEnabled,
                    null, null, null, null, null, null, null, [], null, null, null, [], []);
            }
            else
            {
                world.Validate(hero);
                var config = world.RageConfig(hero);
                var sam = world.Ptr(hero.Context + 0x180);
                if (world.Ptr(sam) != game.BaseAddress + 0x543A578 || world.Ptr(sam + 0x10) != hero.Actor)
                    throw new InvalidOperationException("Snapshot found an unexpected ability manager identity.");
                var count = world.U32(sam + 0x658);
                if (count > 12) throw new InvalidOperationException("Snapshot found an invalid ability cache count.");
                var entries = Enumerable.Range(0, (int)count).Select(index => ReadAbility(sam + 0x58UL + (ulong)index * 0x80)).ToArray();
                var active = Enumerable.Range(0, 4).Select(index =>
                {
                    var pointer = world.Ptr(sam + 0x660UL + (ulong)index * 8);
                    if (pointer == 0) return null;
                    if (pointer < sam + 0x58 || pointer >= sam + 0x58 + count * 0x80UL || (pointer - sam - 0x58) % 0x80 != 0)
                        throw new InvalidOperationException($"Active slot {index} points outside the verified inline cache: 0x{pointer:X}.");
                    return ReadAbility(pointer);
                }).ToArray();
                snapshot = new Snapshot(stage, DateTimeOffset.UtcNow, hero, backend.AvailabilityReason,
                    backend.SupportsSurge, backend.SupportsBlackSuit, backend.SurgeEnabled, backend.BlackSuitEnabled,
                    (world.U8(hero.Rage + 0x90) & 1) != 0, world.F32(hero.Rage + 0x50), config == 0 ? null : world.F32(config + 0x188),
                    world.U8(hero.Rage + 0x90), world.U8(hero.Rage + 0x91), Convert.ToHexString(game.Read(hero.StateMachine + 0x9AC, 3)),
                    config == 0 ? null : Convert.ToHexString(game.Read(config + 0x14C, 8)),
                    hero.Vanity == 0 ? [] : [Convert.ToHexString(game.Read(hero.Vanity + 0x2250, 32)), Convert.ToHexString(game.Read(hero.Vanity + 0x2270, 32))],
                    world.U8(hero.Character + 0x15D), $"0x{sam:X}", world.U32(sam + 0x7F4), entries, active);

                Ability ReadAbility(ulong address)
                {
                    var bytes = game.Read(address, 32);
                    var end = Array.IndexOf(bytes, (byte)0);
                    return new Ability($"0x{address:X}", Encoding.ASCII.GetString(bytes, 0, end < 0 ? bytes.Length : end), world.U32(address + 0x28), world.U8(address + 0x74));
                }
            }
            report.Snapshots.Add(snapshot);
            Save();
            return snapshot;
        }

        void Pump(TimeSpan duration)
        {
            var clock = Stopwatch.StartNew();
            do
            {
                backend!.Tick();
                var remainder = duration - clock.Elapsed;
                if (remainder > TimeSpan.Zero) Thread.Sleep((int)Math.Min(100, Math.Max(1, remainder.TotalMilliseconds)));
            } while (clock.Elapsed < duration);
            backend!.Tick();
        }

        void WaitUntil(Func<bool> ready, string operation, TimeSpan timeout)
        {
            var clock = Stopwatch.StartNew();
            while (!ready())
            {
                if (clock.Elapsed >= timeout) throw new TimeoutException($"{operation} did not complete in {timeout.TotalSeconds:0}s. {backend!.AvailabilityReason}");
                Pump(TimeSpan.FromMilliseconds(100));
            }
        }

        bool IsBlackReady()
        {
            if (game == null || world == null || backend == null || !backend.BlackSuitReady) return false;
            var hero = world.ResolvePeter();
            if (hero == null || hero.Vanity == 0) return false;
            world.Validate(hero);
            var sam = world.Ptr(hero.Context + 0x180);
            if ((world.U32(sam + 0x7F4) & 0x80) == 0) return false;
            var names = Enumerable.Range(0, 4).Select(index =>
            {
                var entry = world.Ptr(sam + 0x660UL + (ulong)index * 8);
                if (entry == 0) return "";
                var raw = game.Read(entry, 32);
                var end = Array.IndexOf(raw, (byte)0);
                return Encoding.ASCII.GetString(raw, 0, end < 0 ? raw.Length : end);
            }).ToArray();
            if (!RequiredAbilities.All(name => names.Count(value => value == name) == 1)) return false;
            var asset = world.Ptr(world.RageConfig(hero) + 0x14C);
            return Enumerable.Range(0, 2).Any(index =>
            {
                var layer = hero.Vanity + 0x2250UL + (ulong)index * 32;
                return world.Ptr(layer) == asset && world.U32(layer + 28) == hero.Handle;
            });
        }

        void Record(string name, bool passed, object? expected, object? actual)
        {
            report.Checks.Add(new Check(name, passed, expected, actual));
            Save();
        }

        void Save() => File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string? Hex(uint? value) => value.HasValue ? $"0x{value:X}" : null;
}
