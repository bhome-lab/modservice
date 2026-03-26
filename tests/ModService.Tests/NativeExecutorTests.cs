using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ModService.Interop.Native;

namespace ModService.Tests;

public sealed class NativeExecutorTests
{
    [Fact]
    public void Execute_InjectsIntoRemoteProcess_AndForwardsEnvironment()
    {
        NativeBuild.EnsureBuilt();
        Assert.True(File.Exists(RepoPaths.NativeExecutorDll), $"Missing native executor at {RepoPaths.NativeExecutorDll}");
        Assert.True(File.Exists(RepoPaths.SampleModuleDll), $"Missing sample module at {RepoPaths.SampleModuleDll}");
        Assert.True(File.Exists(RepoPaths.TestAppExe), $"Missing test app at {RepoPaths.TestAppExe}");

        var tempFile = Path.GetTempFileName();
        File.Delete(tempFile);

        // Launch a real remote target process (TestApp.exe).
        using var targetProcess = Process.Start(new ProcessStartInfo
        {
            FileName = RepoPaths.TestAppExe,
            UseShellExecute = false,
            CreateNoWindow = true
        })!;

        try
        {
            // Give the process time to initialize.
            Thread.Sleep(500);
            Assert.False(targetProcess.HasExited, "Target process exited prematurely.");

            using var client = new NativeExecutorClient(RepoPaths.NativeExecutorDll);

            var result = client.Execute(new NativeExecuteRequest
            {
                ProcessId = (uint)targetProcess.Id,
                ProcessCreateTimeUtc100ns = (ulong)targetProcess.StartTime.ToUniversalTime().ToFileTimeUtc(),
                ExecutablePath = targetProcess.MainModule?.FileName ?? RepoPaths.TestAppExe,
                ModulePaths = [RepoPaths.DepModuleDll, RepoPaths.SampleModuleDll],
                EnvironmentVariables =
                [
                    new NativeEnvironmentVariable { Name = "MODSERVICE_SAMPLE_OUTPUT", Value = tempFile },
                    new NativeEnvironmentVariable { Name = "MODSERVICE_SAMPLE_MARKER", Value = "hello-from-test" }
                ],
                ExecutorOptions = [],
                TimeoutMs = 10000
            });

            Assert.True(result.IsSuccess, result.ErrorText);

            // Give the module DllMain time to write the output file.
            Thread.Sleep(500);

            Assert.False(targetProcess.HasExited, "Target process crashed after injection!");

            // SampleModule appends ":42" from DepModule dependency.
            Assert.True(File.Exists(tempFile), "Output file was not created by injected module.");
            Assert.Equal("hello-from-test:42", Encoding.Unicode.GetString(File.ReadAllBytes(tempFile)).Trim('\0', '\r', '\n'));
        }
        finally
        {
            if (!targetProcess.HasExited)
            {
                targetProcess.Kill();
                targetProcess.WaitForExit(3000);
            }

            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public void Execute_RejectsDifferentTargetProcess()
    {
        NativeBuild.EnsureBuilt();
        Assert.True(File.Exists(RepoPaths.NativeExecutorDll), $"Missing native executor at {RepoPaths.NativeExecutorDll}");
        Assert.True(File.Exists(RepoPaths.SampleModuleDll), $"Missing sample module at {RepoPaths.SampleModuleDll}");

        using var client = new NativeExecutorClient(RepoPaths.NativeExecutorDll);
        using var process = Process.GetCurrentProcess();

        var result = client.Execute(new NativeExecuteRequest
        {
            ProcessId = (uint)(process.Id + 9999),
            ProcessCreateTimeUtc100ns = (ulong)process.StartTime.ToUniversalTime().ToFileTimeUtc(),
            ExecutablePath = process.MainModule?.FileName ?? Environment.ProcessPath ?? "testhost",
            ModulePaths = [RepoPaths.SampleModuleDll],
            EnvironmentVariables = [],
            ExecutorOptions = [],
            TimeoutMs = 1000
        });

        Assert.Equal(NativeExecuteStatus.TargetNotFound, result.Status);
    }

    [Fact]
    public void Execute_RejectsEmptyExecutorOptionName()
    {
        NativeBuild.EnsureBuilt();
        Assert.True(File.Exists(RepoPaths.NativeExecutorDll), $"Missing native executor at {RepoPaths.NativeExecutorDll}");
        Assert.True(File.Exists(RepoPaths.SampleModuleDll), $"Missing sample module at {RepoPaths.SampleModuleDll}");

        using var client = new NativeExecutorClient(RepoPaths.NativeExecutorDll);
        using var process = Process.GetCurrentProcess();

        var result = client.Execute(new NativeExecuteRequest
        {
            ProcessId = (uint)process.Id,
            ProcessCreateTimeUtc100ns = (ulong)process.StartTime.ToUniversalTime().ToFileTimeUtc(),
            ExecutablePath = process.MainModule?.FileName ?? Environment.ProcessPath ?? "testhost",
            ModulePaths = [RepoPaths.SampleModuleDll],
            EnvironmentVariables = [],
            ExecutorOptions =
            [
                new NativeExecutorOption { Name = "", Value = "x" }
            ],
            TimeoutMs = 1000
        });

        Assert.Equal(NativeExecuteStatus.InvalidArgument, result.Status);
        Assert.Contains("option", result.ErrorText ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MmValidateOffsetsDelegate(
        IntPtr errorBuffer,
        uint errorBufferCapacity,
        out uint errorBufferWritten);

    [Fact]
    public void ValidateOffsets_ReturnsZero_WhenAllOffsetsDiscovered()
    {
        NativeBuild.EnsureBuilt();
        Assert.True(File.Exists(RepoPaths.NativeExecutorDll), $"Missing native executor at {RepoPaths.NativeExecutorDll}");

        var lib = NativeLibrary.Load(RepoPaths.NativeExecutorDll);
        try
        {
            var export = NativeLibrary.GetExport(lib, "mm_validate_offsets");
            var validate = Marshal.GetDelegateForFunctionPointer<MmValidateOffsetsDelegate>(export);

            var errorBuf = Marshal.AllocHGlobal(2048);
            try
            {
                var result = validate(errorBuf, 1024, out var written);
                var errorText = written > 0
                    ? Marshal.PtrToStringUni(errorBuf, (int)written)
                    : string.Empty;

                Assert.True(result == 0, $"mm_validate_offsets returned {result}: {errorText}");
            }
            finally
            {
                Marshal.FreeHGlobal(errorBuf);
            }
        }
        finally
        {
            NativeLibrary.Free(lib);
        }
    }
}
