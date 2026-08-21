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
        Assert.Null(New().Token);
    }

    [Fact]
    public void Persists_across_instances()
    {
        // The point of the store: 2FA should be rare, not once per restart.
        New().Save("token-abc");

        Assert.Equal("token-abc", New().Token);
    }

    [Fact]
    public void Clear_removes_the_file()
    {
        var store = New();
        store.Save("token-abc");

        store.Clear();

        Assert.Null(store.Token);
        Assert.Null(New().Token);
    }

    [Fact]
    public void Creates_the_data_directory()
    {
        New();

        Assert.True(Directory.Exists(_dir));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
