using System.Security.Claims;

namespace MealiePicnic.Storage;

/// <summary>
/// Reads the household key stashed on the auth cookie at sign-in (see the OIDC
/// OnTicketReceived handler and HandlePasswordLoginAsync in Program.cs).
/// Resolution happens once, at login, not per request. Absent entirely means
/// resolution never succeeded -- e.g. the signed-in Mealie account is not
/// linked to any household -- and callers must treat that as an explicit error
/// state (issue #17), never silently fall back to a shared one.
/// </summary>
public static class HouseholdContext
{
    public const string ClaimType = "household_key";

    public static string? KeyOf(ClaimsPrincipal user) => user.FindFirstValue(ClaimType);
}
