using Xunit;

namespace UnifiedLicGen.Tests;

public sealed class WindowTitleFormatterTests
{
    [Theory]
    [InlineData(null, null, "◆ Unified Test & Keygen Dashboard  v2.0.0")]
    [InlineData("A26050605", null, "◆ Unified Test & Keygen Dashboard  v2.0.0  —  ASM A26050605")]
    [InlineData(null, "S26050606", "◆ Unified Test & Keygen Dashboard  v2.0.0  —  Stepper S26050606")]
    [InlineData("A26050605", "S26050606", "◆ Unified Test & Keygen Dashboard  v2.0.0  —  ASM A26050605  |  Stepper S26050606")]
    public void FormatWindowTitle_ListsEveryDetectedCardSerial(
        string? asmSerial,
        string? stepperSerial,
        string expected)
    {
        Assert.Equal(expected, MainWindow.FormatWindowTitle("2.0.0", asmSerial, stepperSerial));
    }
}
