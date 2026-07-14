using System.IO.Ports;
using NModbus;
using NModbus.Serial;
using UnifiedLicGen.Models;

namespace UnifiedLicGen.Services;

public sealed class StepperModbusService : IDisposable
{
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

    public bool IsConnected => _port?.IsOpen == true && _master is not null;

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
                var inputs = master.ReadInputRegisters(_slaveId, RuntimeInputStart, RuntimeInputCount);
                var holding = master.ReadHoldingRegisters(_slaveId, RuntimeHoldingStart, RuntimeHoldingCount);
                var discrete = master.ReadInputs(_slaveId, 0, 4);
                return new StepperLiveStatus(
                    Combine(inputs[0], inputs[1]),
                    Combine(inputs[2], inputs[3]),
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

    public async Task<StepperIdentity> ReadIdentityAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var master = _master ?? throw new InvalidOperationException("Stepper Modbus connection is not open.");
            var identity = await Task.Run(
                () => master.ReadInputRegisters(_slaveId, IdentityStart, IdentityCount),
                cancellationToken).ConfigureAwait(false);
            var metadata = await Task.Run(
                () => master.ReadInputRegisters(_slaveId, MetadataStart, MetadataCount),
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
                () => master.ReadHoldingRegisters(_slaveId, 0, 19),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

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
        DisconnectCore();
        _gate.Dispose();
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
}
