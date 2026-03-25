using System.Diagnostics;
using System.Text;
using ModService.Interop.Native;

namespace ModService.Tests;

public sealed class NativeExecutorTests
{
    [Fact]
    public void Execute_LoadsLocalSampleModule_AndForwardsEnvironment()
    {
        NativeBuild.EnsureBuilt();
        Assert.True(File.Exists(RepoPaths.NativeExecutorDll), $"Missing native executor at {RepoPaths.NativeExecutorDll}");
        Assert.True(File.Exists(RepoPaths.SampleModuleDll), $"Missing sample module at {RepoPaths.SampleModuleDll}");

        var tempFile = Path.GetTempFileName();
        File.Delete(tempFile);

        try
        {
            using var client = new NativeExecutorClient(RepoPaths.NativeExecutorDll);
            using var process = Process.GetCurrentProcess();

            var result = client.Execute(new NativeExecuteRequest
            {
                ProcessId = (uint)process.Id,
                ProcessCreateTimeUtc100ns = (ulong)process.StartTime.ToUniversalTime().ToFileTimeUtc(),
                ExecutablePath = process.MainModule?.FileName ?? Environment.ProcessPath ?? "testhost",
                ModulePaths = [RepoPaths.DepModuleDll, RepoPaths.SampleModuleDll],
                EnvironmentVariables =
                [
                    new NativeEnvironmentVariable { Name = "MODSERVICE_SAMPLE_OUTPUT", Value = tempFile },
                    new NativeEnvironmentVariable { Name = "MODSERVICE_SAMPLE_MARKER", Value = "hello-from-test" }
                ],
                ExecutorOptions =
                [
                    new NativeExecutorOption { Name = "mode", Value = "safe-smoke" }
                ],
                TimeoutMs = 5000
            });

            Assert.True(result.IsSuccess, result.ErrorText);
            // SampleModule now appends ":42" from DepModule dependency.
            Assert.Equal("hello-from-test:42", Encoding.Unicode.GetString(File.ReadAllBytes(tempFile)).Trim('\0', '\r', '\n'));
        }
        finally
        {
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
}
