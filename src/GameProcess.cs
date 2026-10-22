using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SymbioteControl;

internal sealed class GameProcess : IDisposable
{
    private readonly SafeProcessHandle handle;
    private readonly Process process;
    private readonly object memoryLock = new();
    private bool disposed;

    private GameProcess(Process process, SafeProcessHandle handle, ulong baseAddress)
    {
        this.process = process;
        this.handle = handle;
        BaseAddress = baseAddress;
        ProcessId = process.Id;
    }

    public ulong BaseAddress { get; }
    public int ProcessId { get; }
    public bool IsAlive
    {
        get { try { return !disposed && !process.HasExited; } catch { return false; } }
    }

    public static GameProcess Open(Process process)
    {
        if (!Environment.Is64BitProcess) throw new PlatformNotSupportedException("Use the x64 build.");
        const uint access = 0x00100000 | 0x1000 | 0x0400 | 0x0010 | 0x0020 | 0x0008;
        var handle = Native.OpenProcess(access, false, process.Id);
        if (handle.IsInvalid) throw Win32("Cannot attach to the game");
        try
        {
            var module = process.MainModule ?? throw new InvalidOperationException("The game module is unavailable.");
            return new GameProcess(process, handle, (ulong)module.BaseAddress.ToInt64());
        }
        catch { handle.Dispose(); throw; }
    }

    public byte[] Read(ulong address, int length)
    {
        lock (memoryLock)
        {
            EnsureAlive();
            ValidateRange(address, length);
            var bytes = new byte[length];
            if (!Native.ReadProcessMemory(handle, (nint)address, bytes, (nuint)bytes.Length, out var count)
                || count != (nuint)length)
                throw Win32($"Cannot read {length} bytes at 0x{address:X}");
            return bytes;
        }
    }

    // Data writes do not change page protections. Supplying expectedBytes makes
    // the write conditional on an exact match at the moment of the read.
    public void WriteData(ulong address, byte[] replacement, byte[]? expectedBytes = null)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        lock (memoryLock)
        {
            EnsureAlive();
            ValidateRange(address, replacement.Length);
            if (expectedBytes != null)
            {
                if (expectedBytes.Length != replacement.Length) throw new ArgumentException("Expected and replacement lengths differ.");
                RequireBytes(address, expectedBytes);
            }
            WriteExact(address, replacement);
        }
    }

    // This API validates byte identity, protection restoration, cache flush, and
    // readback. Callers must choose a safe target execution boundary themselves.
    public void WriteCode(ulong address, byte[] expected, byte[] replacement)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(replacement);
        if (expected.Length != replacement.Length) throw new ArgumentException("A patch must preserve byte length.");
        lock (memoryLock)
        {
            EnsureAlive();
            ValidateRange(address, replacement.Length);
            var pageSize = (ulong)Environment.SystemPageSize;
            if (address / pageSize != (address + (ulong)replacement.Length - 1) / pageSize)
                throw new ArgumentException("A code patch must fit within one memory page to preserve its original protection exactly.");
            RequireBytes(address, expected);
            if (!Native.VirtualProtectEx(handle, (nint)address, (nuint)replacement.Length, 0x40, out var oldProtection))
                throw Win32("Cannot make the patch range writable");

            Exception? failure = null;
            try
            {
                // Check again after changing protection; never overwrite an
                // unrelated change detected before the write.
                RequireBytes(address, expected);
                try
                {
                    WriteExact(address, replacement);
                    Flush(address, replacement.Length);
                    RequireBytes(address, replacement);
                }
                catch (Exception writeFailure)
                {
                    failure = writeFailure;
                    try
                    {
                        var current = Read(address, expected.Length);
                        // A failed partial write may only be rolled back when
                        // every byte belongs to this known transition.
                        for (var i = 0; i < current.Length; i++)
                            if (current[i] != expected[i] && current[i] != replacement[i])
                                throw new InvalidOperationException("Patch rollback refused: unrelated bytes were observed.");
                        WriteExact(address, expected);
                        Flush(address, expected.Length);
                        RequireBytes(address, expected);
                    }
                    catch (Exception rollbackFailure)
                    {
                        failure = new AggregateException("Patch write failed, and restoration could not be confirmed.", writeFailure, rollbackFailure);
                    }
                }
            }
            catch (Exception e) { failure = e; }
            finally
            {
                if (!Native.VirtualProtectEx(handle, (nint)address, (nuint)replacement.Length, oldProtection, out _))
                {
                    var protectionFailure = Win32("Cannot restore the original page protection");
                    failure = failure == null ? protectionFailure : new AggregateException(failure, protectionFailure);
                }
            }
            if (failure != null) throw failure;
        }
    }

    private void RequireBytes(ulong address, byte[] expected)
    {
        if (!Read(address, expected.Length).AsSpan().SequenceEqual(expected))
            throw new InvalidOperationException($"Refused to write 0x{address:X}: the game bytes differ from the expected state.");
    }

    private void WriteExact(ulong address, byte[] bytes)
    {
        if (!Native.WriteProcessMemory(handle, (nint)address, bytes, (nuint)bytes.Length, out var count)
            || count != (nuint)bytes.Length)
            throw Win32($"Cannot write {bytes.Length} bytes at 0x{address:X}");
    }

    private void Flush(ulong address, int length)
    {
        if (!Native.FlushInstructionCache(handle, (nint)address, (nuint)length))
            throw Win32("Cannot flush the instruction cache");
    }

    private void EnsureAlive()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!IsAlive) throw new InvalidOperationException("The game has exited. Attach again after restarting it.");
    }

    private static void ValidateRange(ulong address, int length)
    {
        if (length <= 0 || length > 16 * 1024 * 1024 || address == 0 || address > (ulong)long.MaxValue - (uint)length)
            throw new ArgumentOutOfRangeException(nameof(address), "Invalid memory range.");
    }

    private static Win32Exception Win32(string message) => new(Marshal.GetLastWin32Error(), message);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        handle.Dispose();
        process.Dispose();
    }

    private static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ReadProcessMemory(SafeProcessHandle process, nint address, [Out] byte[] buffer, nuint size, out nuint read);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WriteProcessMemory(SafeProcessHandle process, nint address, byte[] buffer, nuint size, out nuint written);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool VirtualProtectEx(SafeProcessHandle process, nint address, nuint size, uint protection, out uint previous);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool FlushInstructionCache(SafeProcessHandle process, nint address, nuint size);
    }
}
