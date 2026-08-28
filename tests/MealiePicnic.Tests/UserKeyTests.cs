using MealiePicnic.Storage;
using System.Security.Claims;

namespace MealiePicnic.Tests;

public class UserKeyTests
{
    private static ClaimsPrincipal Principal(string type, string value) =>
        new(new ClaimsIdentity([new Claim(type, value)]));

    [Fact]
    public void Same_subject_produces_the_same_key()
    {
        var a = UserKey.From(Principal("sub", "user-1"));
        var b = UserKey.From(Principal("sub", "user-1"));

        Assert.Equal(a, b);
    }

    [Fact]
    public void Different_subjects_produce_different_keys()
    {
        var a = UserKey.From(Principal("sub", "user-1"));
        var b = UserKey.From(Principal("sub", "user-2"));

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Key_is_filesystem_safe()
    {
        var key = UserKey.From(Principal("sub", "user-1"));

        Assert.Matches("^[a-f0-9]{16}$", key);
    }

    [Fact]
    public void Missing_subject_falls_back_to_the_local_constant()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        var explicitLocal = Principal(ClaimTypes.NameIdentifier, UserKey.LocalSubject);

        Assert.Equal(UserKey.From(explicitLocal), UserKey.From(anonymous));
    }
}
