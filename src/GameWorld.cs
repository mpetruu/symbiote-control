namespace SymbioteControl;

internal sealed record HeroState(
    uint Handle,
    ulong Actor,
    ulong Context,
    ulong Rage,
    ulong Character,
    ulong StateMachine,
    ulong Vanity);

internal sealed class GameWorld(GameProcess game)
{
    private const uint HandleIndexMask = 0xFFFFF;
    private const uint GenerationMask = 0x7FF;

    public byte U8(ulong address) => game.Read(address, 1)[0];
    public ushort U16(ulong address) => BitConverter.ToUInt16(game.Read(address, 2));
    public uint U32(ulong address) => BitConverter.ToUInt32(game.Read(address, 4));
    public ulong Ptr(ulong address) => BitConverter.ToUInt64(game.Read(address, 8));
    public float F32(ulong address) => BitConverter.ToSingle(game.Read(address, 4));

    public HeroState? ResolvePeter()
    {
        var handle = U32(game.BaseAddress + 0x9BD1134);
        var index = handle & HandleIndexMask;
        var generation = (handle >> 20) & GenerationMask;
        var count = U32(game.BaseAddress + 0xC2D3DAC);
        if (index >= count || count > HandleIndexMask + 1) return null;
        var actors = Ptr(game.BaseAddress + 0xC2D3D90);
        if (!IsPointer(actors)) return null;
        var actor = actors + index * 0xC0UL;
        if (U16(actor + 8) != generation) return null;

        var context = FindComponent(actor, game.BaseAddress + 0xB3FB8E0);
        if (!IsPointer(context)) return null;
        var character = Ptr(context + 0x78);
        var rage = Ptr(context + 0x220);
        if (!IsPointer(character) || !IsPointer(rage)) return null;
        if (Ptr(character) != game.BaseAddress + 0x5435C08
            || Ptr(rage) != game.BaseAddress + 0x5439440)
            return null;
        if (U32(character + 0x98) != 0) return null; // Peter only.
        if (Ptr(character + 0x58) != context || Ptr(rage + 0x48) != context
            || Ptr(character + 0x10) != actor || Ptr(rage + 0x10) != actor)
            return null;

        var stateMachine = Ptr(context + 0xB8);
        if (!IsPointer(stateMachine)) return null;
        var vanity = ResolveVanity(context);

        // A load, switch, or actor recycling during the read must not produce a
        // mixed snapshot that could later be used as a write target.
        if (U32(game.BaseAddress + 0x9BD1134) != handle
            || U16(actor + 8) != generation
            || Ptr(character + 0x58) != context || Ptr(rage + 0x48) != context
            || Ptr(character + 0x10) != actor || Ptr(rage + 0x10) != actor)
            return null;
        var resolved = new HeroState(handle, actor, context, rage, character, stateMachine, vanity);
        return IsSame(resolved) ? resolved : null;
    }

    public void Validate(HeroState expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (!IsSame(expected))
            throw new InvalidOperationException("Peter's actor or component identity changed. The write was refused; wait for gameplay to resume.");
    }

    // Lease restoration may target a still-valid Peter actor after the player
    // switches characters. This identity check deliberately does not require
    // the HeroSystem's active handle to still name the saved actor.
    public bool IsSame(HeroState state)
    {
        if (!game.IsAlive || !IsPointer(state.Actor) || !IsPointer(state.Context)
            || !IsPointer(state.Character) || !IsPointer(state.Rage) || !IsPointer(state.StateMachine))
            return false;
        try
        {
            var index = state.Handle & HandleIndexMask;
            var generation = (state.Handle >> 20) & GenerationMask;
            var count = U32(game.BaseAddress + 0xC2D3DAC);
            if (index >= count || count > HandleIndexMask + 1) return false;
            var actors = Ptr(game.BaseAddress + 0xC2D3D90);
            if (!IsPointer(actors) || actors + index * 0xC0UL != state.Actor
                || U16(state.Actor + 8) != generation)
                return false;
            if (FindComponent(state.Actor, game.BaseAddress + 0xB3FB8E0) != state.Context
                || Ptr(state.Context + 0x78) != state.Character
                || Ptr(state.Context + 0x220) != state.Rage
                || Ptr(state.Context + 0xB8) != state.StateMachine)
                return false;
            return Ptr(state.Character) == game.BaseAddress + 0x5435C08
                && Ptr(state.Rage) == game.BaseAddress + 0x5439440
                && U32(state.Character + 0x98) == 0
                && Ptr(state.Character + 0x58) == state.Context
                && Ptr(state.Rage + 0x48) == state.Context
                && Ptr(state.Character + 0x10) == state.Actor
                && Ptr(state.Rage + 0x10) == state.Actor
                && U16(state.Actor + 8) == generation;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    public ulong RageConfig(HeroState state)
    {
        Validate(state);
        var owner = Ptr(state.Context + 0x80);
        if (!IsPointer(owner)) return 0;
        var settings = Ptr(owner + 0x48);
        if (!IsPointer(settings)) return 0;
        var config = Ptr(settings + 0x1340);
        return IsPointer(config) ? config : 0;
    }

    public void Mask(ulong address, byte mask, byte value)
    {
        var current = game.Read(address, 1);
        var next = (byte)((current[0] & ~mask) | (value & mask));
        if (next != current[0]) game.WriteData(address, [next], current);
    }

    private ulong FindComponent(ulong actor, ulong descriptor)
    {
        var capacity = U16(actor + 0x88);
        if (capacity != 0)
        {
            // This layout uses a power-of-two open-addressed table. Reject
            // corrupt capacities rather than probing arbitrary memory.
            if ((capacity & (capacity - 1)) != 0) return 0;
            var slots = Ptr(actor + 0x80);
            if (!IsPointer(slots)) return 0;
            var slot = (descriptor >> 4) & (ulong)(capacity - 1);
            for (var probe = 0; probe < capacity; probe++)
            {
                var pair = slots + slot * 16;
                var key = Ptr(pair);
                if (key == descriptor) return Ptr(pair + 8);
                if (key == 0) return 0;
                slot = (slot + 1) & (ulong)(capacity - 1);
            }
            return 0;
        }
        var list = Ptr(actor + 0x68);
        var count = U16(actor + 0x70);
        if (count == 0 || !IsPointer(list)) return 0;
        for (var index = 0; index < count; index++)
        {
            var pair = list + (ulong)index * 16;
            if (Ptr(pair) == descriptor) return Ptr(pair + 8);
        }
        return 0;
    }

    private ulong ResolveVanity(ulong context)
    {
        var inventory = Ptr(context + 0xC8);
        if (!IsPointer(inventory)) return 0;
        var handle = U32(inventory + 0x2128);
        var index = handle & HandleIndexMask;
        var generation = handle >> 20;
        if (generation == 0) return 0;
        var count = U32(game.BaseAddress + 0xC2D3D40);
        if (index >= count || count > HandleIndexMask + 1) return 0;
        var pool = Ptr(game.BaseAddress + 0xC2D3D20);
        if (!IsPointer(pool)) return 0;
        var slot = pool + index * 16UL;
        if (U32(slot + 8) != generation) return 0;
        var vanity = Ptr(slot);
        if (!IsPointer(vanity) || Ptr(vanity) != game.BaseAddress + 0x5486B58) return 0;
        if (U32(inventory + 0x2128) != handle || U32(slot + 8) != generation || Ptr(slot) != vanity) return 0;
        return vanity;
    }

    private static bool IsPointer(ulong value) => value >= 0x10000 && value <= 0x00007FFFFFFFFFFF && (value & 7) == 0;
}
