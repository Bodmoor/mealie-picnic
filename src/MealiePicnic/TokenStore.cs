namespace MealiePicnic;

/// <summary>
/// Persists the Picnic auth token on the mounted volume so 2FA is a rare chore
/// rather than a per-restart one. The token is a bearer credential for a real
/// account, so the file is written 0600 where the platform allows it.
/// </summary>
public sealed class TokenStore
{
    private readonly string _path;
    private readonly ILogger<TokenStore> _log;
    private readonly object _lock = new();
    private string? _token;

    public TokenStore(AppOptions options, ILogger<TokenStore> log)
    {
        _log = log;
        Directory.CreateDirectory(options.DataDir);
        _path = Path.Combine(options.DataDir, "picnic-token");

        if (File.Exists(_path))
        {
            _token = File.ReadAllText(_path).Trim();
            _log.LogInformation("Loaded cached Picnic token from {Path}", _path);
        }
    }

    public string? Token
    {
        get { lock (_lock) return _token; }
    }

    public void Save(string token)
    {
        lock (_lock)
        {
            _token = token;
            try
            {
                File.WriteAllText(_path, token);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Could not persist Picnic token to {Path}", _path);
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _token = null;
            try { if (File.Exists(_path)) File.Delete(_path); } catch { /* best effort */ }
        }
    }
}
