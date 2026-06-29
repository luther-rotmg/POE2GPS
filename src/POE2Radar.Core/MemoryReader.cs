using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using POE2Radar.Core.Native;

namespace POE2Radar.Core;

/// <summary>
/// Low-level typed reads against an attached process. Stateless apart from the handle reference;
/// safe to call from anywhere, but the underlying syscalls are not free â€” wrap hot paths
/// (full struct reads, entity walks) in a single Read call rather than dozens of small ones.
///
/// Memory layout assumption for ReadStruct&lt;T&gt;: T is a blittable, sequential-or-explicit struct.
/// Managed types and reference fields will produce garbage and possibly crash.
/// </summary>
public sealed class MemoryReader
{
    private readonly ProcessHandle _process;
    private long _readCount;
    private long _readBytes;
    private long _failedReads;

    public MemoryReader(ProcessHandle process)
    {
        _process = process;
    }

    public ProcessHandle Process => _process;
    public long ReadCount => Interlocked.Read(ref _readCount);
    public long BytesRead => Interlocked.Read(ref _readBytes);
    public long FailedReads => Interlocked.Read(ref _failedReads);

    /// <summary>Compute reads/sec from a cumulative-count delta over an elapsed window.
    /// Returns 0 for a non-positive window. Pure — unit-tested.</summary>
    public static float ComputeRpmPerSec(long delta, double elapsedSeconds)
        => elapsedSeconds > 0 ? (float)(delta / elapsedSeconds) : 0f;

    /// <summary>Read a single blittable struct value from the target process.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe T ReadStruct<T>(nint address) where T : unmanaged
    {
        T value;
        if (!TryRead(address, &value, (nuint)sizeof(T)))
            throw NewReadException(address, sizeof(T), typeof(T).Name);
        return value;
    }

    /// <summary>Read a pointer-sized value (treated as nint, x64 = 8 bytes).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe nint ReadPointer(nint address)
    {
        nint value;
        if (!TryRead(address, &value, (nuint)sizeof(nint)))
            throw NewReadException(address, sizeof(nint), "pointer");
        return value;
    }

    /// <summary>
    /// Walk a chain of pointers: read a pointer at <paramref name="baseAddress"/>, add offsets[0],
    /// read again, add offsets[1], etc. Returns the final pointer (not dereferenced again).
    /// Mirrors how ExileCore composes long pointer chains into nested structs.
    /// </summary>
    public nint ReadPointerChain(nint baseAddress, params int[] offsets)
    {
        var addr = baseAddress;
        for (var i = 0; i < offsets.Length; i++)
        {
            addr = ReadPointer(addr);
            if (addr == 0) return 0; // null in the middle of a chain â€” common during area transitions
            addr += offsets[i];
        }
        return addr;
    }

    /// <summary>Read raw bytes into a caller-provided buffer. Throws if the entire request couldn't be satisfied.</summary>
    public unsafe int ReadBytes(nint address, Span<byte> destination)
    {
        if (destination.IsEmpty) return 0;
        fixed (byte* p = destination)
        {
            return TryRead(address, p, (nuint)destination.Length, out var bytesRead)
                ? (int)bytesRead
                : throw NewReadException(address, destination.Length, "byte[]");
        }
    }

    /// <summary>
    /// Try-read variant. Returns the number of bytes actually read (0 if the call failed entirely,
    /// or up to destination.Length on partial / full success). Use this for memory scanning where
    /// regions may be torn down mid-walk.
    /// </summary>
    public unsafe int TryReadBytes(nint address, Span<byte> destination)
    {
        if (destination.IsEmpty) return 0;
        fixed (byte* p = destination)
        {
            // Note: NtReadVirtualMemory only sets bytesRead reliably on success â€” on failure the value is
            // implementation-defined. We return 0 for both "nothing read" and "partial read failed" cases.
            // For our scan use case (small fixed chunks within a single region), all-or-nothing is fine.
            TryRead(address, p, (nuint)destination.Length, out var bytesRead);
            return (int)bytesRead;
        }
    }

    /// <summary>Read a fixed-count array of blittable values into a freshly allocated array.</summary>
    public unsafe T[] ReadArray<T>(nint address, int count) where T : unmanaged
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (count == 0) return [];

        var result = new T[count];
        var byteCount = (nuint)((long)count * sizeof(T));
        fixed (T* p = result)
        {
            if (!TryRead(address, p, byteCount))
                throw NewReadException(address, (long)byteCount, $"{typeof(T).Name}[{count}]");
        }
        return result;
    }

    /// <summary>
    /// Read a UTF-16 string up to <paramref name="maxChars"/> chars (game uses UTF-16 widely).
    /// Stops at the first null. Returns empty string if address is 0.
    /// </summary>
    public unsafe string ReadStringUtf16(nint address, int maxChars = 256)
    {
        if (address == 0) return string.Empty;
        if (maxChars <= 0) return string.Empty;

        Span<char> buf = maxChars <= 256 ? stackalloc char[maxChars] : new char[maxChars];
        fixed (char* p = buf)
        {
            if (!TryRead(address, p, (nuint)(maxChars * sizeof(char)), out var bytesRead))
                return string.Empty;
            var charsRead = (int)(bytesRead / sizeof(char));
            var nullIdx = buf[..charsRead].IndexOf('\0');
            return new string(buf[..(nullIdx >= 0 ? nullIdx : charsRead)]);
        }
    }

    /// <summary>Read a UTF-8 string up to <paramref name="maxBytes"/> bytes. Stops at the first null.</summary>
    public unsafe string ReadStringUtf8(nint address, int maxBytes = 256)
    {
        if (address == 0) return string.Empty;
        if (maxBytes <= 0) return string.Empty;

        Span<byte> buf = maxBytes <= 256 ? stackalloc byte[maxBytes] : new byte[maxBytes];
        fixed (byte* p = buf)
        {
            if (!TryRead(address, p, (nuint)maxBytes, out var bytesRead))
                return string.Empty;
            var nullIdx = buf[..(int)bytesRead].IndexOf((byte)0);
            return Encoding.UTF8.GetString(buf[..(nullIdx >= 0 ? nullIdx : (int)bytesRead)]);
        }
    }

    /// <summary>Try-version that returns false on failure instead of throwing. For speculative reads.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe bool TryReadStruct<T>(nint address, out T value) where T : unmanaged
    {
        value = default;
        fixed (T* p = &value)
        {
            return TryRead(address, p, (nuint)sizeof(T));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe bool TryRead(nint address, void* buffer, nuint size)
        => TryRead(address, buffer, size, out _);

    private unsafe bool TryRead(nint address, void* buffer, nuint size, out nuint bytesRead)
    {
        bytesRead = 0;
        if (_process.Handle == 0)
        {
            Interlocked.Increment(ref _failedReads);
            return false;
        }
        if (address == 0 || size == 0)
        {
            Interlocked.Increment(ref _failedReads);
            return false;
        }

        var status = NativeMethods.NtReadVirtualMemory(_process.Handle, address, buffer, size, out bytesRead);
        Interlocked.Increment(ref _readCount);
        if (status == 0 && bytesRead == size)
        {
            Interlocked.Add(ref _readBytes, (long)bytesRead);
            return true;
        }

        Interlocked.Increment(ref _failedReads);
        return false;
    }

    private static InvalidOperationException NewReadException(nint address, long size, string description)
        => new($"Memory read failed: address=0x{address:X}, size={size}, type={description}, lastError={Marshal.GetLastWin32Error()}");
}
