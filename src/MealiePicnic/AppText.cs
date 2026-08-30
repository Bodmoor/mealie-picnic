using System.Globalization;
using System.Text.Json;
using MealiePicnic.Clients;

namespace MealiePicnic;

/// <summary>
/// Every string the app itself writes for a human to read, in the configured
/// language, plus the number format that goes with it (issue #13).
///
/// Deliberately a plain object rather than <c>.resx</c> and
/// <c>IStringLocalizer</c>, for two reasons that both come from this app rather
/// than from taste:
///
/// <list type="number">
/// <item>The project builds with <c>InvariantGlobalization</c> (see
/// MealiePicnic.csproj), so no culture except the invariant one exists at
/// runtime. Satellite resource assemblies are selected by
/// <see cref="CultureInfo"/>, and there is no culture to select them with --
/// resource lookup would silently always return the neutral fallback. Turning
/// invariant globalization off to buy a resource mechanism would pull ICU into
/// the container for two languages that are known at compile time.</item>
/// <item>The language is one app-wide setting fixed at startup (LANGUAGE), not a
/// per-request negotiation. There is nothing for a scoped, injected localizer to
/// vary on, so <see cref="Current"/> is set once in Program.cs and read
/// everywhere, including from Razor slices that take no services.</item>
/// </list>
///
/// Adding a string means adding a property here and a value in both languages;
/// the compiler then finds every place that has to change, which a string-keyed
/// resource lookup would not.
/// </summary>
public sealed record AppText
{
    private static AppText? current;

    /// <summary>
    /// The language in force for this process. Assigned once at startup from
    /// <see cref="AppOptions.Language"/>; Dutch until then, so anything
    /// constructed before configuration is read (and every test that does not
    /// care) still has real text rather than nulls.
    ///
    /// Defaulted on read rather than in a field initializer: those run in
    /// textual order, and <see cref="Dutch"/> is declared further down, so
    /// initializing from it here would capture null.
    /// </summary>
    public static AppText Current
    {
        get => current ??= Dutch;
        set => current = value;
    }

    /// <summary>BCP 47 tag for <c>&lt;html lang&gt;</c> and the manifest.</summary>
    public required string Lang { get; init; }

    /// <summary>
    /// Decimal separator for prices and the salt figure. Built by hand rather
    /// than taken from a <see cref="CultureInfo"/>: under invariant
    /// globalization every culture is the invariant one, so <c>nl-NL</c> would
    /// silently format with a point. Only the separator differs between the two
    /// languages, so only the separator is set.
    /// </summary>
    public required NumberFormatInfo Numbers { get; init; }

    // ------------------------------------------------------------ app shell

    public required string BrandHomeTitle { get; init; }
    public required string PicnicLabel { get; init; }
    public required string PicnicSignedIn { get; init; }
    public required string PicnicSignedOut { get; init; }
    /// <summary>
    /// Button labels, not words in a sentence (issue #60). Each names the service
    /// it acts on, because the two sit side by side and "sign out" alone would
    /// not say which one you were about to leave.
    /// </summary>
    public required string PicnicSignInAction { get; init; }

    public required string PicnicSignOutAction { get; init; }

    /// <summary>
    /// The app's own sign-out. The signed-in account, when there is one, is
    /// appended by the shell in parentheses (issue #22) -- the shared-password
    /// identity has no name to show, so the label has to stand on its own.
    /// </summary>
    public required string AppSignOutAction { get; init; }

    /// <summary>Tooltip saying which sign-out this is, since the label cannot
    /// carry "bij deze app" and an account name at once without becoming a
    /// sentence again.</summary>
    public required string AppSignOutTitle { get; init; }
    public required string Loading { get; init; }

    // --------------------------------------------------------- picnic dialogs

    public required string CredsTitle { get; init; }
    public required string CredsEmailPlaceholder { get; init; }
    public required string CredsSubmit { get; init; }
    public required string DialogClose { get; init; }
    public required string TwoFactorTitle { get; init; }
    public required string TwoFactorChooseChannel { get; init; }
    public required string ChannelSms { get; init; }
    public required string ChannelEmail { get; init; }
    public required string TwoFactorVerify { get; init; }

    // ------------------------------------------------------------ login pages

    public required string SignInTitle { get; init; }
    public required string PasswordPlaceholder { get; init; }
    public required string SignInSubmit { get; init; }
    public required string WrongPassword { get; init; }
    public required string SignInWithSso { get; init; }

    // -------------------------------------------------------------- list view

    public required string MealieListPicker { get; init; }
    public required string FetchMealieList { get; init; }
    public required string AddAllToBasket { get; init; }
    public required string CheckOffInMealie { get; init; }
    public required string ShowExcluded { get; init; }
    public required string TagNew { get; init; }
    public required string TagLinked { get; init; }
    public required string TagNotAtPicnic { get; init; }
    public required string TagExcluded { get; init; }
    public required string TagCheckedOff { get; init; }
    public required string Restore { get; init; }
    public required string ListEmpty { get; init; }
    public required string ListAllCheckedOff { get; init; }

    public required Func<int, string> HeadingNew { get; init; }
    public required Func<int, string> HeadingLinked { get; init; }
    public required Func<int, string> HeadingNotAtPicnic { get; init; }
    public required Func<int, string> HeadingDone { get; init; }

    // ------------------------------------------------------------ search view

    public required string Back { get; init; }
    public required string NotLinkedYet { get; init; }
    public required string Search { get; init; }
    public required string ExcludeFromPicnic { get; init; }
    public required string Searching { get; init; }
    public required string NoResults { get; init; }

    public required Func<string, string> LinkedTo { get; init; }

    /// <summary>Split around the product id, which the grid emphasises.</summary>
    public required string LinkNotInResultsBefore { get; init; }

    public required string LinkNotInResultsAfter { get; init; }

    // ------------------------------------------------------------ basket log

    public required string BasketNothingLinked { get; init; }
    public required string BasketInCartNotCheckedOff { get; init; }
    public required string BasketStaysOnList { get; init; }
    public required string BasketRequestFailed { get; init; }
    public required string BasketPicnicRefused { get; init; }
    public required string BasketUpstreamFailed { get; init; }
    public required string BasketInternalError { get; init; }

    public required Func<int, string> BasketNotLinkedYet { get; init; }

    // ---------------------------------------------------------------- amounts

    /// <summary>One pack, where Picnic never told us how big a pack is.</summary>
    public required string AmountOnePackUnknownSize { get; init; }

    public required Func<string, string> AmountOnePack { get; init; }
    public required Func<int, string> AmountCount { get; init; }
    public required Func<int, string, string> AmountCountOfPack { get; init; }

    // -------------------------------------------------------------- household

    public required string NoHousehold { get; init; }
    public required Func<string, string> NoHouseholdForEmail { get; init; }

    // --------------------------------------------------------------- manifest

    public required string AppShortName { get; init; }
    public required string AppDescription { get; init; }

    // ------------------------------------------------- strings used by app.js

    public required string JsPasswordKeepConfigured { get; init; }
    public required string JsCredsFromConfiguration { get; init; }
    public required string JsEnterEmail { get; init; }
    public required string JsEnterPassword { get; init; }
    public required string JsLoginFailed { get; init; }
    public required string JsRequestingCode { get; init; }
    public required string JsCouldNotSendCode { get; init; }
    public required string JsVerificationFailed { get; init; }
    public required string JsEmailChannelName { get; init; }
    public required string JsAnd { get; init; }
    public required string JsOrganicAlt { get; init; }
    public required string JsOrganicTitle { get; init; }
    public required string JsSaltTitle { get; init; }
    public required string JsSaltUnit { get; init; }

    // Allergen chips (issue #14). The labels are keyed by the group name the
    // server sends, so app.js can look one up without ever writing a value from
    // the response into the markup.
    public required string AllergenNuts { get; init; }

    public required string AllergenMilk { get; init; }
    public required string AllergenDeclaredTitle { get; init; }
    public required string AllergenSuspectedTitle { get; init; }

    /// <summary>
    /// The third strength (issue #58): the term appeared only in a "kan sporen
    /// van ... bevatten" line, which warns about the factory rather than
    /// describing what is in the product.
    /// </summary>
    public required string AllergenTracesTitle { get; init; }

    // --------------------------------------------------- allergen explanation

    /// <summary>
    /// The line under the results grid, split around the two marks it names.
    /// Said once rather than per card, and worded so it never claims a product is
    /// free of anything -- a card with no chip may equally be a page that could
    /// not be read.
    /// </summary>
    public required string AllergenNoteBeforeTick { get; init; }

    public required string AllergenNoteBeforeQuestion { get; init; }
    public required string AllergenNoteAfter { get; init; }

    // --------------------------------------------------- product detail (#48)

    public required string DetailsOpen { get; init; }
    public required string DetailsBack { get; init; }
    public required string DetailsAllergens { get; init; }
    public required string DetailsFacts { get; init; }
    public required string DetailsOrganic { get; init; }
    public required string DetailsSalt { get; init; }
    public required string DetailsDescription { get; init; }
    public required string DetailsHighlights { get; init; }
    public required string DetailsSource { get; init; }
    public required string DetailsUnavailable { get; init; }

    /// <summary>Why a declared allergen offers no deny button. Picnic emphasising
    /// a term in the ingredient list is EU labelling, not a guess of ours.</summary>
    public required string DetailsDeclaredFixed { get; init; }

    public required string VerdictConfirm { get; init; }
    public required string VerdictDeny { get; init; }
    public required string VerdictClear { get; init; }
    public required string VerdictConfirmed { get; init; }
    public required string VerdictDenied { get; init; }
    public required string VerdictUnreviewed { get; init; }

    /// <summary>Shown when the ingredient text has changed since the verdict was
    /// recorded -- the verdict stands, but it was made about a different recipe.</summary>
    public required string VerdictStale { get; init; }

    /// <summary>
    /// The label for an allergen group, looked up by the key the parser produced
    /// rather than taken from anything on the wire -- the same rule app.js
    /// follows for the chips. An unknown group falls back to its own key, which
    /// is honest: it says a group was found without inventing a translation.
    /// </summary>
    public string AllergenLabel(string group) => group switch
    {
        AllergenGroups.Nuts => AllergenNuts,
        AllergenGroups.Milk => AllergenMilk,
        _ => group,
    };

    /// <summary>
    /// The two-part "not set in configuration" notice, split around the list of
    /// missing keys that app.js builds.
    /// </summary>
    public required string JsMissingConfigBefore { get; init; }

    public required string JsMissingConfigAfter { get; init; }

    /// <summary>
    /// What a failed request says (issue #63). Two messages, because only two
    /// things a reader can act on: an upstream is down and it is worth trying
    /// again later, or something on our side went wrong and it is not.
    /// </summary>
    public required string JsUpstreamDown { get; init; }

    public required string JsSomethingWentWrong { get; init; }

    public required Func<string, string> JsCodeSentVia { get; init; }

    /// <summary>
    /// The subset app.js needs, as JSON for the shell to embed. Serialized with
    /// the default (HTML-escaping) encoder, so <c>&lt;/script&gt;</c> in a value
    /// could not close the block it sits in.
    /// </summary>
    public string ClientJson() => JsonSerializer.Serialize(new
    {
        lang = Lang,
        decimalSeparator = Numbers.NumberDecimalSeparator,
        passwordKeepConfigured = JsPasswordKeepConfigured,
        passwordPlaceholder = PasswordPlaceholder,
        credsFromConfiguration = JsCredsFromConfiguration,
        missingConfigBefore = JsMissingConfigBefore,
        missingConfigAfter = JsMissingConfigAfter,
        upstreamDown = JsUpstreamDown,
        somethingWentWrong = JsSomethingWentWrong,
        and = JsAnd,
        enterEmail = JsEnterEmail,
        enterPassword = JsEnterPassword,
        loginFailed = JsLoginFailed,
        chooseChannel = TwoFactorChooseChannel,
        requestingCode = JsRequestingCode,
        codeSentVia = JsCodeSentVia("{channel}"),
        emailChannelName = JsEmailChannelName,
        couldNotSendCode = JsCouldNotSendCode,
        verificationFailed = JsVerificationFailed,
        organicAlt = JsOrganicAlt,
        organicTitle = JsOrganicTitle,
        saltTitle = JsSaltTitle,
        saltUnit = JsSaltUnit,
        // Keyed by the group name PicnicDetails sends, so app.js looks a label up
        // rather than rendering anything that came off the wire.
        allergenLabels = new Dictionary<string, string>
        {
            [AllergenGroups.Nuts] = AllergenNuts,
            [AllergenGroups.Milk] = AllergenMilk,
        },
        allergenDeclared = AllergenDeclaredTitle,
        allergenSuspected = AllergenSuspectedTitle,
        allergenTraces = AllergenTracesTitle,
    });

    /// <summary>
    /// The text for a language code: "en", or "en-" with any region ("en-GB"),
    /// selects English; everything else is Dutch.
    ///
    /// Matching the subtag rather than any string starting with "en" is the
    /// difference between accepting a real language code and accepting
    /// "enigmatic". Anything unrecognised falls back rather than throwing,
    /// because the value is operator-supplied and a typo in LANGUAGE should
    /// leave a working app in its original language, not stop it booting.
    /// </summary>
    public static AppText For(string language)
    {
        var code = language.Trim();
        var english = code.Equals("en", StringComparison.OrdinalIgnoreCase)
            || code.StartsWith("en-", StringComparison.OrdinalIgnoreCase);
        return english ? English : Dutch;
    }

    private static NumberFormatInfo Separator(string separator)
    {
        var format = (NumberFormatInfo)NumberFormatInfo.InvariantInfo.Clone();
        format.NumberDecimalSeparator = separator;
        format.NumberGroupSeparator = separator == "," ? "." : ",";
        return format;
    }

    public static AppText Dutch { get; } = new()
    {
        Lang = "nl",
        Numbers = Separator(","),

        BrandHomeTitle = "Terug naar de lijst",
        PicnicLabel = "Picnic",
        PicnicSignedIn = "ingelogd",
        PicnicSignedOut = "niet ingelogd",
        PicnicSignInAction = "Inloggen bij Picnic",
        PicnicSignOutAction = "Uitloggen bij Picnic",
        AppSignOutAction = "Afmelden",
        AppSignOutTitle = "Afmelden bij deze app",
        Loading = "Laden...",

        CredsTitle = "Picnic inloggen",
        CredsEmailPlaceholder = "E-mailadres",
        CredsSubmit = "Inloggen",
        DialogClose = "Sluiten",
        TwoFactorTitle = "Picnic 2FA",
        TwoFactorChooseChannel = "Kies waar de code naartoe moet.",
        ChannelSms = "SMS",
        ChannelEmail = "E-mail",
        TwoFactorVerify = "Verifieren",

        SignInTitle = "Inloggen",
        PasswordPlaceholder = "Wachtwoord",
        SignInSubmit = "Inloggen",
        WrongPassword = "Onjuist wachtwoord",
        SignInWithSso = "Inloggen met SSO",

        MealieListPicker = "Mealie lijst",
        FetchMealieList = "Haal Mealie lijst op",
        AddAllToBasket = "Alles in Picnic mand",
        CheckOffInMealie = "afvinken in Mealie",
        ShowExcluded = "toon uitgesloten",
        TagNew = "nieuw",
        TagLinked = "gekoppeld",
        TagNotAtPicnic = "niet bij Picnic",
        TagExcluded = "uitgesloten",
        TagCheckedOff = "afgevinkt",
        Restore = "terugzetten",
        ListEmpty = "Lijst is leeg.",
        ListAllCheckedOff = "Alles op deze lijst is afgevinkt.",

        HeadingNew = n => $"Nieuw ({n})",
        HeadingLinked = n => $"Gekoppeld ({n})",
        HeadingNotAtPicnic = n => $"Niet bij Picnic ({n})",
        HeadingDone = n => $"Afgevinkt / toegevoegd aan mand ({n})",

        Back = "terug",
        NotLinkedYet = "nog niet gekoppeld",
        Search = "Zoek",
        ExcludeFromPicnic = "Niet bij Picnic",
        Searching = "Zoeken...",
        NoResults = "Geen resultaten.",

        LinkedTo = label => $"gekoppeld aan {label}",
        LinkNotInResultsBefore = "Huidige koppeling ",
        LinkNotInResultsAfter = " staat niet in deze resultaten.",

        BasketNothingLinked = "Niets toegevoegd: geen gekoppelde items op deze lijst.",
        BasketInCartNotCheckedOff = "(in de kar, niet afgevinkt)",
        BasketStaysOnList = "blijft op de lijst",
        BasketRequestFailed = "De aanvraag naar Picnic is mislukt; niets is toegevoegd.",
        BasketPicnicRefused = "Picnic weigerde het product",
        BasketUpstreamFailed = "aanvraag bij Picnic mislukt",
        BasketInternalError = "interne fout",

        BasketNotLinkedYet = n => $"{n} nog niet gekoppeld",

        AmountOnePackUnknownSize = "1 pak (geen pakgrootte bekend)",
        AmountOnePack = pack => $"1 x {pack}",
        AmountCount = n => $"{n} x",
        AmountCountOfPack = (n, pack) => $"{n} x {pack}",

        NoHousehold = "Dit account is niet gekoppeld aan een Mealie huishouden.",
        NoHouseholdForEmail = email =>
            $"Dit account ({email}) is niet gekoppeld aan een Mealie huishouden. " +
            "Vraag een beheerder dit te koppelen.",

        AppShortName = "Boodschappen",
        AppDescription = "Koppel Mealie-boodschappen aan Picnic-producten en vul de kar.",

        JsPasswordKeepConfigured = "Laat leeg om het ingestelde wachtwoord te gebruiken",
        JsCredsFromConfiguration = "Gegevens komen uit de configuratie. Pas ze hier eventueel aan.",
        JsEnterEmail = "Vul een e-mailadres in.",
        JsEnterPassword = "Vul een wachtwoord in.",
        JsLoginFailed = "Login mislukt: ",
        JsRequestingCode = "Code aanvragen...",
        JsCouldNotSendCode = "Kon geen code sturen: ",
        JsVerificationFailed = "Verificatie mislukt: ",
        JsEmailChannelName = "e-mail",
        JsAnd = " en ",
        JsOrganicAlt = "Biologisch",
        JsOrganicTitle = "Het product wordt op de Picnic-pagina als biologisch aangeduid",
        JsSaltTitle = "Zout per 100 g",
        JsSaltUnit = "g zout",

        AllergenNuts = "noten",
        AllergenMilk = "melk",
        AllergenDeclaredTitle = "Picnic markeert dit allergeen in de ingrediëntenlijst",
        AllergenSuspectedTitle = "Gevonden in de ingrediëntentekst, maar niet door Picnic gemarkeerd",
        AllergenTracesTitle = "Alleen genoemd als mogelijke sporen: een waarschuwing over de fabriek, geen ingrediënt",

        AllergenNoteBeforeTick = "Allergenen komen van de Picnic-productpagina. ",
        AllergenNoteBeforeQuestion = " betekent dat Picnic het allergeen zelf markeert, ",
        AllergenNoteAfter =
            " dat wij het woord in de ingrediënten vonden. " +
            "Geen label betekent niet dat het product vrij is van dat allergeen.",

        DetailsOpen = "Productinfo",
        DetailsBack = "Terug naar de resultaten",
        DetailsAllergens = "Allergenen",
        DetailsFacts = "Kenmerken",
        DetailsOrganic = "Biologisch",
        DetailsSalt = "Zout per 100 g",
        DetailsDescription = "Omschrijving",
        DetailsHighlights = "Kenmerken van Picnic",
        DetailsSource = "Gevonden in",
        DetailsUnavailable = "De productpagina van Picnic kon niet gelezen worden.",
        DetailsDeclaredFixed =
            "Picnic markeert dit allergeen zelf in de ingrediëntenlijst; dat is een wettelijke " +
            "vermelding en kan hier niet weerlegd worden.",

        VerdictConfirm = "Klopt",
        VerdictDeny = "Klopt niet",
        VerdictClear = "Ongedaan maken",
        VerdictConfirmed = "Bevestigd",
        VerdictDenied = "Weerlegd",
        VerdictUnreviewed = "Nog niet beoordeeld",
        VerdictStale = "De ingrediëntentekst is veranderd sinds deze beoordeling.",

        JsMissingConfigBefore = "Niet ingesteld in de configuratie: ",
        JsMissingConfigAfter = ". Vul aan; er wordt niets opgeslagen, alleen het token wordt bewaard.",

        JsUpstreamDown = "Niet bereikbaar. Probeer het zo nog eens.",
        JsSomethingWentWrong = "Er ging iets mis. De actie is niet uitgevoerd.",

        JsCodeSentVia = channel => $"Code verstuurd via {channel}.",
    };

    public static AppText English { get; } = new()
    {
        Lang = "en",
        Numbers = Separator("."),

        BrandHomeTitle = "Back to the list",
        PicnicLabel = "Picnic",
        PicnicSignedIn = "signed in",
        PicnicSignedOut = "not signed in",
        PicnicSignInAction = "Sign in to Picnic",
        PicnicSignOutAction = "Sign out of Picnic",
        AppSignOutAction = "Sign out",
        AppSignOutTitle = "Sign out of this app",
        Loading = "Loading...",

        CredsTitle = "Sign in to Picnic",
        CredsEmailPlaceholder = "Email address",
        CredsSubmit = "Sign in",
        DialogClose = "Close",
        TwoFactorTitle = "Picnic 2FA",
        TwoFactorChooseChannel = "Choose where the code should go.",
        ChannelSms = "SMS",
        ChannelEmail = "Email",
        TwoFactorVerify = "Verify",

        SignInTitle = "Sign in",
        PasswordPlaceholder = "Password",
        SignInSubmit = "Sign in",
        WrongPassword = "Wrong password",
        SignInWithSso = "Sign in with SSO",

        MealieListPicker = "Mealie list",
        FetchMealieList = "Fetch Mealie list",
        AddAllToBasket = "Add all to Picnic basket",
        CheckOffInMealie = "check off in Mealie",
        ShowExcluded = "show excluded",
        TagNew = "new",
        TagLinked = "linked",
        TagNotAtPicnic = "not at Picnic",
        TagExcluded = "excluded",
        TagCheckedOff = "checked off",
        Restore = "restore",
        ListEmpty = "This list is empty.",
        ListAllCheckedOff = "Everything on this list is checked off.",

        HeadingNew = n => $"New ({n})",
        HeadingLinked = n => $"Linked ({n})",
        HeadingNotAtPicnic = n => $"Not at Picnic ({n})",
        HeadingDone = n => $"Checked off / added to basket ({n})",

        Back = "back",
        NotLinkedYet = "not linked yet",
        Search = "Search",
        ExcludeFromPicnic = "Not at Picnic",
        Searching = "Searching...",
        NoResults = "No results.",

        LinkedTo = label => $"linked to {label}",
        LinkNotInResultsBefore = "The current link ",
        LinkNotInResultsAfter = " is not among these results.",

        BasketNothingLinked = "Nothing added: no linked items on this list.",
        BasketInCartNotCheckedOff = "(in the basket, not checked off)",
        BasketStaysOnList = "stays on the list",
        BasketRequestFailed = "The request to Picnic failed; nothing was added.",
        BasketPicnicRefused = "Picnic refused the product",
        BasketUpstreamFailed = "upstream request failed",
        BasketInternalError = "internal error",

        BasketNotLinkedYet = n => $"{n} not linked yet",

        AmountOnePackUnknownSize = "1 pack (pack size unknown)",
        AmountOnePack = pack => $"1 x {pack}",
        AmountCount = n => $"{n} x",
        AmountCountOfPack = (n, pack) => $"{n} x {pack}",

        NoHousehold = "This account is not linked to a Mealie household.",
        NoHouseholdForEmail = email =>
            $"This account ({email}) is not linked to a Mealie household. " +
            "Ask an administrator to link it.",

        AppShortName = "Groceries",
        AppDescription = "Link Mealie groceries to Picnic products and fill the basket.",

        JsPasswordKeepConfigured = "Leave blank to use the configured password",
        JsCredsFromConfiguration = "These come from the configuration. Adjust them here if needed.",
        JsEnterEmail = "Enter an email address.",
        JsEnterPassword = "Enter a password.",
        JsLoginFailed = "Sign-in failed: ",
        JsRequestingCode = "Requesting code...",
        JsCouldNotSendCode = "Could not send a code: ",
        JsVerificationFailed = "Verification failed: ",
        JsEmailChannelName = "email",
        JsAnd = " and ",
        JsOrganicAlt = "Organic",
        JsOrganicTitle = "Picnic's product page describes this product as organic",
        JsSaltTitle = "Salt per 100 g",
        JsSaltUnit = "g salt",

        AllergenNuts = "nuts",
        AllergenMilk = "milk",
        AllergenDeclaredTitle = "Picnic marks this allergen in the ingredient list",
        AllergenSuspectedTitle = "Found in the ingredient text, but not marked by Picnic",
        AllergenTracesTitle = "Only mentioned as possible traces: a warning about the factory, not an ingredient",

        AllergenNoteBeforeTick = "Allergens come from Picnic's product page. ",
        AllergenNoteBeforeQuestion = " means Picnic marks the allergen itself, ",
        AllergenNoteAfter =
            " that we found the word in the ingredients. " +
            "No label does not mean the product is free of that allergen.",

        DetailsOpen = "Details",
        DetailsBack = "Back to the results",
        DetailsAllergens = "Allergens",
        DetailsFacts = "Facts",
        DetailsOrganic = "Organic",
        DetailsSalt = "Salt per 100 g",
        DetailsDescription = "Description",
        DetailsHighlights = "Picnic highlights",
        DetailsSource = "Found in",
        DetailsUnavailable = "Picnic's product page could not be read.",
        DetailsDeclaredFixed =
            "Picnic marks this allergen in the ingredient list itself. That is a legal " +
            "declaration and cannot be overruled here.",

        VerdictConfirm = "Correct",
        VerdictDeny = "Not correct",
        VerdictClear = "Undo",
        VerdictConfirmed = "Confirmed",
        VerdictDenied = "Refuted",
        VerdictUnreviewed = "Not reviewed yet",
        VerdictStale = "The ingredient text has changed since this was reviewed.",

        JsMissingConfigBefore = "Not set in the configuration: ",
        JsMissingConfigAfter = ". Fill these in; nothing is stored, only the token is kept.",

        JsUpstreamDown = "Not reachable. Try again in a moment.",
        JsSomethingWentWrong = "Something went wrong. The action did not run.",

        JsCodeSentVia = channel => $"Code sent via {channel}.",
    };
}
