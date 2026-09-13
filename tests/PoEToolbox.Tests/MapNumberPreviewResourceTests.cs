using System.Collections;
using System.Resources;
using Xunit;

namespace PoEToolbox.Tests;

/// <summary>
/// The map-number module previews from a texture embedded in the plugin assembly, so entering it
/// never opens the client. A broken <c>&lt;Resource&gt;</c> link would not fail the build — it would
/// silently fall back to reading the client — so the asset is asserted here.
/// </summary>
public sealed class MapNumberPreviewResourceTests
{
    private const string WpfResourceStream = "PoEToolbox.Plugins.DataBrowser.g.resources";

    [Fact]
    public void MapNumberView_EmbedsThePreviewCanvasTemplate()
    {
        var keys = GetWpfResourceKeys();

        Assert.Contains("assets/mapnumber-preview.dds", keys);
    }

    [Fact]
    public void MapNumberView_EmbedsThePoe2BackgroundTexture()
    {
        // The apply step draws the PoE2 numbers over this background; it has to ship as well.
        Assert.Contains("assets/mapbackground.png", GetWpfResourceKeys());
    }

    private static List<string> GetWpfResourceKeys()
    {
        var assembly = typeof(PoEToolbox.Plugins.DataBrowser.MapNumberView).Assembly;
        using var stream = assembly.GetManifestResourceStream(WpfResourceStream);
        Assert.NotNull(stream);

        using var reader = new ResourceReader(stream);
        return reader.Cast<DictionaryEntry>().Select(entry => (string)entry.Key).ToList();
    }
}
