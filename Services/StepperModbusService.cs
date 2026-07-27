using System.IO.Ports;
using NModbus;
using NModbus.Serial;
using UnifiedLicGen.Models;

namespace UnifiedLicGen.Services;

public sealed class StepperModbusService : IDisposable
{
    internal const int TransportRetryCount = 1;
    internal static readonly TimeSpan CommandStatusSettleDelay = TimeSpan.FromMilliseconds(80);
    private const ushort StepperControl = 0;
    private const ushort StepperMode = 1;
    private const ushort StepperAbsoluteTarget = 2;
    private const ushort StepperRelativeTarget = 4;
    private const ushort StepperConfigStart = 6;
    private const ushort StepperStatus = 12;
    private const ushort StepperDriverConfig = 14;
    private const ushort StepperAdvancedConfigStart = 14;
    private const ushort StepperControlTrigger = 0x0001;
    private const ushort StepperControlHomeReset = 0x0002;
    private const ushort StepperControlStartHoming = 0x0004;
    private const ushort StepperControlJogPositive = 0x0008;
    private const ushort StepperControlJogNegative = 0x0010;
    private const ushort StepperBusyMask = 0x000B;
    private const ushort AsmHoldingPwmStart = 2;
    private const ushort AsmHoldingPwmCount = 9;
    private const ushort AsmHoldingControl = 4;
    private const ushort AsmHoldingLedBrightness = 9;
    private const ushort AsmHoldingFrequency = 10;
    private const ushort AsmControlBlower = 0x0001;
    private const ushort AsmControlLedUpdate = 0x0004;
    private const ushort AsmControlLedClear = 0x0008;
    private const ushort AsmPersistentControlMask = AsmControlBlower;
    private const ushort IdentityStart = 4;
    private const ushort IdentityCount = 46;
    private const ushort MetadataStart = 52;
    private const ushort MetadataCount = 7;
    private const ushort RuntimeInputStart = 0;
    private const ushort RuntimeInputCount = 4;
    private const ushort RuntimeHoldingStart = 12;
    private const ushort RuntimeHoldingCount = 2;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IModbusFactory _factory = new ModbusFactory();
    private SerialPort? _port;
    private IModbusSerialMaster? _master;
    private byte _slaveId;

    public byte AsmSlaveId { get; set; } = 1;
    public byte StepperSlaveId { get; set; } = 2;

    public bool IsConnected => _port?.IsOpen == true && _master is not null;

    public async Task OpenAsync(string portName, int baudRate, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DisconnectCore();
            var port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
            {
                Handshake = Handshake.None,
                ReadTimeout = 600,
                WriteTimeout = 600,
                DtrEnable = true,
                RtsEnable = true
            };
            port.Open();
            var master = _factory.CreateRtuMaster(new SerialPortAdapter(port));
            master.Transport.ReadTimeout = 600;
            master.Transport.WriteTimeout = 600;
            master.Transport.Retries = TransportRetryCount;
            _port = port;
            _master = master;
        }
        catch
        {
            DisconnectCore();
            throw;
        }
        finally { _gate.Release(); }
    }

    public async Task<StepperIdentity> ConnectAndReadIdentityAsync(
        string portName,
        int baudRate,
        byte slaveId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DisconnectCore();
            var port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
            {
                Handshake = Handshake.None,
                ReadTimeout = 1200,
                WriteTimeout = 1200,
                DtrEnable = true,
                RtsEnable = true
            };
            port.Open();

            var master = _factory.CreateRtuMaster(new SerialPortAdapter(port));
            master.Transport.ReadTimeout = 1200;
            master.Transport.WriteTimeout = 1200;
            master.Transport.Retries = 2;

            _port = port;
            _master = master;
            _slaveId = slaveId;

            var identity = await Task.Run(
                () => master.ReadInputRegisters(slaveId, IdentityStart, IdentityCount),
                cancellationToken).ConfigureAwait(false);
            var metadata = await Task.Run(
                () => master.ReadInputRegisters(slaveId, MetadataStart, MetadataCount),
                cancellationToken).ConfigureAwait(false);

            return StepperIdentity.Decode(identity, metadata);
        }
        catch
        {
            DisconnectCore();
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<StepperLiveStatus> ReadLiveStatusAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var master = _master ?? throw new InvalidOperationException("Stepper Modbus connection is not open.");
            return await Task.Run(() =>
            {
                var inputs = master.ReadInputRegisters(StepperSlaveId, RuntimeInputStart, RuntimeInputCount);
                var holding = master.ReadHoldingRegisters(StepperSlaveId, RuntimeHoldingStart, RuntimeHoldingCount);
                var discrete = master.ReadInputs(StepperSlaveId, 0, 4);
                return new StepperLiveStatus(
                    Combine(inputs[0], inputs[1]),
                    unchecked((int)Combine(inputs[2], inputs[3])),
                    holding[0],
                    holding[1],
                    discrete.ElementAtOrDefault(0),
                    discrete.ElementAtOrDefault(1),
                    discrete.ElementAtOrDefault(2),
                    discrete.ElementAtOrDefault(3));
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<StepperIdentity> ReadIdentityAsync(CancellationToken cancellationToken = default) =>
        ReadIdentityAsync(_slaveId, cancellationToken);

    public async Task<StepperIdentity> ReadIdentityAsync(byte slaveId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var master = _master ?? throw new InvalidOperationException("Stepper Modbus connection is not open.");
            var identity = await Task.Run(
                () => master.ReadInputRegisters(slaveId, IdentityStart, IdentityCount),
                cancellationToken).ConfigureAwait(false);
            var metadata = await Task.Run(
                () => master.ReadInputRegisters(slaveId, MetadataStart, MetadataCount),
                cancellationToken).ConfigureAwait(false);
            return StepperIdentity.Decode(identity, metadata);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ushort[]> ReadDriveRegistersAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var master = _master ?? throw new InvalidOperationException("Stepper Modbus connection is not open.");
            return await Task.Run(
                () => master.ReadHoldingRegisters(StepperSlaveId, 0, 19),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<StepperDriveConfiguration> ReadStepperConfigurationAsync(CancellationToken cancellationToken = default)
    {
        var registers = await ReadDriveRegistersAsync(cancellationToken).ConfigureAwait(false);
        return StepperDriveConfiguration.FromHoldingRegisters(registers);
    }

    public Task WriteStepperConfigurationAsync(StepperDriveConfiguration config, CancellationToken cancellationToken = default) =>
        ExecuteAsync(master =>
        {
            EnsureStepperIdle(master, "configuration write");
            WriteAndVerifyRegisters(master, StepperConfigStart,
                new[] { config.Microstep, config.PulsesPerRevolution, config.Acceleration, config.Deceleration, config.Velocity, config.JogChunk }, "drive setup");
            WriteAndVerifyRegisters(master, StepperAdvancedConfigStart,
                new[] { (ushort)(config.ConfigBits & 0x000F), config.HomeChunk, config.DeadbandChunk, config.HomingSpeed, config.DeadbandSpeed }, "advanced drive setup");
        }, cancellationToken);

    public Task MoveStepperRelativeAsync(int pulses, CancellationToken cancellationToken = default) => ExecuteAsync(master =>
    {
        EnsureStepperIdle(master, "relative move"); Split(unchecked((uint)pulses), out var low, out var high);
        WriteAndVerifyRegisters(master, StepperMode, new ushort[] { 0 }, "relative mode");
        WriteAndVerifyRegisters(master, StepperRelativeTarget, new[] { low, high }, "relative target");
        master.WriteSingleRegister(StepperSlaveId, StepperControl, StepperControlTrigger);
    }, cancellationToken);

    public Task MoveStepperAbsoluteAsync(uint pulses, CancellationToken cancellationToken = default) => ExecuteAsync(master =>
    {
        EnsureStepperIdle(master, "absolute move"); Split(pulses, out var low, out var high);
        WriteAndVerifyRegisters(master, StepperMode, new ushort[] { 1 }, "absolute mode");
        WriteAndVerifyRegisters(master, StepperAbsoluteTarget, new[] { low, high }, "absolute target");
        master.WriteSingleRegister(StepperSlaveId, StepperControl, StepperControlTrigger);
    }, cancellationToken);

    public Task JogStepperAsync(ushort jogChunk, bool positive, CancellationToken cancellationToken = default) => ExecuteAsync(master =>
    {
        EnsureStepperIdle(master, positive ? "positive jog" : "negative jog");
        WriteAndVerifyRegisters(master, 11, new[] { jogChunk }, "jog chunk");
        master.WriteSingleRegister(StepperSlaveId, StepperControl, positive ? StepperControlJogPositive : StepperControlJogNegative);
    }, cancellationToken);

    public Task ResetStepperPositionAsync(CancellationToken cancellationToken = default) => StepperCommandAsync(StepperControlHomeReset, "position reset", cancellationToken);
    public Task StartStepperHomingAsync(CancellationToken cancellationToken = default) => StepperCommandAsync(StepperControlStartHoming, "homing", cancellationToken);

    public async Task<AsmLiveState> ReadAsmStateAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var master = RequireMaster();
            return await Task.Run(() =>
            {
                var inputs = master.ReadInputRegisters(AsmSlaveId, 0, 4);
                var timer = master.ReadInputRegisters(AsmSlaveId, 50, 2);
                var holding = master.ReadHoldingRegisters(AsmSlaveId, 0, 13);
                var pedals = master.ReadCoils(AsmSlaveId, 0, 2);
                var outputs = DecodeAsmOutputs(holding.Skip(AsmHoldingPwmStart).Take(AsmHoldingPwmCount).ToArray());
                return new AsmLiveState(inputs[0], inputs[1], inputs[2], inputs[3], timer[0], timer[1],
                    pedals.ElementAtOrDefault(0), pedals.ElementAtOrDefault(1), holding[11], holding[12], outputs);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ushort> ReadAsmInputStatusAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var master = RequireMaster();
            return await Task.Run(() => master.ReadInputRegisters(AsmSlaveId, 0, 1)[0], cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public Task WriteAsmPwmAsync(ushort pwm1, ushort pwm2, ushort frequencyHz, CancellationToken cancellationToken = default)
    {
        if (pwm1 > 1023 || pwm2 > 1023) throw new ArgumentOutOfRangeException(nameof(pwm1), "PWM duty must be 0..1023.");
        if (frequencyHz is < 1 or > 11718) throw new ArgumentOutOfRangeException(nameof(frequencyHz), "PWM frequency must be 1..11718 Hz.");
        return ExecuteAsync(master =>
        {
            master.WriteMultipleRegisters(AsmSlaveId, AsmHoldingPwmStart, new[] { pwm1, pwm2 });
            master.WriteSingleRegister(AsmSlaveId, AsmHoldingFrequency, frequencyHz);
        }, cancellationToken);
    }

    public Task WriteAsmOutputsAsync(bool blowerOn, CancellationToken cancellationToken = default) =>
        ExecuteAsync(master =>
        {
            var control = ComposeAsmPersistentControl(ReadAsmPersistentControl(master), blowerOn);
            master.WriteSingleRegister(AsmSlaveId, AsmHoldingControl, control);
        }, cancellationToken);

    public Task UpdateAsmLedAsync(ushort address, ushort red, ushort green, ushort blue, CancellationToken cancellationToken = default)
    {
        ValidateAsmLed(address, red, green, blue);
        return ExecuteAsync(master => master.WriteMultipleRegisters(AsmSlaveId, AsmHoldingControl,
            new[] { (ushort)(ReadAsmPersistentControl(master) | AsmControlLedUpdate), address, red, green, blue }), cancellationToken);
    }

    public async Task UpdateAllAsmLedsAsync(ushort red, ushort green, ushort blue, CancellationToken cancellationToken = default)
    {
        ValidateAsmLed(0, red, green, blue);
        for (ushort address = 0; address <= 7; address++)
            await UpdateAsmLedAsync(address, red, green, blue, cancellationToken).ConfigureAwait(false);
    }

    public Task UpdateAsmLedFrameAsync(IReadOnlyList<(ushort R, ushort G, ushort B)> colors, CancellationToken cancellationToken = default)
    {
        if (colors.Count != 8) throw new ArgumentException("A WS2812 frame must contain exactly 8 colors.", nameof(colors));
        foreach (var color in colors) ValidateAsmLed(0, color.R, color.G, color.B);
        return ExecuteAsync(master =>
        {
            var control = (ushort)(ReadAsmPersistentControl(master) | AsmControlLedUpdate);
            for (ushort address = 0; address < colors.Count; address++)
            {
                var color = colors[address];
                master.WriteMultipleRegisters(AsmSlaveId, AsmHoldingControl, new[] { control, address, color.R, color.G, color.B });
            }
        }, cancellationToken);
    }

    public Task SetAsmBrightnessAsync(ushort brightness, CancellationToken cancellationToken = default)
    {
        if (brightness > 255) throw new ArgumentOutOfRangeException(nameof(brightness), "Brightness must be 0..255.");
        return ExecuteAsync(master =>
        {
            var refreshControl = ComposeAsmBrightnessRefreshControl(ReadAsmPersistentControl(master));
            master.WriteSingleRegister(AsmSlaveId, AsmHoldingLedBrightness, brightness);
            master.WriteSingleRegister(AsmSlaveId, AsmHoldingControl, refreshControl);
        }, cancellationToken);
    }

    public Task ClearAsmLedsAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(master => master.WriteSingleRegister(AsmSlaveId, AsmHoldingControl,
            (ushort)(ReadAsmPersistentControl(master) | AsmControlLedClear)), cancellationToken);

    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            DisconnectCore();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Wait();
        try { DisconnectCore(); }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private void DisconnectCore()
    {
        if (_master is IDisposable disposable)
        {
            disposable.Dispose();
        }
        _master = null;

        if (_port is not null)
        {
            if (_port.IsOpen) _port.Close();
            _port.Dispose();
            _port = null;
        }
    }

    private static uint Combine(ushort low, ushort high) => ((uint)high << 16) | low;

    private IModbusSerialMaster RequireMaster() => _master ?? throw new InvalidOperationException("Modbus connection is not open.");

    private async Task ExecuteAsync(Action<IModbusSerialMaster> operation, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await Task.Run(() => operation(RequireMaster()), cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private ushort ReadAsmPersistentControl(IModbusSerialMaster master) =>
        (ushort)(master.ReadHoldingRegisters(AsmSlaveId, AsmHoldingControl, 1)[0] & AsmPersistentControlMask);

    internal static ushort ComposeAsmPersistentControl(ushort currentControl, bool blowerOn)
    {
        var control = (ushort)(currentControl & AsmPersistentControlMask);
        return blowerOn ? (ushort)(control | AsmControlBlower) : (ushort)(control & ~AsmControlBlower);
    }

    internal static ushort ComposeAsmBrightnessRefreshControl(ushort currentControl) =>
        (ushort)((currentControl & AsmPersistentControlMask) | AsmControlLedUpdate);

    private static AsmOutputState DecodeAsmOutputs(ushort[] r)
    {
        if (r.Length != AsmHoldingPwmCount) throw new InvalidDataException("ASM holding-register response was incomplete.");
        return new AsmOutputState(r[0], r[1], r[2], r[3], r[4], r[5], r[6], r[7], r[8]);
    }

    private static void ValidateAsmLed(ushort address, ushort red, ushort green, ushort blue)
    {
        if (address > 7) throw new ArgumentOutOfRangeException(nameof(address), "LED address must be 0..7.");
        if (red > 255 || green > 255 || blue > 255) throw new ArgumentOutOfRangeException(nameof(red), "LED channels must be 0..255.");
    }

    private Task StepperCommandAsync(ushort command, string name, CancellationToken cancellationToken) => ExecuteAsync(master =>
    { EnsureStepperIdle(master, name); master.WriteSingleRegister(StepperSlaveId, StepperControl, command); }, cancellationToken);

    private void EnsureStepperIdle(IModbusSerialMaster master, string operation)
    {
        var status = master.ReadHoldingRegisters(StepperSlaveId, StepperStatus, 1)[0];
        if ((status & StepperBusyMask) != 0) throw new InvalidOperationException($"Drive is busy; refused {operation}. Status=0x{status:X4}.");
    }

    private void WriteAndVerifyRegisters(IModbusSerialMaster master, ushort start, ushort[] values, string operation)
    {
        if (values.Length == 1) master.WriteSingleRegister(StepperSlaveId, start, values[0]);
        else master.WriteMultipleRegisters(StepperSlaveId, start, values);
        var readback = master.ReadHoldingRegisters(StepperSlaveId, start, (ushort)values.Length);
        if (!readback.SequenceEqual(values))
            throw new IOException($"Stepper {operation} register readback mismatch at HR {start}.");
    }

    private static void Split(uint value, out ushort low, out ushort high)
    { low = (ushort)(value & 0xFFFF); high = (ushort)(value >> 16); }
}
