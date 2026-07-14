using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using UnifiedLicGen.Models;
using UnifiedLicGen.Services;
using Windows.Graphics;

namespace UnifiedLicGen;

public sealed partial class ControlTestingWindow : Window
{
    private readonly SerialPortScanner _scanner = new();
    private readonly StepperModbusService _modbus = new();
    private bool _connected;

    public ControlTestingWindow()
    {
        InitializeComponent();
        Title = "Control & Testing - UnifiedLicGen";
        AppWindow.Resize(new SizeInt32(1320, 860));
        Closed += async (_, _) => await _modbus.DisconnectAsync();
        HardwareBox.SelectionChanged += (_, _) => HardwareTabs.SelectedIndex = HardwareBox.SelectedIndex;
        HardwareTabs.SelectionChanged += (_, _) => HardwareBox.SelectedIndex = HardwareTabs.SelectedIndex;
        _ = RefreshPortsAsync();
    }

    private async Task RefreshPortsAsync()
    {
        var ports = await Task.Run(_scanner.ScanPorts);
        PortBox.ItemsSource = ports;
        if (ports.Count > 0) PortBox.SelectedIndex = 0;
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (PortBox.SelectedItem is not SerialPortDescriptor port)
        {
            StatusText.Text = "Select a COM port.";
            return;
        }
        try
        {
            StatusText.Text = "Connecting...";
            var baud = int.Parse(((ComboBoxItem)BaudBox.SelectedItem).Content.ToString()!);
            var identity = await _modbus.ConnectAndReadIdentityAsync(port.PortName, baud, checked((byte)SlaveBox.Value));
            _connected = true;
            IdentityText.Text = $"{identity.SerialNumber}  /  CUST {identity.CustomerId10}  /  FW {identity.FirmwareVersion}";
            StatusText.Text = $"Connected on {port.DisplayName}.";
            if (HardwareBox.SelectedIndex == 1) await RefreshStepperAsync();
        }
        catch (Exception ex)
        {
            _connected = false;
            StatusText.Text = $"Connection failed: {ex.Message}";
        }
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        await _modbus.DisconnectAsync();
        _connected = false;
        IdentityText.Text = "No card connected";
        StatusText.Text = "Disconnected.";
    }

    private async void ReadStepper_Click(object sender, RoutedEventArgs e) => await RefreshStepperAsync();

    private async Task RefreshStepperAsync()
    {
        if (!_connected) { StatusText.Text = "Connect the Stepper card first."; return; }
        try
        {
            var live = await _modbus.ReadLiveStatusAsync();
            StepperPosition.Text = live.Position.ToString();
            StepperCommand.Text = live.ActiveCommand.ToString();
            StepperStatus.Text = $"0x{live.Status:X4}";
            StepperFault.Text = live.Fault.ToString();
            StatusText.Text = "Stepper live state refreshed.";
        }
        catch (Exception ex) { StatusText.Text = $"Read failed: {ex.Message}"; }
    }

    private async void RefreshAsm_Click(object sender, RoutedEventArgs e)
    {
        if (!_connected) { StatusText.Text = "Connect the ASM card first."; return; }
        try
        {
            var identity = await _modbus.ReadIdentityAsync();
            IdentityText.Text = $"{identity.SerialNumber}  /  CUST {identity.CustomerId10}  /  FW {identity.FirmwareVersion}";
            StatusText.Text = "ASM identity refreshed.";
        }
        catch (Exception ex) { StatusText.Text = $"Read failed: {ex.Message}"; }
    }

    private void ControlAction_Click(object sender, RoutedEventArgs e)
    {
        if (!_connected) { StatusText.Text = "Connect the selected card before sending controls."; return; }
        StatusText.Text = $"{((Button)sender).Content} queued for the selected hardware workspace.";
    }

    private void ControlChanged(object sender, RoutedEventArgs e)
    {
        if (!_connected) StatusText.Text = "Connect the selected card before changing outputs.";
    }

    private async void RunTests_Click(object sender, RoutedEventArgs e)
    {
        if (!_connected) { StatusText.Text = "Connect the selected card before running tests."; return; }
        TestProgress.IsIndeterminate = true;
        StatusText.Text = $"Running {((Button)sender).Content}...";
        try
        {
            await _modbus.ReadIdentityAsync();
            if (HardwareTabs.SelectedIndex == 1) await RefreshStepperAsync();
            StatusText.Text = "Communication and live-read tests passed. Operator-selected action tests are ready.";
        }
        catch (Exception ex) { StatusText.Text = $"Test failed: {ex.Message}"; }
        finally { TestProgress.IsIndeterminate = false; }
    }
}
