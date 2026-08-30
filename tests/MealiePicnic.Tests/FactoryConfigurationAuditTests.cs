using System.Runtime.CompilerServices;

namespace MealiePicnic.Tests;

/// <summary>
/// A tripwire for a test that passes on one machine and fails on another.
///
/// WebApplicationFactory hosts the app in the Development environment, which
/// loads the developer's real user secrets. A developer who has OIDC configured
/// for manual testing therefore hosts a DIFFERENT app than CI does: OIDC-enabled,
/// with /login/admin mapped and /login challenging instead of showing a form.
///
/// EndpointMetadataTests learned this the expensive way — green locally, red in
/// CI, on an endpoint that only exists when OIDC is configured. Every factory
/// must therefore pin the OIDC settings, to either "" or a fake authority,
/// rather than inheriting whatever the machine happens to have.
/// </summary>
public class FactoryConfigurationAuditTests
{
    [Fact]
    public void Every_test_factory_pins_the_oidc_settings()
    {
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(TestsDirectory(), "*.cs"))
        {
            var name = Path.GetFileName(file);
            if (name == "FactoryConfigurationAuditTests.cs") continue;

            var text = File.ReadAllText(file);
            if (!text.Contains("new WebApplicationFactory<Program>()", StringComparison.Ordinal)) continue;
            if (text.Contains("BOODSCHAPPEN_OIDC_AUTHORITY", StringComparison.Ordinal)) continue;

            offenders.Add(name);
        }

        Assert.True(offenders.Count == 0,
            "These host the app without pinning the OIDC settings, so what they test depends on "
            + "whether the machine running them has OIDC in user secrets:\n" + string.Join("\n", offenders));
    }

    private static string TestsDirectory([CallerFilePath] string thisFile = "") =>
        Path.GetDirectoryName(thisFile)!;
}
