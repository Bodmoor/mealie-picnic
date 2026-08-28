using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace MealiePicnic;

/// <summary>
/// Maps a signed-in principal to a filesystem-safe per-user storage key, used to
/// give every identity its own <see cref="TokenStore"/> slot once OIDC is enabled.
/// The panic-login identity (see /login/admin in Program.cs) carries this same
/// fixed subject, so it lands in its own slot alongside real OIDC users instead
/// of needing special-cased storage.
/// </summary>
public static class UserKey
{
    public const string LocalSubject = "local-admin";

    public static string From(ClaimsPrincipal user)
    {
        var subject = user.FindFirstValue("sub")
            ?? user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? LocalSubject;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(subject));
        return Convert.ToHexStringLower(hash)[..16];
    }
}
