using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Runtime.Versioning;

namespace ModService.GitHub.Auth;

[SupportedOSPlatform("windows")]
public sealed class GitHubTokenStore
{
    private readonly string _filePath;

    public GitHubTokenStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
    }

    public string FilePath => _filePath;

    public bool HasToken() => File.Exists(_filePath);

    public string? TryLoadToken()
    {
        if (!File.Exists(_filePath))
        {
            return null;
        }

        var protectedBytes = File.ReadAllBytes(_filePath);
        var rawBytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.LocalMachine);
        return Encoding.UTF8.GetString(rawBytes);
    }

    public async Task SaveAsync(string token, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        var directory = Path.GetDirectoryName(_filePath) ?? throw new InvalidOperationException("Secret store path must have a parent directory.");
        Directory.CreateDirectory(directory);
        ApplyDirectoryAcl(directory);

        var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), optionalEntropy: null, DataProtectionScope.LocalMachine);
        var tempPath = _filePath + ".tmp";
        await File.WriteAllBytesAsync(tempPath, protectedBytes, cancellationToken);
        File.Move(tempPath, _filePath, overwrite: true);
        ApplyFileAcl(_filePath);
    }

    public void Clear()
    {
        if (File.Exists(_filePath))
        {
            File.Delete(_filePath);
        }
    }

    private static void ApplyDirectoryAcl(string directory)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(CreateDirectoryRule(WellKnownSidType.LocalSystemSid));
        security.AddAccessRule(CreateDirectoryRule(WellKnownSidType.BuiltinAdministratorsSid));
        TryAddCurrentUserDirectoryRule(security);
        new DirectoryInfo(directory).SetAccessControl(security);
    }

    private static void ApplyFileAcl(string filePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(CreateFileRule(WellKnownSidType.LocalSystemSid));
        security.AddAccessRule(CreateFileRule(WellKnownSidType.BuiltinAdministratorsSid));
        TryAddCurrentUserFileRule(security);
        new FileInfo(filePath).SetAccessControl(security);
    }

    private static FileSystemAccessRule CreateDirectoryRule(WellKnownSidType sidType)
    {
        var sid = new SecurityIdentifier(sidType, null);
        return new FileSystemAccessRule(
            sid,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow);
    }

    private static FileSystemAccessRule CreateFileRule(WellKnownSidType sidType)
    {
        var sid = new SecurityIdentifier(sidType, null);
        return new FileSystemAccessRule(
            sid,
            FileSystemRights.FullControl,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow);
    }

    private static void TryAddCurrentUserDirectoryRule(DirectorySecurity security)
    {
        var sid = WindowsIdentity.GetCurrent().User;
        if (sid is null)
        {
            return;
        }

        security.AddAccessRule(new FileSystemAccessRule(
            sid,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
    }

    private static void TryAddCurrentUserFileRule(FileSecurity security)
    {
        var sid = WindowsIdentity.GetCurrent().User;
        if (sid is null)
        {
            return;
        }

        security.AddAccessRule(new FileSystemAccessRule(
            sid,
            FileSystemRights.FullControl,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));
    }
}
