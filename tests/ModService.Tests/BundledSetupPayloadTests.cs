using ModService.SetupLauncher;

namespace ModService.Tests;

public sealed class BundledSetupPayloadTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        "ModService.Tests",
        nameof(BundledSetupPayloadTests),
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void TryReadPayloadInfo_ReturnsFalse_WhenTrailerIsMissing()
    {
        Directory.CreateDirectory(_tempDirectory);
        var bundlePath = Path.Combine(_tempDirectory, "bundle.exe");
        File.WriteAllBytes(bundlePath, [1, 2, 3, 4]);

        var found = BundledSetupPayload.TryReadPayloadInfo(bundlePath, out var info);

        Assert.False(found);
        Assert.Equal(default, info);
    }

    [Fact]
    public void TryReadPayloadInfo_AndExtractToTempFile_ReadBundledPayload()
    {
        Directory.CreateDirectory(_tempDirectory);
        var bundlePath = Path.Combine(_tempDirectory, "bundle.exe");
        var launcherBytes = new byte[] { 1, 2, 3, 4, 5 };
        var payloadBytes = new byte[] { 9, 8, 7, 6 };

        using (var stream = File.Create(bundlePath))
        {
            stream.Write(launcherBytes);
            stream.Write(payloadBytes);
            stream.Write(BitConverter.GetBytes((long)payloadBytes.Length));
            stream.Write(System.Text.Encoding.ASCII.GetBytes(BundledSetupPayload.TrailerMagic));
        }

        var found = BundledSetupPayload.TryReadPayloadInfo(bundlePath, out var info);

        Assert.True(found);
        Assert.Equal(launcherBytes.Length, info.Offset);
        Assert.Equal(payloadBytes.Length, info.Length);

        using var extracted = BundledSetupPayload.ExtractToTempFile(bundlePath);
        Assert.Equal(payloadBytes, File.ReadAllBytes(extracted.ExecutablePath));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch
        {
        }
    }
}
