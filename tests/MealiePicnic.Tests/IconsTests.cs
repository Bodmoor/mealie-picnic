namespace MealiePicnic.Tests;

/// <summary>
/// The icons are EmbeddedResource items whose LogicalName in the csproj must match
/// what Icons.Load() asks for. Nothing else catches a rename: the app would build
/// fine and then throw the first time a browser requests the favicon.
/// </summary>
public class IconsTests
{
    public static TheoryData<string, byte[], int> Icons => new()
    {
        { "192", MealiePicnic.Icons.Png192, 192 },
        { "512", MealiePicnic.Icons.Png512, 512 },
        { "maskable-512", MealiePicnic.Icons.PngMaskable512, 512 },
    };

    [Theory]
    [MemberData(nameof(Icons))]
    public void Icon_is_a_png_of_the_expected_size(string name, byte[] bytes, int expected)
    {
        Assert.NotEmpty(bytes);

        // PNG signature.
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A },
            bytes.Take(8).ToArray());

        // IHDR carries width and height as big-endian uint32 at offsets 16 and 20.
        var width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
        var height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];

        Assert.Equal(expected, width);
        Assert.Equal(expected, height);
    }

    [Fact]
    public void Manifest_theme_colour_matches_the_icon_background()
    {
        // Android tints the task switcher with theme_color; a mismatch against the
        // icon's Mealie orange looks like a bug.
        Assert.Contains("\"theme_color\": \"#E58325\"", MealiePicnic.Icons.Manifest);
    }
}
