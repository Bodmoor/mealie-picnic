using System.Text.Json;
using MealiePicnic.Clients;
using MealiePicnic.Presentation;

namespace MealiePicnic.Tests;

/// <summary>
/// Issue #13. The interesting failures here are not "is this word translated"
/// but the two ways a half-translated app ships: a string that exists in one
/// language and not the other, and a surface the localization never reached --
/// which is exactly what happened to assets/app.js, where the whole Picnic login
/// flow talks to the user from JavaScript that no server-side mechanism can see.
/// </summary>
public class AppTextTests
{
    [Theory]
    [InlineData("nl", "nl")]
    [InlineData("en", "en")]
    [InlineData("EN", "en")]
    [InlineData("en-GB", "en")]
    [InlineData(" en ", "en")]
    public void Language_codes_select_a_language(string configured, string expected) =>
        Assert.Equal(expected, AppText.For(configured).Lang);

    // An operator typo must leave the app in its original language rather than
    // half-translated or crashed on startup.
    [Theory]
    [InlineData("")]
    [InlineData("nl-NL")]
    [InlineData("de")]
    [InlineData("english")]  // close, but not the code -- still not English
    [InlineData("nonsense")]
    public void Unknown_languages_fall_back_to_dutch(string configured) =>
        Assert.Equal("nl", AppText.For(configured).Lang);

    [Fact]
    public void Every_string_is_filled_in_both_languages()
    {
        // `required` makes the compiler catch a missing property; this catches
        // one that was added to the list and then left blank.
        var blank = new List<string>();
        foreach (var property in typeof(AppText).GetProperties())
        {
            if (property.PropertyType != typeof(string)) continue;

            foreach (var (name, text) in new[] { ("Dutch", AppText.Dutch), ("English", AppText.English) })
                if (string.IsNullOrWhiteSpace((string?)property.GetValue(text)))
                    blank.Add($"{name}.{property.Name}");
        }

        Assert.True(blank.Count == 0, "Blank strings: " + string.Join(", ", blank));
    }

    [Fact]
    public void The_two_languages_actually_differ()
    {
        // A copy-paste that left English as a clone of Dutch would otherwise pass
        // every other test here.
        var identical = typeof(AppText).GetProperties()
            .Where(p => p.PropertyType == typeof(string) && p.Name != nameof(AppText.PicnicLabel)
                                                         && p.Name != nameof(AppText.ChannelSms)
                                                         && p.Name != nameof(AppText.TwoFactorTitle))
            .Where(p => Equals(p.GetValue(AppText.Dutch), p.GetValue(AppText.English)))
            .Select(p => p.Name)
            .ToList();

        Assert.True(identical.Count == 0, "Untranslated: " + string.Join(", ", identical));
    }

    // ------------------------------------------------------------------ numbers

    [Fact]
    public void Decimal_separator_follows_the_language()
    {
        Assert.Equal(",", AppText.Dutch.Numbers.NumberDecimalSeparator);
        Assert.Equal(".", AppText.English.Numbers.NumberDecimalSeparator);
    }

    [Fact]
    public void The_client_is_told_the_same_separator_the_server_formats_with()
    {
        // The disagreement this closes: PriceText produced "€2.69" while the salt
        // badge produced "1,2 g", on the same card.
        foreach (var text in new[] { AppText.Dutch, AppText.English })
        {
            var client = JsonDocument.Parse(text.ClientJson()).RootElement;
            Assert.Equal(text.Numbers.NumberDecimalSeparator, client.GetProperty("decimalSeparator").GetString());
        }
    }

    // --------------------------------------------------------------- app.js block

    [Fact]
    public void Client_json_carries_every_string_app_js_asks_for()
    {
        // The keys app.js reads. If one is renamed on either side the dialog goes
        // blank rather than throwing, so nothing else would notice.
        string[] keys =
        [
            "lang", "decimalSeparator", "passwordKeepConfigured", "passwordPlaceholder",
            "credsFromConfiguration", "missingConfigBefore", "missingConfigAfter", "and",
            "enterEmail", "enterPassword", "loginFailed", "chooseChannel", "requestingCode",
            "codeSentVia", "emailChannelName", "couldNotSendCode", "verificationFailed",
            "organicAlt", "organicTitle", "saltTitle", "saltUnit",
            "allergenDeclared", "allergenSuspected",
        ];

        foreach (var text in new[] { AppText.Dutch, AppText.English })
        {
            var client = JsonDocument.Parse(text.ClientJson()).RootElement;
            foreach (var key in keys)
            {
                Assert.True(client.TryGetProperty(key, out var value), $"{text.Lang}: missing '{key}'");
                Assert.False(string.IsNullOrWhiteSpace(value.GetString()), $"{text.Lang}: blank '{key}'");
            }
        }
    }

    [Fact]
    public void Client_json_carries_a_label_for_every_allergen_group_the_server_sends()
    {
        // app.js looks the label up by the group key and skips anything it has no
        // label for, so a group added server-side without a label here would go
        // silently missing from the card rather than fail loudly (issue #14).
        string[] groups = [AllergenGroups.Nuts, AllergenGroups.Milk];

        foreach (var text in new[] { AppText.Dutch, AppText.English })
        {
            var labels = JsonDocument.Parse(text.ClientJson()).RootElement.GetProperty("allergenLabels");

            foreach (var group in groups)
            {
                Assert.True(labels.TryGetProperty(group, out var label), $"{text.Lang}: no label for '{group}'");
                Assert.False(string.IsNullOrWhiteSpace(label.GetString()), $"{text.Lang}: blank label for '{group}'");
            }
        }
    }

    [Fact]
    public void Code_sent_message_keeps_the_channel_placeholder_app_js_substitutes()
    {
        foreach (var text in new[] { AppText.Dutch, AppText.English })
            Assert.Contains("{channel}",
                JsonDocument.Parse(text.ClientJson()).RootElement.GetProperty("codeSentVia").GetString());
    }

    [Fact]
    public void Client_json_cannot_close_the_script_block_it_sits_in()
    {
        // The block is inside <script type="application/json">, so a value
        // containing a closing tag would end it early and spill markup.
        var hostile = AppText.Dutch with { JsSaltUnit = "</script><img src=x onerror=alert(1)>" };

        Assert.DoesNotContain("</script>", hostile.ClientJson(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", hostile.ClientJson(), StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------- surfaces

    [Fact]
    public void The_shell_is_rendered_in_the_chosen_language()
    {
        var dutch = Html.PageFor(AppText.Dutch);
        var english = Html.PageFor(AppText.English);

        Assert.Contains("<html lang=\"nl\"", dutch);
        Assert.Contains("<html lang=\"en\"", english);
        Assert.Contains(">Laden...<", dutch);
        Assert.Contains(">Loading...<", english);
    }

    [Fact]
    public void The_english_shell_has_no_dutch_left_in_it()
    {
        // The tripwire for the failure this issue exists to prevent: one surface
        // missed, so the page reads English until it needs to say something.
        string[] dutchOnly =
        [
            "Laden...", "Inloggen", "Sluiten", "Verifieren", "Wachtwoord",
            "ingelogd", "uitloggen", "afmelden", "Terug naar de lijst", "Boodschappen",
        ];

        var english = Html.PageFor(AppText.English);
        var found = dutchOnly.Where(word => english.Contains(word, StringComparison.Ordinal)).ToList();

        Assert.True(found.Count == 0, "Dutch left in the English shell: " + string.Join(", ", found));
    }

    [Fact]
    public void The_shell_embeds_the_client_strings_as_a_data_block()
    {
        // A data block, not a script: the type is not a JavaScript MIME type, so
        // the strict script-src (no 'unsafe-inline') does not apply to it. Making
        // this an executable inline script would be silently blocked in the
        // browser and only show up as a dead login dialog.
        var page = Html.PageFor(AppText.English);

        Assert.Contains("<script type=\"application/json\" id=\"i18n\">", page);

        var start = page.IndexOf("id=\"i18n\">", StringComparison.Ordinal) + "id=\"i18n\">".Length;
        var json = page[start..page.IndexOf("</script>", start, StringComparison.Ordinal)];

        Assert.Equal("en", JsonDocument.Parse(json).RootElement.GetProperty("lang").GetString());
    }

    [Fact]
    public void The_manifest_follows_the_language()
    {
        var dutch = JsonDocument.Parse(Icons.ManifestFor(AppText.Dutch)).RootElement;
        var english = JsonDocument.Parse(Icons.ManifestFor(AppText.English)).RootElement;

        Assert.Equal("nl", dutch.GetProperty("lang").GetString());
        Assert.Equal("en", english.GetProperty("lang").GetString());
        Assert.Equal("Boodschappen", dutch.GetProperty("short_name").GetString());
        Assert.Equal("Groceries", english.GetProperty("short_name").GetString());

        // The product lockup is a name, not a string to translate.
        Assert.Equal("Mealie → Picnic", english.GetProperty("name").GetString());
    }
}
