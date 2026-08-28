using System.Security.Cryptography;
using System.Text;

namespace MealiePicnic.Storage;

/// <summary>
/// Maps a Mealie household to a filesystem-safe storage key for
/// <see cref="HouseholdLinkStore"/>, the same way <see cref="UserKey"/> maps a
/// signed-in principal to a <see cref="TokenStore"/> slot. Hashing the stable
/// household id (not its name or slug, either of which can be renamed) keeps
/// the key opaque and immune to a household rename.
/// </summary>
public static class HouseholdKey
{
    /// <summary>
    /// Fixed pseudo-household for the panic-login / OIDC-disabled path, where
    /// there is no Mealie identity to resolve a real household from.
    /// </summary>
    public const string Local = "local";

    public static string From(Guid householdId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(householdId.ToString()));
        return Convert.ToHexStringLower(hash)[..16];
    }
}
