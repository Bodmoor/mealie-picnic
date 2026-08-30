using System.Text.RegularExpressions;
using MealiePicnic.Clients;

namespace MealiePicnic.Endpoints;

/// <summary>The Picnic session: status, login, two-factor, logout.</summary>
internal static class PicnicEndpoints
{
    internal static void MapPicnicEndpoints(this IEndpointRouteBuilder api)
    {

        // ----------------------------------------------------------------- picnic auth

        api.MapGet("/picnic/status", async (PicnicClient picnic, CancellationToken ct) =>
            Results.Ok(new
            {
                authenticated = await picnic.IsUsableAsync(ct),
                // Lets the UI prefill the login dialog and say precisely what is missing.
                // The username is safe to expose; the password never leaves the server.
                hasConfiguredUser = picnic.HasConfiguredUser,
                hasConfiguredPassword = picnic.HasConfiguredPassword,
                configuredUser = picnic.ConfiguredUser,
            }));

        api.MapPost("/picnic/logout", async (PicnicClient picnic, CancellationToken ct) =>
        {
            await picnic.LogoutAsync(ct);
            return Results.Ok(new { loggedOut = true });
        });

        api.MapPost("/picnic/login", async (LoginRequest? body, PicnicClient picnic, CancellationToken ct) =>
        {
            var needs2fa = await picnic.LoginAsync(body?.User, body?.Password, ct);
            return Results.Ok(new { needs2fa });
        }).RequireRateLimiting("credentials");

        api.MapPost("/picnic/2fa/generate", async (TwoFactorChannel? body, PicnicClient picnic, CancellationToken ct) =>
        {
            var channel = string.IsNullOrWhiteSpace(body?.Channel) ? "EMAIL" : body.Channel.ToUpperInvariant();
            if (channel is not ("SMS" or "EMAIL"))
                return Results.BadRequest(new { error = "invalid_channel" });

            await picnic.Generate2faAsync(channel, ct);
            return Results.Ok(new { sent = true });
        }).RequireRateLimiting("credentials");

        api.MapPost("/picnic/2fa/verify", async (Otp? body, PicnicClient picnic, CancellationToken ct) =>
        {
            var code = body?.Code?.Trim() ?? "";
            if (!Regex.IsMatch(code, "^[0-9]{4,10}$"))
                return Results.BadRequest(new { error = "invalid_code" });

            await picnic.Verify2faAsync(code, ct);
            return Results.Ok(new { verified = true });
        }).RequireRateLimiting("credentials");
    }
}
