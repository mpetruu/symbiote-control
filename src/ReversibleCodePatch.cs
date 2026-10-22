namespace SymbioteControl;

internal sealed class ReversibleCodePatch : IDisposable
{
    private readonly GameProcess game;
    private readonly ulong address;
    private readonly byte[] original;
    private readonly byte[] replacement;
    private readonly string name;

    public ReversibleCodePatch(GameProcess game, ulong address, byte[] original, byte[] replacement, string name)
    {
        if (original.Length == 0 || original.Length != replacement.Length)
            throw new ArgumentException("A patch must have equal nonempty original and replacement arrays.");
        this.game = game;
        this.address = address;
        this.original = (byte[])original.Clone();
        this.replacement = (byte[])replacement.Clone();
        this.name = name;
    }

    public bool IsApplied { get; private set; }

    public void Apply()
    {
        if (IsApplied)
        {
            if (!game.Read(address, replacement.Length).AsSpan().SequenceEqual(replacement))
                throw new InvalidOperationException($"{name}: the enabled patch was changed externally.");
            return;
        }
        if (!game.Read(address, original.Length).AsSpan().SequenceEqual(original))
            throw new InvalidOperationException($"{name}: enable refused because the original bytes differ.");
        // Keep restoration responsibility on write failures until readback
        // positively confirms the original bytes.
        IsApplied = true;
        try { game.WriteCode(address, original, replacement); }
        catch
        {
            try { if (game.Read(address, original.Length).AsSpan().SequenceEqual(original)) IsApplied = false; } catch { }
            throw;
        }
    }

    public void Revert()
    {
        if (!IsApplied) return;
        if (!game.IsAlive) { IsApplied = false; return; }
        var current = game.Read(address, original.Length);
        if (current.AsSpan().SequenceEqual(original)) { IsApplied = false; return; }
        if (!current.AsSpan().SequenceEqual(replacement))
            throw new InvalidOperationException($"{name}: restoration refused because the patch bytes changed externally.");
        game.WriteCode(address, replacement, original);
        IsApplied = false;
    }

    public void Dispose() => Revert();
}
