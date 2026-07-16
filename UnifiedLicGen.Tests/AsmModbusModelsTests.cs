using UnifiedLicGen.Models;
using Xunit;

namespace UnifiedLicGen.Tests;

public sealed class AsmModbusModelsTests
{
    [Fact]
    public void LiveState_DecodesEveryStatusBit()
    {
        var outputs = new AsmOutputState(10, 20, 3, 4, 5, 6, 7, 8, 1000);
        var state = new AsmLiveState(0x003F, 10, 20, 1000, 63, 1, true, true, 2, 4, outputs);

        Assert.True(state.BlowerOn);
        Assert.True(state.OnboardLedOn);
        Assert.True(state.Ws2812Busy);
        Assert.True(state.Ip1Active);
        Assert.True(state.Ip2Active);
        Assert.True(state.CommunicationError);
    }

    [Fact]
    public void LiveState_LeavesStatusClearWhenRegisterIsZero()
    {
        var state = new AsmLiveState(0, 0, 0, 0, 0, 0, false, false, 1, 0,
            new AsmOutputState(0, 0, 0, 0, 0, 0, 0, 0, 1000));

        Assert.False(state.BlowerOn);
        Assert.False(state.OnboardLedOn);
        Assert.False(state.Ws2812Busy);
        Assert.False(state.Ip1Active);
        Assert.False(state.Ip2Active);
        Assert.False(state.CommunicationError);
    }
}
