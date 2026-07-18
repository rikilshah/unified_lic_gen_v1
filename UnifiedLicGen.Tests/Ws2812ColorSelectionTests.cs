using Windows.UI;
using Xunit;

namespace UnifiedLicGen.Tests;

public sealed class Ws2812ColorSelectionTests
{
    [Fact]
    public void PickerColor_MapsDirectlyToWs2812Channels()
    {
        var selected = Color.FromArgb(255, 17, 93, 241);

        var channels = MainWindow.Ws2812Channels(selected);

        Assert.Equal((ushort)17, channels.R);
        Assert.Equal((ushort)93, channels.G);
        Assert.Equal((ushort)241, channels.B);
    }
}
