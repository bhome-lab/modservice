param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$executor = Join-Path $root "artifacts\native\NativeExecutor\x64\$Configuration\NativeExecutor.dll"
$sample = Join-Path $root "artifacts\native\SampleModule\x64\$Configuration\SampleModule.dll"
$testApp = Join-Path $root "artifacts\native\TestApp\x64\$Configuration\TestApp.exe"
$sampleOut = Join-Path $env:TEMP ("mm_sample_" + [guid]::NewGuid().ToString('N') + '.txt')
$persistOut = Join-Path $env:TEMP ("mm_persist_" + [guid]::NewGuid().ToString('N') + '.txt')

Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public enum NativeExecuteStatus : uint {
    Ok = 0,
    InvalidArgument = 1,
    TargetNotFound = 2,
    TargetChanged = 3,
    Timeout = 4,
    ExecutionFailed = 5
}

public sealed class NativeExecuteResult {
    public uint Status;
    public string ErrorText;
}

public static class NativeExecutorHarness {
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NativeExecuteStatus MmExecuteDelegate(
        ref MmExecuteRequest request,
        IntPtr errorBuffer,
        uint errorBufferCapacity,
        out uint errorBufferWritten);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr hModule);

    [StructLayout(LayoutKind.Sequential)]
    private struct MmUtf16View {
        public IntPtr Ptr;
        public uint Len;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MmEnvVar {
        public MmUtf16View Name;
        public MmUtf16View Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MmExecuteRequest {
        public uint pid;
        public ulong process_create_time_utc_100ns;
        public MmUtf16View exe_path;
        public IntPtr modules;
        public uint module_count;
        public IntPtr env;
        public uint env_count;
        public IntPtr options;
        public uint option_count;
        public uint timeout_ms;
    }

    public static NativeExecuteResult Execute(string dllPath, uint pid, ulong createTime, string exePath, string[] modules, string[] envNames, string[] envValues, uint timeoutMs) {
        var strings = new List<IntPtr>();
        GCHandle? modulesHandle = null;
        GCHandle? envHandle = null;
        IntPtr library = IntPtr.Zero;
        IntPtr errorBuffer = IntPtr.Zero;

        try {
            var exeView = CreateView(exePath, strings);
            var moduleViews = new MmUtf16View[modules.Length];
            for (int i = 0; i < modules.Length; ++i) {
                moduleViews[i] = CreateView(modules[i], strings);
            }

            var envEntries = new MmEnvVar[envNames.Length];
            for (int i = 0; i < envNames.Length; ++i) {
                envEntries[i] = new MmEnvVar {
                    Name = CreateView(envNames[i], strings),
                    Value = CreateView(envValues[i], strings)
                };
            }

            if (moduleViews.Length > 0) {
                modulesHandle = GCHandle.Alloc(moduleViews, GCHandleType.Pinned);
            }

            if (envEntries.Length > 0) {
                envHandle = GCHandle.Alloc(envEntries, GCHandleType.Pinned);
            }

            var request = new MmExecuteRequest {
                pid = pid,
                process_create_time_utc_100ns = createTime,
                exe_path = exeView,
                modules = modulesHandle.HasValue ? modulesHandle.Value.AddrOfPinnedObject() : IntPtr.Zero,
                module_count = (uint)moduleViews.Length,
                env = envHandle.HasValue ? envHandle.Value.AddrOfPinnedObject() : IntPtr.Zero,
                env_count = (uint)envEntries.Length,
                options = IntPtr.Zero,
                option_count = 0,
                timeout_ms = timeoutMs
            };

            library = LoadLibrary(dllPath);
            if (library == IntPtr.Zero) {
                throw new InvalidOperationException("LoadLibrary failed: " + Marshal.GetLastWin32Error());
            }

            var proc = GetProcAddress(library, "mm_execute");
            if (proc == IntPtr.Zero) {
                throw new InvalidOperationException("GetProcAddress failed: " + Marshal.GetLastWin32Error());
            }

            var execute = (MmExecuteDelegate)Marshal.GetDelegateForFunctionPointer(proc, typeof(MmExecuteDelegate));
            const uint errorCapacity = 1024;
            errorBuffer = Marshal.AllocHGlobal((int)(errorCapacity * 2));
            uint written;
            var status = execute(ref request, errorBuffer, errorCapacity, out written);
            var message = written > 0 ? Marshal.PtrToStringUni(errorBuffer, (int)written) : null;
            return new NativeExecuteResult { Status = (uint)status, ErrorText = message };
        }
        finally {
            if (modulesHandle.HasValue && modulesHandle.Value.IsAllocated) {
                modulesHandle.Value.Free();
            }

            if (envHandle.HasValue && envHandle.Value.IsAllocated) {
                envHandle.Value.Free();
            }

            foreach (var ptr in strings) {
                Marshal.FreeHGlobal(ptr);
            }

            if (errorBuffer != IntPtr.Zero) {
                Marshal.FreeHGlobal(errorBuffer);
            }

            if (library != IntPtr.Zero) {
                FreeLibrary(library);
            }
        }
    }

    private static MmUtf16View CreateView(string value, List<IntPtr> strings) {
        var ptr = Marshal.StringToHGlobalUni(value);
        strings.Add(ptr);
        return new MmUtf16View { Ptr = ptr, Len = (uint)value.Length };
    }
}
"@

$proc = Start-Process -FilePath $testApp -PassThru

try {
    Start-Sleep -Milliseconds 250
    $proc.Refresh()

    $result = [NativeExecutorHarness]::Execute(
        $executor,
        [uint32]$proc.Id,
        [uint64]$proc.StartTime.ToUniversalTime().ToFileTimeUtc(),
        $proc.MainModule.FileName,
        @($sample),
        @('MODSERVICE_SAMPLE_OUTPUT', 'MODSERVICE_SAMPLE_MARKER', 'MODSERVICE_TESTAPP_PERSIST_PATH'),
        @($sampleOut, 'manual-marker', $persistOut),
        5000)

    $exited = $proc.WaitForExit(15000)
    if (-not $exited) {
        throw 'TestApp did not exit in time.'
    }

    $sampleText = if (Test-Path $sampleOut) {
        [Text.Encoding]::Unicode.GetString([IO.File]::ReadAllBytes($sampleOut)).Replace([string][char]0, '').Trim()
    } else {
        ''
    }

    $persistText = if (Test-Path $persistOut) {
        [Text.Encoding]::Unicode.GetString([IO.File]::ReadAllBytes($persistOut)).Replace([string][char]0, '').Trim()
    } else {
        ''
    }

    [pscustomobject]@{
        Status = $result.Status
        Error = $result.ErrorText
        SampleOutput = $sampleText
        PersistedEnvironment = $persistText
        ExitCode = $proc.ExitCode
    }

    if ($result.Status -ne 0) {
        throw ('Executor failed: ' + $result.ErrorText)
    }

    if ($sampleText -ne 'manual-marker') {
        throw ('Unexpected sample output: ' + $sampleText)
    }

    if ($persistText -ne 'manual-marker') {
        throw ('Unexpected persisted env output: ' + $persistText)
    }
}
finally {
    if (-not $proc.HasExited) {
        Stop-Process -Id $proc.Id -Force
    }

    Remove-Item $sampleOut -ErrorAction SilentlyContinue
    Remove-Item $persistOut -ErrorAction SilentlyContinue
}
