using System.Runtime.InteropServices;

namespace ModService.Interop.Native;

public sealed class NativeExecutorClient : IDisposable
{
    private IntPtr _libraryHandle;
    private readonly MmExecuteDelegate _execute;
    private bool _disposed;

    public NativeExecutorClient(string libraryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryPath);

        _libraryHandle = NativeLibrary.Load(libraryPath);
        var export = NativeLibrary.GetExport(_libraryHandle, "mm_execute");
        _execute = Marshal.GetDelegateForFunctionPointer<MmExecuteDelegate>(export);
    }

    public NativeExecuteResult Execute(NativeExecuteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var allocatedStrings = new List<IntPtr>();
        GCHandle? modulesHandle = null;
        GCHandle? envHandle = null;
        IntPtr errorBuffer = IntPtr.Zero;

        try
        {
            var exePath = CreateView(request.ExecutablePath, allocatedStrings);
            var moduleViews = request.ModulePaths.Select(path => CreateView(path, allocatedStrings)).ToArray();
            var envEntries = request.EnvironmentVariables
                .Select(item => new MmEnvVar
                {
                    Name = CreateView(item.Name, allocatedStrings),
                    Value = CreateView(item.Value, allocatedStrings)
                })
                .ToArray();

            modulesHandle = moduleViews.Length > 0
                ? GCHandle.Alloc(moduleViews, GCHandleType.Pinned)
                : null;
            envHandle = envEntries.Length > 0
                ? GCHandle.Alloc(envEntries, GCHandleType.Pinned)
                : null;

            var nativeRequest = new MmExecuteRequest
            {
                ProcessId = request.ProcessId,
                ProcessCreateTimeUtc100ns = request.ProcessCreateTimeUtc100ns,
                ExePath = exePath,
                Modules = modulesHandle?.AddrOfPinnedObject() ?? IntPtr.Zero,
                ModuleCount = (uint)moduleViews.Length,
                Environment = envHandle?.AddrOfPinnedObject() ?? IntPtr.Zero,
                EnvironmentCount = (uint)envEntries.Length,
                TimeoutMs = request.TimeoutMs
            };

            const uint errorCapacity = 1024;
            errorBuffer = Marshal.AllocHGlobal((int)(errorCapacity * sizeof(char)));
            var status = _execute(nativeRequest, errorBuffer, errorCapacity, out var written);
            var message = written > 0 ? Marshal.PtrToStringUni(errorBuffer, (int)written) : null;

            return new NativeExecuteResult(status, message);
        }
        finally
        {
            if (modulesHandle is { IsAllocated: true })
            {
                modulesHandle.Value.Free();
            }

            if (envHandle is { IsAllocated: true })
            {
                envHandle.Value.Free();
            }

            foreach (var pointer in allocatedStrings)
            {
                Marshal.FreeHGlobal(pointer);
            }

            if (errorBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(errorBuffer);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_libraryHandle != IntPtr.Zero)
        {
            NativeLibrary.Free(_libraryHandle);
            _libraryHandle = IntPtr.Zero;
        }
    }

    private static MmUtf16View CreateView(string value, ICollection<IntPtr> allocatedStrings)
    {
        ArgumentNullException.ThrowIfNull(value);

        var pointer = Marshal.StringToHGlobalUni(value);
        allocatedStrings.Add(pointer);
        return new MmUtf16View
        {
            Ptr = pointer,
            Length = (uint)value.Length
        };
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NativeExecuteStatus MmExecuteDelegate(
        MmExecuteRequest request,
        IntPtr errorBuffer,
        uint errorBufferCapacity,
        out uint errorBufferWritten);

    [StructLayout(LayoutKind.Sequential)]
    private struct MmUtf16View
    {
        public IntPtr Ptr;

        public uint Length;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MmEnvVar
    {
        public MmUtf16View Name;

        public MmUtf16View Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MmExecuteRequest
    {
        public uint ProcessId;

        public ulong ProcessCreateTimeUtc100ns;

        public MmUtf16View ExePath;

        public IntPtr Modules;

        public uint ModuleCount;

        public IntPtr Environment;

        public uint EnvironmentCount;

        public uint TimeoutMs;
    }
}
