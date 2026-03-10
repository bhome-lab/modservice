using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ModService.Host;

public static class ProcessEnvironmentReader
{
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ProcessVmRead = 0x0010;
    private const ushort ImageFileMachineUnknown = 0;
    private const int ProcessBasicInformation = 0;
    private const int ProcessWow64Information = 26;
    private const ulong MaxEnvironmentBytes = 1_048_576;
    private const int PebProcessParametersOffset32 = 0x10;
    private const int PebProcessParametersOffset64 = 0x20;
    private const int ProcessParametersEnvironmentOffset32 = 0x48;
    private const int ProcessParametersEnvironmentOffset64 = 0x80;

    public static IReadOnlyDictionary<string, string> Read(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        if (process.Id == Environment.ProcessId)
        {
            return Environment.GetEnvironmentVariables()
                .Cast<System.Collections.DictionaryEntry>()
                .ToDictionary(
                    entry => (string)entry.Key,
                    entry => (string?)entry.Value ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase);
        }

        var access = ProcessQueryInformation | ProcessQueryLimitedInformation | ProcessVmRead;
        var handle = OpenProcess(access, inheritHandle: false, process.Id);
        if (handle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"OpenProcess failed for pid {process.Id}.");
        }

        try
        {
            var is32BitPointers = Is32BitProcess(handle);
            var pebAddress = GetPebAddress(handle, is32BitPointers);
            var processParametersAddress = ReadPointer(
                handle,
                pebAddress + (ulong)(is32BitPointers ? PebProcessParametersOffset32 : PebProcessParametersOffset64),
                is32BitPointers);
            var environmentAddress = ReadPointer(
                handle,
                processParametersAddress + (ulong)(is32BitPointers ? ProcessParametersEnvironmentOffset32 : ProcessParametersEnvironmentOffset64),
                is32BitPointers);

            if (environmentAddress == 0)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            var environmentBytes = ReadEnvironmentBlock(handle, environmentAddress);
            return ParseEnvironmentBlock(environmentBytes);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static bool Is32BitProcess(IntPtr processHandle)
    {
        if (!Environment.Is64BitOperatingSystem)
        {
            return true;
        }

        if (TryIsWow64Process2(processHandle, out var processMachine))
        {
            return processMachine != ImageFileMachineUnknown;
        }

        if (!IsWow64Process(processHandle, out var isWow64))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "IsWow64Process failed.");
        }

        return isWow64;
    }

    private static bool TryIsWow64Process2(IntPtr processHandle, out ushort processMachine)
    {
        processMachine = ImageFileMachineUnknown;
        try
        {
            if (!IsWow64Process2(processHandle, out processMachine, out _))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == 0)
                {
                    return false;
                }

                throw new Win32Exception(error, "IsWow64Process2 failed.");
            }

            return true;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static ulong GetPebAddress(IntPtr processHandle, bool is32BitPointers)
    {
        if (is32BitPointers && Environment.Is64BitProcess)
        {
            IntPtr wow64PebAddress;
            var status = NtQueryInformationProcess(
                processHandle,
                ProcessWow64Information,
                out wow64PebAddress,
                IntPtr.Size,
                out _);
            if (status != 0)
            {
                throw new Win32Exception($"NtQueryInformationProcess(ProcessWow64Information) failed with NTSTATUS 0x{status:X8}.");
            }

            return (ulong)(uint)wow64PebAddress.ToInt64();
        }

        var basicInfoStatus = NtQueryInformationProcess(
            processHandle,
            ProcessBasicInformation,
            out ProcessBasicInformationData basicInformation,
            Marshal.SizeOf<ProcessBasicInformationData>(),
            out _);
        if (basicInfoStatus != 0)
        {
            throw new Win32Exception($"NtQueryInformationProcess(ProcessBasicInformation) failed with NTSTATUS 0x{basicInfoStatus:X8}.");
        }

        return unchecked((ulong)(nuint)basicInformation.PebBaseAddress);
    }

    private static ulong ReadPointer(IntPtr processHandle, ulong address, bool is32BitPointers)
    {
        var buffer = new byte[is32BitPointers ? sizeof(uint) : sizeof(ulong)];
        ReadExact(processHandle, address, buffer);
        return is32BitPointers
            ? BitConverter.ToUInt32(buffer, 0)
            : BitConverter.ToUInt64(buffer, 0);
    }

    private static byte[] ReadEnvironmentBlock(IntPtr processHandle, ulong environmentAddress)
    {
        var regionSize = QueryReadableRegion(processHandle, environmentAddress);
        var remainingBytes = regionSize - environmentAddress;
        var bytesToRead = checked((int)Math.Min(remainingBytes, MaxEnvironmentBytes));
        if (bytesToRead <= 0)
        {
            return [];
        }

        var buffer = new byte[bytesToRead];
        ReadExact(processHandle, environmentAddress, buffer);

        var terminatorLength = FindEnvironmentTerminatorLength(buffer);
        if (terminatorLength == 0)
        {
            throw new InvalidOperationException("Process environment block was not double-null terminated.");
        }

        return buffer[..terminatorLength];
    }

    private static ulong QueryReadableRegion(IntPtr processHandle, ulong address)
    {
        var infoLength = VirtualQueryEx(
            processHandle,
            new IntPtr(unchecked((long)address)),
            out var memoryInfo,
            (nuint)Marshal.SizeOf<MemoryBasicInformation>());
        if (infoLength == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "VirtualQueryEx failed for process environment.");
        }

        var regionStart = unchecked((ulong)(nuint)memoryInfo.BaseAddress);
        var regionSize = unchecked((ulong)memoryInfo.RegionSize);
        return regionStart + regionSize;
    }

    private static void ReadExact(IntPtr processHandle, ulong address, byte[] buffer)
    {
        if (!ReadProcessMemory(
                processHandle,
                new IntPtr(unchecked((long)address)),
                buffer,
                (nuint)buffer.Length,
                out var bytesRead))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "ReadProcessMemory failed.");
        }

        if ((ulong)bytesRead != (ulong)buffer.Length)
        {
            throw new EndOfStreamException("ReadProcessMemory returned a truncated result.");
        }
    }

    private static int FindEnvironmentTerminatorLength(byte[] buffer)
    {
        for (var index = 0; index <= buffer.Length - 4; index += 2)
        {
            if (buffer[index] == 0 &&
                buffer[index + 1] == 0 &&
                buffer[index + 2] == 0 &&
                buffer[index + 3] == 0)
            {
                return index + 4;
            }
        }

        return 0;
    }

    private static IReadOnlyDictionary<string, string> ParseEnvironmentBlock(byte[] buffer)
    {
        var text = Encoding.Unicode.GetString(buffer);
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in text.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = entry.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            variables[entry[..separator]] = entry[(separator + 1)..];
        }

        return variables;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(
        IntPtr processHandle,
        IntPtr baseAddress,
        [Out] byte[] buffer,
        nuint size,
        out nuint numberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process(IntPtr processHandle, [MarshalAs(UnmanagedType.Bool)] out bool wow64Process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process2(IntPtr processHandle, out ushort processMachine, out ushort nativeMachine);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint VirtualQueryEx(
        IntPtr processHandle,
        IntPtr address,
        out MemoryBasicInformation buffer,
        nuint length);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        out ProcessBasicInformationData processInformation,
        int processInformationLength,
        out int returnLength);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        out IntPtr processInformation,
        int processInformationLength,
        out int returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformationData
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr Reserved3;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }
}
