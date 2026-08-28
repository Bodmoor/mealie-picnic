using Microsoft.Extensions.Logging.Abstractions;

namespace MealiePicnic.Tests;

public class TokenStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    private TokenStore New() =>
        new(TestFactory.Options(_dir), NullLogger<TokenStore>.Instance);

    [Fact]
    public void Starts_empty()
    {
        Assert.Null(New().Token());
    }

    [Fact]
    public void Persists_across_instances()
    {
        // The point of the store: 2FA should be rare, not once per restart.
        New().Save("token-abc");

        Assert.Equal("token-abc", New().Token());
    }

    [Fact]
    public void Clear_removes_the_file()
    {
        var store = New();
        store.Save("token-abc");

        store.Clear();

        Assert.Null(store.Token());
        Assert.Null(New().Token());
    }

    [Fact]
    public void Legacy_key_uses_the_original_flat_layout()
    {
        // No userKey (today's only mode) must keep writing exactly where a
        // pre-OIDC deployment expects the file, or upgrading loses the token.
        New().Save("token-abc");

        Assert.True(File.Exists(Path.Combine(_dir, "picnic-token")));
    }

    [Fact]
    public void Different_keys_are_isolated()
    {
        var store = New();
        store.Save("alice-token", "alice");
        store.Save("bob-token", "bob");

        Assert.Equal("alice-token", store.Token("alice"));
        Assert.Equal("bob-token", store.Token("bob"));
        Assert.Null(store.Token());
    }

    [Fact]
    public void Keyed_tokens_live_under_a_users_subdirectory()
    {
        New().Save("token-abc", "alice");

        Assert.True(File.Exists(Path.Combine(_dir, "users", "alice", "picnic-token")));
    }

    [Fact]
    public void Creates_the_data_directory()
    {
        New();

        Assert.True(Directory.Exists(_dir));
    }

    [Fact]
    public void Device_id_is_stable_across_instances()
    {
        // Picnic binds 2FA verification to x-picnic-did: losing it forces a new 2FA.
        var first = New().DeviceId();

        Assert.Equal(first, New().DeviceId());
        Assert.Matches("^[A-F0-9]{16}$", first);
    }

    [Fact]
    public void Device_ids_differ_between_installs()
    {
        var other = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var a = New().DeviceId();
            var b = new TokenStore(TestFactory.Options(other), NullLogger<TokenStore>.Instance).DeviceId();

            Assert.NotEqual(a, b);   // a constant in source would fail this
        }
        finally
        {
            if (Directory.Exists(other)) Directory.Delete(other, recursive: true);
        }
    }

    [Fact]
    public void Token_file_is_owner_only_on_unix()
    {
        if (OperatingSystem.IsWindows()) return;

        New().Save("token-abc");

        var mode = File.GetUnixFileMode(Path.Combine(_dir, "picnic-token"));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }

    [Fact]
    public void Truncated_token_file_is_treated_as_absent()
    {
        // A crash mid-write must not present an empty bearer token to Picnic.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "picnic-token"), "");

        Assert.Null(New().Token());
    }

    [Fact]
    public void Save_leaves_no_temp_file_behind()
    {
        New().Save("token-abc");

        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
