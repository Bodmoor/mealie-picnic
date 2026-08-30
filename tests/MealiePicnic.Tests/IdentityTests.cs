using System.Security.Claims;
using MealiePicnic.Storage;

namespace MealiePicnic.Tests;

/// <summary>
/// Naming the signed-in caller (issue #72). The deployed log said
/// <c>user:anonymous</c> above a 200 from an endpoint that requires a session,
/// because the scope read ClaimsIdentity.Name — a claim Authentik does not issue.
/// </summary>
public class IdentityTests
{
    private static ClaimsPrincipal SignedIn(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity([.. claims.Select(c => new Claim(c.Type, c.Value))], "TestAuth"));

    [Fact]
    public void An_oidc_identity_without_a_name_claim_is_still_named_in_the_log()
    {
        // The exact shape that produced "user:anonymous" in production: authenticated,
        // with an email, and no ClaimTypes.Name anywhere.
        var user = SignedIn(("email", "paul@example.com"), ("preferred_username", "paul"));

        Assert.Equal("paul@example.com", Identity.ForLog(user));
    }

    [Fact]
    public void A_name_claim_is_preferred_over_the_email()
    {
        var user = SignedIn(("email", "paul@example.com"), ("name", "Paul"));

        Assert.Equal("Paul", Identity.ForLog(user));
        Assert.Equal("Paul", Identity.LabelOf(user));
    }

    [Fact]
    public void The_shared_password_identity_is_named_but_not_labelled()
    {
        // One "owner" account rather than a person: worth naming in a log, not
        // worth showing beside the sign-out button.
        var user = SignedIn((ClaimTypes.Name, "owner"), (ClaimTypes.NameIdentifier, "local-admin"));

        Assert.Equal("owner", Identity.ForLog(user));
        Assert.Null(Identity.LabelOf(user));
    }

    [Fact]
    public void An_authenticated_caller_with_nothing_to_go_on_is_not_called_anonymous()
    {
        // "Signed in, and we cannot say who" is a different fact from "nobody is
        // signed in", and a log that blurs them is why this class exists.
        var user = SignedIn((ClaimTypes.NameIdentifier, "sub-12345"));

        Assert.Equal("sub-12345", Identity.ForLog(user));
    }

    [Fact]
    public void Only_a_genuinely_unauthenticated_caller_is_anonymous()
    {
        // No authentication type means no authentication -- claims alone do not
        // make a principal signed in.
        var nobody = new ClaimsPrincipal(new ClaimsIdentity());

        Assert.Equal("anonymous", Identity.ForLog(nobody));
        Assert.Null(Identity.LabelOf(nobody));
    }
}
