namespace MealiePicnic.Tests;

/// <summary>
/// <see cref="AppText.Current"/> is process-wide by design (issue #13): the
/// language is one deployment setting, fixed at startup, so nothing per-request
/// varies and the Razor slices can read it without services.
///
/// That makes it shared state between tests. Two kinds of test touch it: the
/// ones that render a slice in a chosen language, and the ones that boot the app
/// through WebApplicationFactory, which assigns it from configuration during
/// startup. xUnit runs different classes in parallel, so without this collection
/// a page could be rendered halfway through another class's startup and come
/// back in the other language.
///
/// Classes in one collection never run concurrently, which is all this needs.
/// Prefer AppText.Dutch / AppText.English and the ...In(text) overloads where a
/// test can avoid the global entirely; this exists for the cases that cannot,
/// because the slice itself reads Current.
/// </summary>
[CollectionDefinition(Name)]
public sealed class AppTextCollection
{
    public const string Name = "AppText";
}
