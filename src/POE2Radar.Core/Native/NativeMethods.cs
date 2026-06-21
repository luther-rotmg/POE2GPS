using System.Runtime.InteropServices;

namespace POE2Radar.Core.Native;

internal static partial class NativeMethods
{
    public const uint PROCESS_VM_READ = 0x0010;
    public const uint PROCESS_VM_WRITE = 0x0020;
    public const uint PROCESS_VM_OPERATION = 0x0008;
    public const uint PROCESS_QUERY_INFORMATION = 0x0400;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    public const uint LIST_MODULES_ALL = 0x03;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint hObject);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool ReadProcessMemory(nint hProcess, nint lpBaseAddress, void* lpBuffer, nuint nSize, out nuint lpNumberOfBytesRead);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool VirtualProtectEx(nint hProcess, nint lpAddress, nuint dwSize, uint flNewProtect, out uint lpflOldProtect);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool WriteProcessMemory(nint hProcess, nint lpBaseAddress, byte* lpBuffer, nuint nSize, out nuint lpNumberOfBytesWritten);

    // NtReadVirtualMemory â€” single syscall, faster than ReadProcessMemory which adds extra validation.
    // Used by ProcessMemoryUtilities and most external memory readers. Signature matches ntdll export.
    [LibraryImport("ntdll.dll")]
    public static unsafe partial int NtReadVirtualMemory(nint hProcess, nint baseAddress, void* buffer, nuint size, out nuint bytesRead);

    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumProcessModulesEx(nint hProcess, [Out] nint[]? lphModule, uint cb, out uint lpcbNeeded, uint dwFilterFlag);

    [LibraryImport("psapi.dll", SetLastError = true, EntryPoint = "GetModuleFileNameExW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint GetModuleFileNameEx(nint hProcess, nint hModule, [Out] char[] lpFilename, uint nSize);

    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetModuleInformation(nint hProcess, nint hModule, out ModuleInformation lpmodinfo, uint cb);

    [StructLayout(LayoutKind.Sequential)]
    public struct ModuleInformation
    {
        public nint LpBaseOfDll;
        public uint SizeOfImage;
        public nint EntryPoint;
    }

    // VirtualQueryEx â€” describes a range of virtual address space in a target process.
    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nuint VirtualQueryEx(nint hProcess, nint lpAddress, out MemoryBasicInformation lpBuffer, nuint dwLength);

    [StructLayout(LayoutKind.Sequential)]
    public struct MemoryBasicInformation
    {
        public nint BaseAddress;
        public nint AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;   // padding before RegionSize on 64-bit; previously alignment bytes
        public ushort _alignmentTail;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    // State values
    public const uint MEM_COMMIT  = 0x00001000;
    public const uint MEM_FREE    = 0x00010000;
    public const uint MEM_RESERVE = 0x00002000;

    // Type values
    public const uint MEM_PRIVATE = 0x00020000;
    public const uint MEM_MAPPED  = 0x00040000;
    public const uint MEM_IMAGE   = 0x01000000;

    // Protect values (bitwise)
    public const uint PAGE_NOACCESS          = 0x01;
    public const uint PAGE_READONLY          = 0x02;
    public const uint PAGE_READWRITE         = 0x04;
    public const uint PAGE_WRITECOPY         = 0x08;
    public const uint PAGE_EXECUTE           = 0x10;
    public const uint PAGE_EXECUTE_READ      = 0x20;
    public const uint PAGE_EXECUTE_READWRITE = 0x40;
    public const uint PAGE_GUARD             = 0x100;
    public const uint PAGE_NOCACHE           = 0x200;
    public const uint PAGE_WRITECOMBINE      = 0x400;

    public const uint READABLE_PROTECT_MASK = PAGE_READONLY | PAGE_READWRITE | PAGE_WRITECOPY | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE;
}
