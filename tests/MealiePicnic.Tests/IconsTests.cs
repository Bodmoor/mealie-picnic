namespace MealiePicnic.Tests;

/// <summary>
/// The icons are EmbeddedResource items whose resource names must match what
/// Icons.Load() asks for. Nothing else catches a rename or a moved folder: the app
/// builds fine and then throws the first time a browser requests the favicon.
/// </summary>
public class IconsTests
{
    public static TheoryData<string, byte[], int> Icons => new()
    {
        { "192", Presentation.Icons.Png192, 192 },
        { "512", Presentation.Icons.Png512, 512 },
        { "maskable-512", Presentation.Icons.PngMaskable512, 512 },
    };

    [Theory]
    [MemberData(nameof(Icons))]
    public void Icon_is_a_png_of_the_expected_size(string name, byte[] bytes, int expected)
    {
        // 'name' is carried into every failure message: with three cases that
        // differ only by size, "expected 512, got 192" would not say which asset
        // is wrong. (It also keeps xUnit1026 happy under -warnaserror.)
        Assert.True(bytes.Length > 0, $"icon-{name}.png resolved to an empty resource");

        Assert.True(
            bytes.Take(8).SequenceEqual(
                new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
            $"icon-{name}.png is not a PNG (bad signature)");

        // IHDR carries width and height as big-endian uint32 at offsets 16 and 20.
        var width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
        var height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];

        Assert.True(width == expected && height == expected,
            $"icon-{name}.png is {width}x{height}, expected {expected}x{expected}");
    }

    [Fact]
    public void Eu_organic_mark_is_a_usable_svg()
    {
        // Served as image/svg+xml from /icons, so it has to parse as SVG and carry
        // the twelve stars of the EU mark -- a truncated file would render blank.
        var svg = System.Text.Encoding.UTF8.GetString(Presentation.Icons.EuOrganicSvg);

        Assert.StartsWith("<svg", svg);
        Assert.EndsWith("</svg>", svg.Trim());
        Assert.Equal(12, System.Text.RegularExpressions.Regex.Matches(svg, "<polygon").Count);
    }

    [Fact]
    public void Manifest_theme_colour_matches_the_icon_background()
    {
        // Android tints the task switcher with theme_color; a mismatch against the
        // icon's Mealie orange looks like a rendering bug.
        Assert.Contains("\"theme_color\": \"#E58325\"", Presentation.Icons.Manifest);
    }
}
