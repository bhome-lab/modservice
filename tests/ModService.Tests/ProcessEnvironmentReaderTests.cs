using System.Diagnostics;
using ModService.Host;

namespace ModService.Tests;

public sealed class ProcessEnvironmentReaderTests
{
    [Fact]
    public void Read_ReturnsCurrentProcessEnvironment()
    {
        var variableName = $"MODSERVICE_TEST_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(variableName, "expected");

        try
        {
            using var process = Process.GetCurrentProcess();
            var environment = ProcessEnvironmentReader.Read(process);
            Assert.Equal("expected", environment[variableName]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, null);
        }
    }
}
