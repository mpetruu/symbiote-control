using System.Diagnostics;
using System.Security.Cryptography;

namespace SymbioteControl;

internal static class GameFingerprint
{
    public const string ExpectedSha256 = "c9b912eff96facb746a705ac8b0b83c29faa7bbe53d984a0c5eb45efd0b738c9";
    public const string ExpectedVersion = "2.0810.0.0";

    public static void Validate(string executablePath)
    {
        if (!string.Equals(Path.GetFileName(executablePath), "Spider-Man2.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected process is not Spider-Man2.exe.");
        var version = FileVersionInfo.GetVersionInfo(executablePath).FileVersion;
        if (version != ExpectedVersion)
            throw new InvalidOperationException($"Unsupported game version {version ?? "unknown"}. This build supports {ExpectedVersion}.");
        using var file = new FileStream(executablePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var sha256 = Convert.ToHexString(SHA256.HashData(file));
        if (!sha256.Equals(ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unsupported executable fingerprint. The game may have updated or been modified.");
    }
}
