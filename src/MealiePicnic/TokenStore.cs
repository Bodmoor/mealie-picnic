namespace MealiePicnic;

/// <summary>
/// Persists the Picnic auth token on the mounted volume so 2FA is a rare chore
/// rather than a per-restart one. The token is a bearer credential for a real
/// account, so the file is written 0600 where the platform allows it.
/// </summary>
public sealed class TokenStore
{
    private readonly string _tokenPath;
    private readonly string _devicePath;
    private readonly ILogger<TokenStore> _log;
    private readonly object _lock = new();
    private string? _token;
    private string? _deviceId;

    public TokenStore(AppOptions options, ILogger<TokenStore> log)
    {
        _log = log;
        Directory.CreateDirectory(options.DataDir);
        _tokenPath = Path.Combine(options.DataDir, "picnic-token");
        _devicePath = Path.Combine(options.DataDir, "picnic-device");

        if (File.Exists(_tokenPath))
        {
            _token = File.ReadAllText(_tokenPath).Trim();
            if (_token.Length == 0)
            {
                // A crash mid-write can leave a truncated file; treat it as absent
                // rather than presenting an empty bearer token to Picnic.
                _token = null;
            }
            else
            {
                _log.LogInformation("Loaded cached Picnic token from {Path}", _tokenPath);
            }
        }
    }

    public string? Token
    {
        get { lock (_lock) return _token; }
    }

    /// <summary>
    /// Per-install device id sent as x-picnic-did. Picnic binds 2FA verification to
    /// it, so it must be stable across restarts -- but it must NOT be a constant in
    /// source, or every deployment of this app presents the same device fingerprint.
    /// Generated once, persisted next to the token; clearing it forces a new 2FA.
    /// </summary>
    public string DeviceId
    {
        get
        {
            lock (_lock)
            {
                if (_deviceId is not null)
                    return _deviceId;

                if (File.Exists(_devicePath))
                {
                    var existing = File.ReadAllText(_devicePath).Trim();
                    if (existing.Length == 16)
                        return _deviceId = existing;
                }

                _deviceId = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8));
                WriteAtomic(_devicePath, _deviceId);
                _log.LogInformation("Generated new Picnic device id");
                return _deviceId;
            }
        }
    }

    public void Save(string token)
    {
        lock (_lock)
        {
            _token = token;
            try
            {
                WriteAtomic(_tokenPath, token);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Could not persist Picnic token to {Path}", _tokenPath);
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _token = null;
            try { if (File.Exists(_tokenPath)) File.Delete(_tokenPath); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Write via temp file + rename. Two reasons: the mode is narrowed to 0600
    /// BEFORE the secret is written (File.WriteAllText creates with the umask,
    /// briefly world-readable), and a crash mid-write can never leave a truncated
    /// file at the final path.
    /// </summary>
    private static void WriteAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write))
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var bytes = System.Text.Encoding.UTF8.GetBytes(content);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }
}
