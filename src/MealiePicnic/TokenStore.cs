namespace MealiePicnic;

/// <summary>
/// Persists the Picnic auth token on the mounted volume so 2FA is a rare chore
/// rather than a per-restart one. The token is a bearer credential for a real
/// account, so the file is written 0600 where the platform allows it.
///
/// Storage is keyed by an optional per-user id. A null key uses the original
/// flat DATA_DIR/picnic-token layout unchanged, so single-password deployments
/// that never configure OIDC see no behaviour change at all. A real key (the
/// hashed OIDC `sub`, or the fixed panic-login subject -- see UserKey) stores
/// under DATA_DIR/users/{key}/ instead, so every signed-in identity gets its
/// own Picnic session once OIDC is enabled.
/// </summary>
public sealed class TokenStore
{
    private readonly string _dataDir;
    private readonly ILogger<TokenStore> _log;
    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _entries = new();

    public TokenStore(AppOptions options, ILogger<TokenStore> log)
    {
        _log = log;
        _dataDir = options.DataDir;
        Directory.CreateDirectory(_dataDir);
    }

    public string? Token(string? userKey = null)
    {
        lock (_lock) return Get(userKey).Token;
    }

    /// <summary>
    /// Per-install (and, once OIDC is enabled, per-user) device id sent as
    /// x-picnic-did. Picnic binds 2FA verification to it, so it must be stable
    /// across restarts -- but it must NOT be a constant in source, or every
    /// deployment of this app presents the same device fingerprint. Generated
    /// once, persisted next to the token; clearing it forces a new 2FA.
    /// </summary>
    public string DeviceId(string? userKey = null)
    {
        lock (_lock)
        {
            var entry = Get(userKey);
            if (entry.DeviceId is not null)
                return entry.DeviceId;

            if (File.Exists(entry.DevicePath))
            {
                var existing = File.ReadAllText(entry.DevicePath).Trim();
                if (existing.Length == 16)
                {
                    entry.DeviceId = existing;
                    return existing;
                }
            }

            entry.DeviceId = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8));
            WriteAtomic(entry.DevicePath, entry.DeviceId);
            _log.LogInformation("Generated new Picnic device id for {Key}", userKey ?? "(default)");
            return entry.DeviceId;
        }
    }

    public void Save(string token, string? userKey = null)
    {
        lock (_lock)
        {
            var entry = Get(userKey);
            entry.Token = token;
            try
            {
                WriteAtomic(entry.TokenPath, token);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Could not persist Picnic token to {Path}", entry.TokenPath);
            }
        }
    }

    public void Clear(string? userKey = null)
    {
        lock (_lock)
        {
            var entry = Get(userKey);
            entry.Token = null;
            try { if (File.Exists(entry.TokenPath)) File.Delete(entry.TokenPath); } catch { /* best effort */ }
        }
    }

    private Entry Get(string? userKey)
    {
        var key = userKey ?? "";
        if (_entries.TryGetValue(key, out var cached))
            return cached;

        var dir = key.Length == 0 ? _dataDir : Path.Combine(_dataDir, "users", key);
        Directory.CreateDirectory(dir);

        var entry = new Entry(Path.Combine(dir, "picnic-token"), Path.Combine(dir, "picnic-device"));
        if (File.Exists(entry.TokenPath))
        {
            var token = File.ReadAllText(entry.TokenPath).Trim();
            // A crash mid-write can leave a truncated file; treat it as absent
            // rather than presenting an empty bearer token to Picnic.
            entry.Token = token.Length == 0 ? null : token;
            if (entry.Token is not null)
                _log.LogInformation("Loaded cached Picnic token from {Path}", entry.TokenPath);
        }

        _entries[key] = entry;
        return entry;
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

    private sealed class Entry(string tokenPath, string devicePath)
    {
        public string TokenPath { get; } = tokenPath;
        public string DevicePath { get; } = devicePath;
        public string? Token { get; set; }
        public string? DeviceId { get; set; }
    }
}
