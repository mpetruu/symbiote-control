using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SymbioteControl;

internal static class SelfTest
{
    // Tests only memory allocated in this test process, never the game.
    public static void Run(string reportPath)
    {
        var checks = new List<string>();
        nint allocation = 0;
        Exception? failure = null;
        try
        {
            allocation = Native.VirtualAlloc(0, 4096, 0x3000, 0x04);
            if (allocation == 0) throw new InvalidOperationException("VirtualAlloc failed.");
            var original = new byte[] { 0x90, 0x90, 0x90, 0xC3 };
            var replacement = new byte[] { 0x31, 0xC0, 0x90, 0xC3 };
            var foreign = new byte[] { 0x66, 0x90, 0x90, 0xC3 };
            Marshal.Copy(original, 0, allocation, original.Length);
            if (!Native.VirtualProtect(allocation, 4096, 0x20, out _)) throw new InvalidOperationException("VirtualProtect failed.");
            using var game = GameProcess.Open(Process.GetCurrentProcess());
            var address = (ulong)allocation.ToInt64();
            Require(game.Read(address, 4).AsSpan().SequenceEqual(original), "Exact read");
            using var patch = new ReversibleCodePatch(game, address, original, replacement, "self-test");
            patch.Apply();
            Require(patch.IsApplied && game.Read(address, 4).AsSpan().SequenceEqual(replacement), "Patch apply and readback");
            patch.Apply();
            checks.Add("Repeated apply is idempotent and verified");
            patch.Revert();
            Require(!patch.IsApplied && game.Read(address, 4).AsSpan().SequenceEqual(original), "Patch revert and readback");
            patch.Revert();
            checks.Add("Repeated revert is idempotent");
            game.WriteCode(address, original, foreign);
            ExpectRefusal(() => patch.Apply(), "Unexpected original bytes refuse apply");
            Require(!patch.IsApplied && game.Read(address, 4).AsSpan().SequenceEqual(foreign), "Refusal preserves foreign bytes and ownership");
            game.WriteCode(address, foreign, original);
            patch.Apply();
            game.WriteCode(address, replacement, foreign);
            ExpectRefusal(() => patch.Revert(), "Unexpected enabled bytes refuse revert");
            Require(patch.IsApplied && game.Read(address, 4).AsSpan().SequenceEqual(foreign), "Failed revert preserves foreign bytes and restoration responsibility");
            game.WriteCode(address, foreign, replacement);
            patch.Revert();
            Require(!patch.IsApplied && game.Read(address, 4).AsSpan().SequenceEqual(original), "Restoration retry succeeds after foreign change is removed");
            ExpectRefusal(() => game.WriteCode(address, replacement, foreign), "Conditional code write refuses wrong expected bytes");
            var edge = address + (ulong)Environment.SystemPageSize - 2;
            ExpectRefusal(() => game.WriteCode(edge, original, replacement), "Cross-page code writes are refused");

            void Require(bool condition, string label)
            {
                if (!condition) throw new InvalidOperationException(label);
                checks.Add(label);
            }
            void ExpectRefusal(Action action, string label)
            {
                try { action(); }
                catch (InvalidOperationException) { checks.Add(label); return; }
                catch (ArgumentException) { checks.Add(label); return; }
                throw new InvalidOperationException($"Expected refusal: {label}");
            }
        }
        catch (Exception error) { failure = error; Environment.ExitCode = 1; }
        finally
        {
            if (allocation != 0) Native.VirtualFree(allocation, 0, 0x8000);
            File.WriteAllText(Path.GetFullPath(reportPath), JsonSerializer.Serialize(new
            {
                passed = failure == null,
                scope = "Temporary allocation in the self-test process; no game access",
                checks,
                error = failure?.ToString()
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern nint VirtualAlloc(nint address, nuint size, uint allocationType, uint protection);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool VirtualProtect(nint address, nuint size, uint protection, out uint previous);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool VirtualFree(nint address, nuint size, uint freeType);
    }
}
