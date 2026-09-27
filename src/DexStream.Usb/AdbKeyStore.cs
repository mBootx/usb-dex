using DexStream.Core.Adb;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DexStream.Usb;

/// <summary>
/// Supplies the RSA key that identifies this host to adbd, reusing the user's existing ADB key when
/// one is present.
/// </summary>
/// <remarks>
/// <para>
/// Reusing <c>%USERPROFILE%\.android\adbkey</c> matters for usability: a device that has already
/// authorized that key will connect without showing the "Allow USB debugging?" prompt again. Falling
/// back to a DexStream-specific key means the app still works for a user who has never installed the
/// platform tools, at the cost of one authorization prompt.
/// </para>
/// </remarks>
public sealed class AdbKeyStore
{
    private readonly ILogger _logger;

    public AdbKeyStore(ILogger<AdbKeyStore>? logger = null)
        => _logger = logger ?? NullLogger<AdbKeyStore>.Instance;

    /// <summary>The platform-tools key location, shared with <c>adb.exe</c>.</summary>
    public static string SystemKeyPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".android",
        "adbkey");

    /// <summary>DexStream's own key, used when the platform-tools key is absent or unreadable.</summary>
    public static string PrivateKeyPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DexStream",
        "adbkey");

    /// <summary>Where the key came from, so the UI can explain whether a prompt is expected.</summary>
    public enum KeyOrigin
    {
        /// <summary>Loaded from the shared platform-tools key; the device may already trust it.</summary>
        SystemAdbKey,

        /// <summary>Loaded from DexStream's own previously generated key.</summary>
        DexStreamKey,

        /// <summary>Freshly generated; the device will show the authorization prompt.</summary>
        NewlyGenerated,
    }

    /// <summary>
    /// Loads or creates the host key.
    /// </summary>
    /// <param name="reuseSystemKey">
    /// When true, try <see cref="SystemKeyPath"/> first so an already-authorized key is reused.
    /// </param>
    public (AdbKeyPair Key, KeyOrigin Origin) Load(bool reuseSystemKey = true)
    {
        string identity = AdbKeyPair.BuildIdentity(Environment.UserName, Environment.MachineName);

        if (reuseSystemKey && TryLoad(SystemKeyPath, identity, out AdbKeyPair systemKey))
        {
            _logger.LogInformation(
                "Reusing the existing ADB key from {Path}; the device may not need to re-authorize.",
                SystemKeyPath);
            return (systemKey, KeyOrigin.SystemAdbKey);
        }

        if (TryLoad(PrivateKeyPath, identity, out AdbKeyPair ownKey))
        {
            return (ownKey, KeyOrigin.DexStreamKey);
        }

        _logger.LogInformation("Generating a new ADB key at {Path}.", PrivateKeyPath);
        AdbKeyPair generated = AdbKeyPair.Generate(identity);
        TrySave(generated);
        return (generated, KeyOrigin.NewlyGenerated);
    }

    private bool TryLoad(string path, string identity, out AdbKeyPair key)
    {
        key = null!;

        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            key = AdbKeyPair.FromPem(File.ReadAllText(path), identity);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AdbProtocolException)
        {
            _logger.LogDebug(ex, "Could not load an ADB key from {Path}.", path);
            return false;
        }
    }

    private void TrySave(AdbKeyPair key)
    {
        try
        {
            string? directory = Path.GetDirectoryName(PrivateKeyPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(PrivateKeyPath, key.ToPrivateKeyPem());
            File.WriteAllText(PrivateKeyPath + ".pub", key.ToPublicKeyFile());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A key that cannot be persisted still works for this session; the user will simply be
            // asked to authorize again next time.
            _logger.LogWarning(
                ex,
                "Could not save the ADB key to {Path}; the device will ask for authorization again " +
                "on the next run.",
                PrivateKeyPath);
        }
    }
}
