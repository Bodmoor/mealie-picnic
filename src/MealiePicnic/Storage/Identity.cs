using System.Security.Claims;

namespace MealiePicnic.Storage;

/// <summary>
/// Naming the signed-in caller (issue #72).
///
/// <see cref="ClaimsIdentity.Name"/> is not usable here. It reads one specific
/// claim type, and Authentik issues <c>preferred_username</c>, <c>name</c> and
/// <c>email</c> — none of which is that one. Reading it made every OIDC request
/// look unauthenticated: the request log said <c>user:anonymous</c> above a 200
/// from an endpoint that requires a session.
///
/// The local password identity does carry the claim, which is exactly why the
/// test covering the log scope passed while production was wrong.
/// </summary>
public static class Identity
{
    /// <summary>
    /// A human label for the account, or null when there is nothing personal to
    /// show. The shared password identity is deliberately unlabelled: it is one
    /// "owner" account rather than a person, so /api/me shows no name beside the
    /// sign-out button.
    /// </summary>
    public static string? LabelOf(ClaimsPrincipal user)
    {
        var email = user.FindFirstValue("email") ?? user.FindFirstValue(ClaimTypes.Email);
        if (email is null) return null;

        return user.FindFirstValue("name")
            ?? user.FindFirstValue(ClaimTypes.Name)
            ?? email;
    }

    /// <summary>
    /// What the log calls this caller. Unlike <see cref="LabelOf"/> it always says
    /// something, and it distinguishes the three states that matter when reading a
    /// log: nobody signed in, somebody signed in whose name we know, and somebody
    /// signed in whose name we do not.
    /// </summary>
    public static string ForLog(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true) return "anonymous";

        return LabelOf(user)
            ?? user.Identity.Name
            ?? user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? "signed-in";
    }
}
