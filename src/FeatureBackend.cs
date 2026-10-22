using System.Text.Json;

namespace SymbioteControl;

// Existing powers use native data queues. Missing powers use the optional,
// asynchronous helper, with acknowledged ownership and cleanup.
internal sealed class FeatureBackend : IDisposable
{
    private readonly GameProcess game;
    private readonly GameWorld world;
    private HeroState? current;
    private SurgeLease? surge;
    private bool surgeWanted;
    private BlackLease? black;
    private bool blackWanted;
    private bool disposed;
    private bool disposeRequested;
    private NativeBridge? native;
    private Task<NativeBridge>? nativeStartup;
    private NativeOperation? nativeOperation;
    private Task<JsonElement>? nativeQuery;
    private Task? nativeShutdown;
    private bool nativeReady;
    private bool nativeMayOwn;
    private bool nativeLedgerKnown;
    private bool nativeCleanup;
    private bool nativeShutdownFailed;
    private bool nativeRepairUsed;
    private bool nativeTargetExited;
    private string? nativeFailure;
    private uint? repairRequested;
    private long nextNativeQuery;
    private long nativeLedgerEpoch;
    private long nativeQueryEpoch;
    private readonly HashSet<uint> awaitingAbilityCache = [];
    private readonly HashSet<uint> rejectedRepairs = [];
    private readonly HashSet<uint> nativeOwnedInventories = [];
    private readonly HashSet<uint> cleanupAttemptedInventories = [];
    private static readonly string[] RequiredAbilities =
        ["Abilities/SymbiotePunch", "Abilities/SymbioteSpikeBall", "Abilities/SymbioteStrike", "Abilities/SymbioteYank"];
    private static readonly string[] AbilityAssets =
        ["b6c5c279a896d94b", "a8e14599ded2e3fd", "96cf9952f3b72010", "9118f67431e68a1d"];
    private sealed class NativeOperation(string kind, uint handle, Task<JsonElement> task)
    {
        public string Kind = kind;
        public uint Handle = handle;
        public Task<JsonElement>? Task = task;
        public string? CommandId;
    }
    private string reason = "Load Peter in gameplay.";
    private sealed record SurgeLease(HeroState Hero, byte OriginalForce, byte[] Queue, float Meter, bool WasActive)
    {
        public bool SeenActive;
        public long Started = Environment.TickCount64;
    }
    private sealed record AbilityEntry(ulong Address, string Name, byte OriginalStoryBlock, ushort Subtype = 0);
    private sealed class BlackLease(HeroState hero, ulong slot, byte[] originalSlot, ulong asset,
        byte originalLock, ulong abilities, uint originalMode, AbilityEntry[] entries)
    {
        public HeroState Hero = hero;
        public ulong Slot = slot;
        public byte[] OriginalSlot = originalSlot;
        public ulong Asset = asset;
        public byte DesiredLock = originalLock;
        public ulong Abilities = abilities;
        public uint OriginalMode = originalMode;
        public AbilityEntry[] Entries = entries;
        public readonly Dictionary<string, byte> DesiredStoryBlocks = entries
            .GroupBy(entry => entry.Name).ToDictionary(group => group.Key, group => group.First().OriginalStoryBlock);
        public bool SlotPublished;
        public bool ChangesStarted;
        public bool RestorationStarted;
        public bool SlotRestored;
        public bool LockRestored;
        public bool ModeRestored;
        public bool RefreshQueued;
        public readonly HashSet<ulong> RestoredEntries = [];
    }
    public FeatureBackend(GameProcess game)
    {
        this.game = game;
        world = new GameWorld(game);
        CheckCode(0x113c2b0, "C681AC090000018891AD090000448881AE090000C3");
        CheckCode(0x10d2e60, "0FB68190000000A80174080C08888190000000C3");
        Tick();
    }
    public bool SupportsSurge => current != null && !disposeRequested;
    public bool SupportsBlackSuit => current?.Vanity > 0 && !disposeRequested;
    public bool SurgeEnabled => surgeWanted;
    public bool BlackSuitEnabled => blackWanted;
    public bool BlackSuitReady => blackWanted && black != null && HasAllAbilities(black.Entries)
        && nativeOperation == null && nativeStartup == null && !nativeCleanup;
    public string? NativeCommandId => nativeOperation?.CommandId ?? native?.UnknownCommandId;
    public string AvailabilityReason => reason;
    public bool NativeRepairUsed => nativeRepairUsed;
    public bool CleanupPending => nativeCleanup || nativeStartup != null || nativeOperation != null || nativeShutdown != null;

    public void Tick()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var h = game.IsAlive ? world.ResolvePeter() : null;
        AdvanceNative(h);
        if (disposeRequested || nativeCleanup)
        {
            reason = NativeProgress();
            return;
        }
        if (!game.IsAlive) return;
        if (black?.RestorationStarted == true)
        {
            RestoreBlack(black);
            black = null;
            blackWanted = false;
            BeginNativeCleanup();
            AdvanceNative(h);
            if (nativeCleanup) { reason = NativeProgress(); return; }
        }
        if (current != h)
        {
            if (surge != null) { RestoreSurge(surge, false); surge = null; }
            if (black != null) { RestoreBlack(black); black = null; }
            current = h;
        }
        if (h == null)
        {
            reason = "Waiting for Peter. Settings pause during loading and character changes.";
            return;
        }
        if (blackWanted && black == null) ApplyBlack(h);
        if (black != null)
        {
            MaintainBlack(black);
            if (!HasAllAbilities(black.Entries)) RequestRepair(h);
        }
        if (surgeWanted && surge == null) ApplySurge(h);
        if (surge != null)
        {
            world.Validate(h);
            var active = (world.U8(h.Rage + 0x90) & 1) != 0;
            surge.SeenActive |= active;
            reason = active ? "Surge is active. Native forced duration prevents depletion."
                : world.U8(h.StateMachine + 0x9ac) != 0 ? "Surge queued. Return to the game to let it update."
                : "Surge request declined by the current game state. Turn it off and retry in normal gameplay.";
        }
        else reason = blackWanted
            ? black != null && HasAllAbilities(black.Entries) && nativeOperation == null
                ? nativeRepairUsed
                    ? "Black suit and four symbiote powers selected. Turn this off before saving if you want temporary powers removed from the save."
                    : "Black suit and four symbiote powers selected."
                : NativeProgress()
            : "Peter detected. Ready.";
        if (surge != null && blackWanted && (nativeOperation != null || black != null && !HasAllAbilities(black.Entries)))
            reason += " " + NativeProgress();
    }

    public void SetSurge(bool enabled)
    {
        if (enabled && (disposeRequested || nativeCleanup))
            throw new InvalidOperationException("Wait for native cleanup to finish before enabling a setting.");
        if (enabled == surgeWanted) return;
        if (enabled)
        {
            var h = world.ResolvePeter() ?? throw new InvalidOperationException("Load Peter in gameplay first.");
            ApplySurge(h);
            current = h;
            surgeWanted = true;
        }
        else
        {
            if (surge != null) RestoreSurge(surge, true);
            surge = null;
            surgeWanted = false;
        }
    }
    private void ApplySurge(HeroState h)
    {
        world.Validate(h);
        var queue = game.Read(h.StateMachine + 0x9ac, 3);
        if (queue[0] != 0) throw new InvalidOperationException("A transformation is already pending. Wait, then retry.");
        var force = (byte)(world.U8(h.Rage + 0x91) & 6);
        if (force != 0) throw new InvalidOperationException("The story currently controls forced Surge. Wait for normal gameplay.");
        if ((world.U8(h.Rage + 0x90) & 0x20) == 0) throw new InvalidOperationException("Surge is disabled by the current game state.");
        var active = (world.U8(h.Rage + 0x90) & 1) != 0;
        var maximum = world.F32(world.RageConfig(h) + 0x188);
        if (!float.IsFinite(maximum) || maximum <= 0 || maximum > 10000)
            throw new InvalidOperationException("Invalid Surge configuration.");
        surge = new SurgeLease(h, force, queue, world.F32(h.Rage + 0x50), active);
        try
        {
            if (!active) game.WriteData(h.Rage + 0x50, BitConverter.GetBytes(maximum));
            world.Mask(h.Rage + 0x91, 6, 2);
            game.WriteData(h.StateMachine + 0x9ad, [0, 1]);
            // Publish last: game executes full activation on its own update thread.
            game.WriteData(h.StateMachine + 0x9ac, [1], [0]);
        }
        catch
        {
            RestoreSurge(surge, false);
            surge = null;
            throw;
        }
    }
    private void RestoreSurge(SurgeLease lease, bool stop)
    {
        if (!game.IsAlive) return;
        var h = lease.Hero;
        if (!world.IsSame(h)) return; // actor destroyed; never write a reused instance
        world.Validate(h);
        var queue = game.Read(h.StateMachine + 0x9ac, 3);
        if (queue.AsSpan().SequenceEqual(new byte[] { 1, 0, 1 }))
            game.WriteData(h.StateMachine + 0x9ac, lease.Queue, queue);
        var bits = (byte)(world.U8(h.Rage + 0x91) & 6);
        if (bits == 2) world.Mask(h.Rage + 0x91, 6, lease.OriginalForce);
        else if (bits != lease.OriginalForce)
            throw new InvalidOperationException("Game changed Surge ownership. Finish the scripted sequence, then retry detaching.");
        if (stop && !lease.WasActive && (world.U8(h.Rage + 0x90) & 1) != 0)
            world.Mask(h.Rage + 0x90, 8, 8); // exact native graceful-stop request
        else if (!lease.SeenActive && !lease.WasActive && (world.U8(h.Rage + 0x90) & 1) == 0)
            game.WriteData(h.Rage + 0x50, BitConverter.GetBytes(lease.Meter));
    }

    public void SetBlackSuit(bool enabled)
    {
        if (enabled && (disposeRequested || nativeCleanup))
            throw new InvalidOperationException("Wait for temporary-power cleanup to finish, then enable black suit again.");
        if (!enabled && nativeCleanup)
        {
            nativeShutdownFailed = false;
            cleanupAttemptedInventories.Clear();
            AdvanceNative(game.IsAlive ? world.ResolvePeter() : null);
            reason = NativeProgress();
            return;
        }
        if (enabled == blackWanted && (enabled || black == null)) return;
        if (enabled)
        {
            var h = world.ResolvePeter() ?? throw new InvalidOperationException("Load Peter in gameplay first.");
            ApplyBlack(h);
            current = h;
            blackWanted = true;
            if (black != null && !HasAllAbilities(black.Entries)) RequestRepair(h);
        }
        else
        {
            if (black != null) RestoreBlack(black);
            black = null;
            blackWanted = false;
            BeginNativeCleanup();
            AdvanceNative(game.IsAlive ? world.ResolvePeter() : null);
            if (nativeCleanup) reason = NativeProgress();
        }
    }
    private void ApplyBlack(HeroState h)
    {
        world.Validate(h);
        ValidateVanity(h);
        var (sam, entries) = ReadAbilities(h);
        ulong slot = 0;
        byte[]? original = null;
        foreach (var layer in new[] { 1, 0 })
        {
            var candidate = h.Vanity + 0x2250UL + (ulong)layer * 32;
            var bytes = game.Read(candidate, 32);
            if (bytes.All(b => b == 0)) { slot = candidate; original = bytes; break; }
        }
        if (original == null) throw new InvalidOperationException("Both temporary suit layers are occupied. Wait for the scripted suit change to finish.");
        var asset = world.Ptr(world.RageConfig(h) + 0x14c);
        if (asset == 0 || (asset >> 63) == 0) throw new InvalidOperationException("Black-suit asset is unavailable.");
        var lease = new BlackLease(h, slot, original, asset, (byte)(world.U8(h.Character + 0x15d) & 4),
            sam, world.U32(sam + 0x7f4) & 0x80, entries);
        var record = new byte[32];
        BitConverter.GetBytes(asset).CopyTo(record, 0);
        BitConverter.GetBytes(h.Handle).CopyTo(record, 28);
        black = lease;
        try
        {
            // Validate every dependent resource before publishing the suit.
            // A later retry still validates each resource independently.
            foreach (var entry in lease.Entries) ValidateEntry(lease, entry);
            world.Validate(h);
            ValidateVanity(h);
            game.WriteData(slot, record, original);
            lease.SlotPublished = true;
            lease.ChangesStarted = true;
            MaintainBlack(lease);
            world.Mask(h.Character + 0x15d, 1, 1); // native equipment refresh
        }
        catch (Exception applyFailure)
        {
            // A refused conditional publication owns no slot or ability edits.
            // Do not adopt or attempt to undo the game's replacement record.
            if (!lease.SlotPublished)
            {
                black = null;
                throw;
            }
            try
            {
                RestoreBlack(lease);
                black = null;
            }
            catch (Exception rollbackFailure)
            {
                // Keep the lease and its completed cleanup steps for retry.
                throw new AggregateException("Black-suit activation failed, and some owned changes still need restoration.",
                    applyFailure, rollbackFailure);
            }
            throw;
        }
    }
    private void MaintainBlack(BlackLease lease)
    {
        var h = lease.Hero;
        world.Validate(h);
        ValidateVanity(h);
        if (world.Ptr(lease.Slot) != lease.Asset || world.U32(lease.Slot + 28) != h.Handle)
            throw new InvalidOperationException("The game replaced the temporary suit override. Changes paused to preserve its state.");
        ReconcileAbilities(lease);
        world.Mask(h.Character + 0x15d, 4, 0);
        foreach (var entry in lease.Entries)
        {
            ValidateEntry(lease, entry);
            world.Mask(entry.Address + 0x74, 2, 0);
        }
        if ((world.U8(lease.Abilities + 0x7f4) & 0x80) == 0)
            world.Mask(lease.Abilities + 0x7f4, 0x90, 0x90);
        if (HasAllAbilities(lease.Entries)) awaitingAbilityCache.Remove(h.Handle);
    }
    private void RestoreBlack(BlackLease lease)
    {
        if (!game.IsAlive || !world.IsSame(lease.Hero)) return;
        lease.RestorationStarted = true;
        var h = lease.Hero;
        var failures = new List<Exception>();
        void Attempt(Action action)
        {
            try { action(); }
            catch (Exception failure) { failures.Add(failure); }
        }
        // Native additions/removals rebuild this inline cache. Adopt its current
        // entries by identity before restoring; never write stale names/slots.
        if (lease.ChangesStarted) Attempt(() => ReconcileAbilities(lease));

        if (lease.SlotPublished && !lease.SlotRestored) Attempt(() =>
        {
            ValidateVanity(h);
            var currentSlot = game.Read(lease.Slot, 32);
            if (!currentSlot.AsSpan().SequenceEqual(lease.OriginalSlot))
            {
                if (BitConverter.ToUInt64(currentSlot, 0) != lease.Asset
                    || BitConverter.ToUInt32(currentSlot, 28) != h.Handle)
                    throw new InvalidOperationException("Suit override ownership changed. Restoration refused to overwrite another change.");
                game.WriteData(lease.Slot, lease.OriginalSlot, currentSlot);
            }
            // This also accepts a previous clear whose later cleanup failed.
            lease.SlotRestored = true;
            lease.RefreshQueued = false;
        });

        if (lease.ChangesStarted)
        {
            if (!lease.LockRestored) Attempt(() =>
            {
                world.Validate(h);
                world.Mask(h.Character + 0x15d, 4, lease.DesiredLock);
                lease.LockRestored = true;
                lease.RefreshQueued = false;
            });
            foreach (var entry in lease.Entries)
            {
                if (lease.RestoredEntries.Contains(entry.Address)) continue;
                Attempt(() =>
                {
                    ValidateEntry(lease, entry);
                    world.Mask(entry.Address + 0x74, 2, lease.DesiredStoryBlocks.GetValueOrDefault(entry.Name, entry.OriginalStoryBlock));
                    lease.RestoredEntries.Add(entry.Address);
                    lease.RefreshQueued = false;
                });
            }
            if (!lease.ModeRestored) Attempt(() =>
            {
                if (world.Ptr(h.Context + 0x180) != lease.Abilities
                    || world.Ptr(lease.Abilities) != game.BaseAddress + 0x543a578
                    || world.Ptr(lease.Abilities + 0x10) != h.Actor)
                    throw new InvalidOperationException("Ability manager changed. Mode restoration refused.");
                // Both bits belong to this byte. Preserve adjacent native state.
                world.Mask(lease.Abilities + 0x7f4, 0x90, (byte)(lease.OriginalMode | 0x10));
                lease.ModeRestored = true;
                lease.RefreshQueued = false;
            });
        }

        if (!lease.RefreshQueued && (lease.SlotRestored || lease.LockRestored || lease.ModeRestored)) Attempt(() =>
        {
            world.Validate(h);
            world.Mask(h.Character + 0x15d, 1, 1);
            lease.RefreshQueued = true;
        });
        if (failures.Count != 0)
            throw new AggregateException("Some black-suit changes could not be restored. Completed cleanup was retained; retry to finish.", failures);
    }
    private void ValidateEntry(BlackLease lease, AbilityEntry entry)
    {
        if (world.Ptr(lease.Hero.Context + 0x180) != lease.Abilities || world.Ptr(lease.Abilities) != game.BaseAddress + 0x543a578)
            throw new InvalidOperationException("Ability manager changed. Write refused.");
        var raw = game.Read(entry.Address, 32);
        var end = Array.IndexOf(raw, (byte)0);
        if (System.Text.Encoding.ASCII.GetString(raw, 0, end < 0 ? 32 : end) != entry.Name
            || world.U16(entry.Address + 0x24) != entry.Subtype)
            throw new InvalidOperationException("Ability cache changed. Write refused.");
    }
    private (ulong Manager, AbilityEntry[] Entries) ReadAbilities(HeroState h)
    {
        world.Validate(h);
        var sam = world.Ptr(h.Context + 0x180);
        if (world.Ptr(sam) != game.BaseAddress + 0x543a578 || world.Ptr(sam + 0x10) != h.Actor)
            throw new InvalidOperationException("Unexpected ability manager identity.");
        var count = world.U32(sam + 0x658);
        if (count > 12) throw new InvalidOperationException("Invalid ability cache.");
        var entries = new List<AbilityEntry>();
        for (var i = 0; i < count; i++)
        {
            var e = sam + 0x58UL + (ulong)i * 0x80;
            var raw = game.Read(e, 32);
            var end = Array.IndexOf(raw, (byte)0);
            var name = System.Text.Encoding.ASCII.GetString(raw, 0, end < 0 ? 32 : end);
            if (world.U32(e + 0x28) == 1 && name.StartsWith("Abilities/Symbiote", StringComparison.Ordinal))
                entries.Add(new AbilityEntry(e, name, (byte)(world.U8(e + 0x74) & 2), world.U16(e + 0x24)));
        }
        if (world.Ptr(h.Context + 0x180) != sam || world.U32(sam + 0x658) != count)
            throw new InvalidOperationException("Ability cache changed during inspection. Wait for gameplay to update.");
        return (sam, entries.ToArray());
    }
    private static bool HasAllAbilities(IEnumerable<AbilityEntry> entries) =>
        RequiredAbilities.All(name => entries.Any(entry => entry.Name == name));
    private void ReconcileAbilities(BlackLease lease)
    {
        if ((world.U8(lease.Hero.Character + 0x15d) & 4) != 0) lease.DesiredLock = 4;
        var (sam, observed) = ReadAbilities(lease.Hero);
        var changed = sam != lease.Abilities || !observed.Select(entry => (entry.Address, entry.Name, entry.Subtype))
            .SequenceEqual(lease.Entries.Select(entry => (entry.Address, entry.Name, entry.Subtype)));
        if (sam != lease.Abilities)
        {
            lease.Abilities = sam;
            lease.OriginalMode = (uint)(world.U8(sam + 0x7f4) & 0x80);
            lease.ModeRestored = false;
        }
        foreach (var entry in observed)
        {
            var original = lease.DesiredStoryBlocks.GetValueOrDefault(entry.Name,
                lease.DesiredLock != 0 ? (byte)2 : entry.OriginalStoryBlock);
            lease.DesiredStoryBlocks[entry.Name] = (byte)(original | entry.OriginalStoryBlock);
        }
        lease.Entries = observed;
        if (changed) lease.RestoredEntries.Clear();
    }
    private void RequestRepair(HeroState h)
    {
        if (nativeCleanup || disposeRequested || rejectedRepairs.Contains(h.Handle)
            || awaitingAbilityCache.Contains(h.Handle) || nativeOperation?.Handle == h.Handle)
            return;
        repairRequested = h.Handle;
        if (native == null && nativeStartup == null && nativeShutdown == null)
        {
            // Fingerprinting, process startup, attachment, and acknowledgements
            // all execute off the UI thread. Tick only inspects completed tasks.
            nativeStartup = Task.Run(() => NativeBridge.StartAsync(game));
            reason = "Preparing native repair for missing symbiote powers. Return to gameplay; the menu remains responsive.";
        }
    }
    private void BeginNativeCleanup()
    {
        repairRequested = null;
        awaitingAbilityCache.Clear();
        rejectedRepairs.Clear();
        cleanupAttemptedInventories.Clear();
        nativeShutdownFailed = false;
        nativeCleanup = native != null || nativeStartup != null || nativeOperation != null || nativeShutdown != null;
    }
    private void AdvanceNative(HeroState? h)
    {
        if (nativeStartup?.IsCompleted == true)
        {
            var startup = nativeStartup;
            nativeStartup = null;
            try
            {
                native = startup.GetAwaiter().GetResult();
                nativeReady = true;
                nativeFailure = null;
            }
            catch (NativeBridgeStartupException error)
            {
                native = error.Cleaned ? null : error.Bridge;
                nativeReady = false;
                nativeFailure = error.Message;
                if (error.Cleaned && repairRequested is { } handle)
                {
                    rejectedRepairs.Add(handle);
                    repairRequested = null;
                }
            }
            catch (Exception error)
            {
                nativeFailure = error.Message;
                if (repairRequested is { } handle) rejectedRepairs.Add(handle);
                repairRequested = null;
            }
        }

        if (!game.IsAlive)
        {
            nativeTargetExited = nativeRepairUsed;
            repairRequested = null;
            // A dead target cannot execute queued game mutations. The bridge's
            // target-exit shutdown path acknowledges host-only detachment and
            // explicitly does not claim inventory/save restoration.
            nativeOperation = null;
            nativeQuery = null;
            nativeCleanup |= native != null || nativeStartup != null || nativeShutdown != null;
        }

        if (nativeOperation?.Task?.IsCompleted == true)
        {
            var operation = nativeOperation;
            var completion = operation.Task;
            operation.Task = null;
            try
            {
                var result = completion.GetAwaiter().GetResult();
                operation.CommandId = StringField(result, "id") ?? native?.UnknownCommandId;
                if (IsTerminal(result)) CompleteNative(operation, result);
                else nativeFailure = $"Native {operation.Kind} is awaiting acknowledgement. Return to Peter's gameplay; the command will not be repeated.";
            }
            catch (Exception error)
            {
                operation.CommandId = native?.UnknownCommandId;
                nativeFailure = $"Native {operation.Kind} acknowledgement failed: {error.Message}. Its outcome must be reconciled before another mutation.";
            }
            nextNativeQuery = 0;
        }

        if (nativeQuery?.IsCompleted == true)
        {
            var query = nativeQuery;
            nativeQuery = null;
            try
            {
                var status = query.GetAwaiter().GetResult();
                if (!nativeReady && BoolField(status, "bindingEnabled") && BoolField(status, "hookInstalled"))
                {
                    nativeReady = true;
                    nativeFailure = null;
                }
                var reconciled = false;
                if (nativeOperation is { } operation && status.TryGetProperty("commands", out var commands))
                {
                    foreach (var command in commands.EnumerateArray())
                    {
                        if (StringField(command, "id") != operation.CommandId) continue;
                        if (IsTerminal(command)) { CompleteNative(operation, command); reconciled = true; }
                        else nativeFailure = StringField(command, "state") == "unknown"
                            ? "A native command has an unknown outcome. It has not been repeated; keep the menu open for reconciliation."
                            : "A native command is queued. Return to Peter's gameplay so the game can finish it.";
                        break;
                    }
                }
                if (reconciled || nativeQueryEpoch == nativeLedgerEpoch) ReadNativeLedger(status);
            }
            catch (Exception error) { nativeFailure = $"Cannot reconcile native state: {error.Message}"; }
            nextNativeQuery = Environment.TickCount64 + 2000;
        }

        if (nativeShutdown?.IsCompleted == true)
        {
            var shutdown = nativeShutdown;
            nativeShutdown = null;
            try
            {
                shutdown.GetAwaiter().GetResult();
                native = null;
                nativeReady = false;
                nativeMayOwn = false;
                nativeOwnedInventories.Clear();
                nativeLedgerKnown = false;
                nativeCleanup = false;
                nativeFailure = null;
            }
            catch (Exception error)
            {
                nativeShutdownFailed = true;
                nativeFailure = $"Native detachment was not acknowledged: {error.Message}. Keep the menu open and retry detaching.";
            }
        }

        if (nativeShutdown != null) return;
        if (native == null)
        {
            if (nativeCleanup && nativeStartup == null) nativeCleanup = false;
            return;
        }
        if (nativeOperation != null || !nativeReady)
        {
            if (nativeOperation?.Task == null) QueueNativeStatus();
            if (!game.IsAlive && nativeOperation == null && nativeStartup == null && !nativeShutdownFailed)
                nativeShutdown = Task.Run(async () => await native.DisposeAsync().ConfigureAwait(false));
            return;
        }
        if (nativeCleanup)
        {
            if (!game.IsAlive)
            {
                if (!nativeShutdownFailed)
                    nativeShutdown = Task.Run(async () => await native.DisposeAsync().ConfigureAwait(false));
                return;
            }
            if (nativeMayOwn)
            {
                if (h == null) { nativeFailure = "Return to Peter in gameplay to remove temporary powers and finish cleanup."; return; }
                var inventory = EquippedInventory(h);
                if (!cleanupAttemptedInventories.Contains(inventory.Handle)
                    && (!nativeLedgerKnown || nativeOwnedInventories.Contains(inventory.Handle)))
                {
                    cleanupAttemptedInventories.Add(inventory.Handle);
                    nativeLedgerEpoch++;
                    nativeOperation = new NativeOperation("remove", h.Handle,
                        Task.Run(() => native.Remove(h.Handle, AbilityAssets)));
                    nextNativeQuery = 0;
                }
                else QueueNativeStatus();
                return;
            }
            if (!nativeShutdownFailed)
                nativeShutdown = Task.Run(async () => await native.DisposeAsync().ConfigureAwait(false));
            return;
        }
        if (repairRequested is { } requested && h?.Handle == requested && blackWanted)
        {
            if (black != null && HasAllAbilities(black.Entries)) { repairRequested = null; return; }
            world.Validate(h);
            _ = EquippedInventory(h);
            nativeMayOwn = true; // Grant may own an equipment selection even when all items already exist.
            nativeLedgerKnown = false;
            nativeRepairUsed = true;
            awaitingAbilityCache.Add(h.Handle);
            repairRequested = null;
            nativeLedgerEpoch++;
            nativeOperation = new NativeOperation("grant", h.Handle,
                Task.Run(() => native.Grant(h.Handle, AbilityAssets)));
        }
    }
    private void CompleteNative(NativeOperation operation, JsonElement result)
    {
        if (!ReferenceEquals(nativeOperation, operation)) return;
        nativeOperation = null;
        nativeLedgerEpoch++;
        nativeLedgerKnown = false;
        if (BoolField(result, "ok"))
        {
            nativeFailure = null;
            if (operation.Kind == "grant")
            {
                nativeMayOwn = true;
                cleanupAttemptedInventories.Clear();
                if (current?.Handle == operation.Handle && game.IsAlive && world.IsSame(current))
                {
                    var sam = world.Ptr(current.Context + 0x180);
                    if (world.Ptr(sam) == game.BaseAddress + 0x543a578)
                        world.Mask(sam + 0x7f4, 0x10, 0x10);
                }
            }
            else
            {
                nextNativeQuery = 0;
                QueueNativeStatus(); // Ledger acknowledgement must precede unload.
            }
        }
        else
        {
            nativeFailure = $"Native {operation.Kind} was rejected: {StringField(result, "error") ?? StringField(result, "outcome") ?? result.ToString()}";
            if (operation.Kind == "grant")
            {
                awaitingAbilityCache.Remove(operation.Handle);
                rejectedRepairs.Add(operation.Handle);
            }
        }
    }
    private void QueueNativeStatus()
    {
        if (native == null || nativeQuery != null || Environment.TickCount64 < nextNativeQuery) return;
        var bridge = native;
        var commandId = nativeOperation?.CommandId ?? bridge.UnknownCommandId;
        nativeQueryEpoch = nativeLedgerEpoch;
        nativeQuery = Task.Run(() => bridge.Status(commandId));
        nextNativeQuery = Environment.TickCount64 + 2000;
    }
    private void ReadNativeLedger(JsonElement status)
    {
        if (!status.TryGetProperty("leases", out var leases)) return;
        nativeOwnedInventories.Clear();
        var owns = false;
        foreach (var listName in new[] { "leases", "equipmentLeases" })
        {
            if (!status.TryGetProperty(listName, out var list) || list.ValueKind != JsonValueKind.Array) continue;
            foreach (var lease in list.EnumerateArray())
            {
                if (!BoolField(lease, "owned")) continue;
                owns = true;
                if (lease.TryGetProperty("inventoryHandle", out var handle) && handle.TryGetUInt32(out var value))
                    nativeOwnedInventories.Add(value);
            }
        }
        nativeMayOwn = owns;
        nativeLedgerKnown = true;
    }
    private (ulong Address, uint Handle) EquippedInventory(HeroState h)
    {
        world.Validate(h);
        var im = world.Ptr(h.Context + 0xc8);
        var handle = world.U32(im + 0x2118);
        var index = handle & 0xfffff;
        var generation = handle >> 20;
        var count = world.U32(game.BaseAddress + 0xc2d3d40);
        if (generation == 0 || index >= count || count > 0x100000)
            throw new InvalidOperationException("Equipment inventory reference is invalid.");
        var slot = world.Ptr(game.BaseAddress + 0xc2d3d20) + index * 16UL;
        var inventory = world.Ptr(slot);
        if (world.U32(slot + 8) != generation || world.Ptr(inventory) != game.BaseAddress + 0x5484908
            || world.U32(im + 0x2118) != handle)
            throw new InvalidOperationException("Equipment inventory changed during inspection.");
        return (inventory, handle);
    }
    private string NativeProgress()
    {
        if (nativeFailure != null) return nativeFailure;
        if (nativeTargetExited) return "Game exited. Native attachment cleanup is pending; powers already saved may remain in the save.";
        if (nativeShutdown != null) return "Waiting for native helper detachment acknowledgement.";
        if (nativeCleanup) return "Temporary powers are being removed. Return to Peter's gameplay; keep this menu open until cleanup is acknowledged.";
        if (nativeStartup != null) return "Preparing missing-power repair. Return to the game; this menu remains responsive.";
        if (nativeOperation != null) return $"Native {nativeOperation.Kind} is pending. Return to Peter's gameplay; the request will not be repeated.";
        if (awaitingAbilityCache.Count != 0) return "Native powers were repaired. Waiting for the game's ability cache to update; return to gameplay.";
        return "Black suit is selected. Missing powers are awaiting native repair.";
    }
    private static bool BoolField(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    private static string? StringField(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool IsTerminal(JsonElement response) =>
        StringField(response, "state") is "completed" or "rejected" or "disabled";
    private void ValidateVanity(HeroState h)
    {
        if (h.Vanity == 0 || world.Ptr(h.Vanity) != game.BaseAddress + 0x5486b58)
            throw new InvalidOperationException("Suit inventory changed. Write refused.");
        var im = world.Ptr(h.Context + 0xc8);
        var handle = world.U32(im + 0x2128);
        var slot = world.Ptr(game.BaseAddress + 0xc2d3d20) + (handle & 0xfffff) * 16UL;
        if ((handle >> 20) == 0 || world.U32(slot + 8) != (handle >> 20) || world.Ptr(slot) != h.Vanity)
            throw new InvalidOperationException("Suit inventory instance changed. Write refused.");
    }
    private void CheckCode(ulong rva, string hex)
    {
        var bytes = Convert.FromHexString(hex);
        if (!game.Read(game.BaseAddress + rva, bytes.Length).AsSpan().SequenceEqual(bytes))
            throw new InvalidOperationException("Live game code differs from the verified build. Attach refused.");
    }
    public void Dispose()
    {
        if (disposed) return;
        disposeRequested = true;
        surgeWanted = false;
        blackWanted = false;
        if (surge != null) RestoreSurge(surge, true);
        if (black != null) RestoreBlack(black);
        surge = null;
        black = null;
        if (!nativeCleanup) BeginNativeCleanup();
        else nativeShutdownFailed = false; // An explicit detach retries failed host cleanup.
        AdvanceNative(game.IsAlive ? world.ResolvePeter() : null);
        if (nativeCleanup || nativeStartup != null || nativeOperation != null || nativeShutdown != null || native != null)
        {
            reason = NativeProgress();
            throw new InvalidOperationException(reason + " Detachment has not completed; keep the menu open and retry after acknowledgement.");
        }
        disposed = true;
    }
}

