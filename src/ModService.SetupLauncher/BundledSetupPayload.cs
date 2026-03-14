using System.Text;

namespace ModService.SetupLauncher;

public static class BundledSetupPayload
{
    public const string TrailerMagic = "MSSTP001";
    private const int TrailerSize = sizeof(long) + 8;

    public static bool TryReadPayloadInfo(string bundlePath, out BundledSetupPayloadInfo payloadInfo)
    {
        using var stream = File.OpenRead(bundlePath);
        return TryReadPayloadInfo(stream, out payloadInfo);
    }

    public static bool TryReadPayloadInfo(Stream stream, out BundledSetupPayloadInfo payloadInfo)
    {
        payloadInfo = default;
        if (!stream.CanSeek || stream.Length < TrailerSize)
        {
            return false;
        }

        stream.Seek(-TrailerSize, SeekOrigin.End);
        Span<byte> trailer = stackalloc byte[TrailerSize];
        var bytesRead = stream.Read(trailer);
        if (bytesRead != trailer.Length)
        {
            return false;
        }

        var setupLength = BitConverter.ToInt64(trailer[..sizeof(long)]);
        var magic = Encoding.ASCII.GetString(trailer[sizeof(long)..]);
        if (!string.Equals(magic, TrailerMagic, StringComparison.Ordinal))
        {
            return false;
        }

        if (setupLength <= 0 || setupLength > stream.Length - TrailerSize)
        {
            return false;
        }

        payloadInfo = new BundledSetupPayloadInfo(
            stream.Length - TrailerSize - setupLength,
            setupLength);
        return true;
    }

    public static ExtractedSetupPayload ExtractToTempFile(string bundlePath)
    {
        if (!TryReadPayloadInfo(bundlePath, out var payloadInfo))
        {
            throw new InvalidOperationException("The bundled setup payload could not be found.");
        }

        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "ModServiceSetup",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        var extractedPath = Path.Combine(tempDirectory, "VelopackSetup.exe");
        using var source = File.OpenRead(bundlePath);
        using var destination = File.Create(extractedPath);
        source.Seek(payloadInfo.Offset, SeekOrigin.Begin);
        CopyRange(source, destination, payloadInfo.Length);

        return new ExtractedSetupPayload(extractedPath, tempDirectory, deleteOnDispose: true);
    }

    private static void CopyRange(Stream source, Stream destination, long length)
    {
        var buffer = new byte[1024 * 1024];
        var remaining = length;
        while (remaining > 0)
        {
            var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0)
            {
                throw new EndOfStreamException("Unexpected end of file while extracting the bundled setup payload.");
            }

            destination.Write(buffer, 0, read);
            remaining -= read;
        }
    }
}

public readonly record struct BundledSetupPayloadInfo(long Offset, long Length);

public sealed class ExtractedSetupPayload(string executablePath, string directoryPath, bool deleteOnDispose) : IDisposable
{
    public string ExecutablePath { get; } = executablePath;

    public string DirectoryPath { get; } = directoryPath;

    public void Dispose()
    {
        if (!deleteOnDispose)
        {
            return;
        }

        try
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
        catch
        {
        }
    }
}
