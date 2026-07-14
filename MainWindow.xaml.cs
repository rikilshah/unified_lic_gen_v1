using System.Security.Cryptography;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using UnifiedLicGen.Models;
using UnifiedLicGen.Services;
using KeyGeneratorUi.Services;
using Windows.Storage.Pickers;
using Windows.Graphics;

namespace UnifiedLicGen;

public sealed partial class MainWindow : Window
{
    private readonly Dictionary<string, UIElement> _pages = new();
    private bool _connected;
    private bool _authorized;
    private string _deviceProfile = "none";
    private string? _rawPublicKey;
    private string? _sec1PublicKey;
    private string? _fingerprint;
    private GeneratedKeyPackage? _generatedKeyPackage;
    private readonly SerialPortScanner _serialPortScanner = new();
    private readonly StepperModbusService _stepperModbus = new();
    private readonly CdiStorageService _cdiStorage = new();
    private StepperIdentity? _stepperIdentity;
    private CardIdentityCdi? _currentCdi;
    private string? _currentCdiPath;
    private readonly CardManifestService _manifestService = new();
    private CardManifest? _loadedManifest;
    private string? _loadedManifestPath;
    private readonly FirmwareProvisioningService _firmwareProvisioning = new();
    private bool _firmwarePrepared;
    private bool _firmwareBuilt;
    private bool _stLinkReady;

    public MainWindow()
    {
        InitializeComponent();
        Title = "Unified Test & Keygen Dashboard";
        AppWindow.Resize(new SizeInt32(1440, 920));
        BuildPages();
        Navigation.SelectedItem = Navigation.MenuItems[0];
        ShowPage("overview");
        PositionSettingsDrawer();
        SizeChanged += (_, _) => PositionSettingsDrawer();
        Navigation.Loaded += async (_, _) => await RefreshPortsAsync(showStatus: false);
    }

    private void BuildPages()
    {
        _pages["overview"] = BuildOverviewPage();
        _pages["identity"] = BuildIdentityPage();
        _pages["keygen"] = BuildKeygenPage();
        _pages["authorization"] = BuildAuthorizationPage();
        _pages["tests"] = BuildTestsPage();
        _pages["firmware"] = BuildFirmwarePage();
        _pages["asm"] = BuildAsmPage();
        _pages["stepper"] = BuildStepperPage();
        _pages["reports"] = BuildReportsPage();
    }

    private UIElement BuildOverviewPage()
    {
        var root = Page("Overview", "Connect, provision, authorize and test either supported hardware family from one workspace.");
        var actions = Columns(3);
        actions.Children.Add(MetricCard("1", "Connect & identify", "Select a card profile and establish the Modbus RTU session.", "Open settings", SettingsButton_Click));
        actions.Children.Add(MetricCard("2", "Provision & authorize", "Generate or load the public manifest and verify four identity checks.", "Open Keygen", (_, _) => SelectNavigation("keygen")));
        actions.Children.Add(MetricCard("3", "Test & report", "Run the card-specific functional suite and retain evidence.", "Open test center", (_, _) => SelectNavigation("tests")));
        root.Children.Add(actions);

        var status = Card("Current session");
        status.Children.Add(KeyValue("Connection", "Disconnected"));
        status.Children.Add(KeyValue("Hardware", "Not detected"));
        status.Children.Add(KeyValue("Authorization", "Required before control writes"));
        status.Children.Add(KeyValue("Test run", "Not started"));
        root.Children.Add(status);

        var notice = new InfoBar { IsOpen = true, Severity = InfoBarSeverity.Informational, Title = "Shared SOP, separate hardware", Message = "Identity, P-256 provisioning and authorization are shared. ASM and Stepper commands remain isolated behind their own validated register maps." };
        root.Children.Add(notice);
        return root;
    }

    private UIElement BuildIdentityPage()
    {
        var root = Page("Device identity", "Read-only card metadata used by provisioning and authorization.");
        var summary = Columns(3);
        summary.Children.Add(ValueCard("SERIAL NUMBER", "—", "Public card identifier", "IdentitySerial"));
        summary.Children.Add(ValueCard("FIRMWARE", "—", "Major.minor", "IdentityFirmware"));
        summary.Children.Add(ValueCard("PRODUCT", "—", "Detected hardware module", "IdentityProduct"));
        root.Children.Add(summary);

        var card = Card("Normalized identity");
        var uid = ReadOnlyField("STM32 UID / DEVID", "Connect to read card UID"); uid.Name = "IdentityUid"; card.Children.Add(uid);
        var customer = ReadOnlyField("Customer ID", "Connect to read customer ID"); customer.Name = "IdentityCustomer"; card.Children.Add(customer);
        var fingerprint = ReadOnlyField("Public-key fingerprint (SHA-256)", "Connect to read card fingerprint"); fingerprint.Name = "IdentityFingerprint"; card.Children.Add(fingerprint);
        var rawKey = ReadOnlyField("Raw P-256 public key (X || Y)", "Connect to read 64-byte key"); rawKey.Name = "IdentityRawKey"; card.Children.Add(rawKey);
        var buttons = Row();
        buttons.Children.Add(ActionButton("Read identity", async (_, _) => await RefreshStepperIdentityAsync(), true));
        buttons.Children.Add(ActionButton("Save CDI to database", async (_, _) => await SaveCurrentCdiAsync()));
        buttons.Children.Add(ActionButton("Copy fingerprint", (_, _) => SetStatus("Fingerprint copied when available.")));
        card.Children.Add(buttons);
        root.Children.Add(card);
        return root;
    }

    private UIElement BuildKeygenPage()
    {
        var root = Page("Keygen & provisioning", "The same three-step provisioning SOP is used for both hardware models.");
        var steps = RowWith(
            StepChip("1", "IDENTITY", true),
            new FontIcon { Glyph = "\uE76C", Foreground = Brush("TextSecondaryBrush"), VerticalAlignment = VerticalAlignment.Center },
            StepChip("2", "P-256 KEY", false),
            new FontIcon { Glyph = "\uE76C", Foreground = Brush("TextSecondaryBrush"), VerticalAlignment = VerticalAlignment.Center },
            StepChip("3", "EXPORT PACKAGE", false));
        root.Children.Add(steps);

        var identity = Card("1. Card identity");
        var identityGrid = Columns(3);
        var serial = Input("Serial number", "A26050601"); serial.Name = "KeySerial";
        var devid = Input("DEVID (24 hex)", "123456789ABCDEF087654321"); devid.Name = "KeyDeviceId";
        var custid = Input("Customer ID (10 digits)", "9428577894"); custid.Name = "KeyCustomerId";
        identityGrid.Children.Add(serial); identityGrid.Children.Add(devid); identityGrid.Children.Add(custid);
        identity.Children.Add(identityGrid);
        identity.Children.Add(ActionButton("Import CDI JSON", (_, _) => SetStatus("Use imported CDI values or the identity read from the connected card.")));
        root.Children.Add(identity);

        var keys = Card("2. Generate card key");
        var keyActions = Row();
        keyActions.Children.Add(ActionButton("Generate P-256 key pair", (_, _) => GenerateKey(), true));
        keyActions.Children.Add(ActionButton("Regenerate", (_, _) => GenerateKey()));
        keys.Children.Add(keyActions);
        var raw = ReadOnlyField("Raw public key — 64 bytes, X || Y", "Not generated"); raw.Name = "RawKeyOutput";
        var sec1 = ReadOnlyField("SEC1 public key — 04 || X || Y", "Not generated"); sec1.Name = "Sec1KeyOutput";
        var fp = ReadOnlyField("SHA-256 fingerprint", "Not generated"); fp.Name = "FingerprintOutput";
        keys.Children.Add(raw); keys.Children.Add(sec1); keys.Children.Add(fp);
        root.Children.Add(keys);

        var export = Card("3. Manifest package");
        var exportGrid = Columns(3);
        var product = Input("Product", "Stepper Control Card V2"); product.Name = "ExportProduct"; exportGrid.Children.Add(product);
        var variant = Input("Customer variant / batch", "PRODUCTION_BATCH"); variant.Name = "ExportVariant"; exportGrid.Children.Add(variant);
        var mode = new ComboBox { Header = "Mode", SelectedIndex = 0, Name = "ExportMode" }; mode.Items.Add("PRODUCTION"); mode.Items.Add("TEST"); exportGrid.Children.Add(mode);
        export.Children.Add(exportGrid);
        var advanced = new Expander { Header = "Advanced: private-key export" };
        advanced.Content = new StackPanel { Spacing = 10, Children = { new CheckBox { Name = "IncludePrivateKey", Content = "Export app signing private key — sensitive" }, new InfoBar { IsOpen = true, Severity = InfoBarSeverity.Warning, Title = "Sensitive credential", Message = "The private key is never placed in the manifest. Enable only for an intentional trusted-app export." } } };
        export.Children.Add(advanced);
        export.Children.Add(ActionButton("Export manifest package", async (_, _) => await ExportManifestPackageAsync(), true));
        root.Children.Add(export);
        return root;
    }

    private UIElement BuildAuthorizationPage()
    {
        var root = Page("Authorization", "Compare the trusted manifest with the connected card before enabling controls.");
        var load = Card("Provisioning manifest");
        var row = Row();
        row.Children.Add(ActionButton("Import manifest JSON", async (_, _) => await ImportManifestAsync(), true));
        row.Children.Add(new TextBlock { Name = "ManifestFileText", Text = "No manifest loaded", Foreground = Brush("TextSecondaryBrush"), VerticalAlignment = VerticalAlignment.Center });
        load.Children.Add(row); root.Children.Add(load);

        var checks = Card("Identity comparison");
        checks.Children.Add(CheckRow("Serial number", "Optional when present", "AuthSerial"));
        checks.Children.Add(CheckRow("STM32 UID / DEVID", "Required • 24 hexadecimal characters", "AuthDevice"));
        checks.Children.Add(CheckRow("Customer ID", "Required • 10 decimal digits", "AuthCustomer"));
        checks.Children.Add(CheckRow("Raw P-256 public key", "Required • 64 decoded bytes", "AuthKey"));
        checks.Children.Add(CheckRow("Public-key fingerprint", "Required • SHA-256(raw bytes)", "AuthFingerprint"));
        checks.Children.Add(CheckRow("Firmware / backend policy", "Enforced when configured", "AuthFirmware"));
        checks.Children.Add(ActionButton("Validate and authorize", async (_, _) => await AuthorizeAsync(), true));
        root.Children.Add(checks);
        root.Children.Add(new InfoBar { IsOpen = true, Severity = InfoBarSeverity.Warning, Title = "Fail closed", Message = "A missing, malformed, unreadable or mismatched required value keeps all device writes disabled." });
        return root;
    }

    private UIElement BuildTestsPage()
    {
        var root = Page("Test center", "Run repeatable hardware-specific tests with operator confirmation and evidence.");
        var run = Card("Test run");
        var controls = Row();
        controls.Children.Add(ActionButton("Run recommended", (_, _) => SetStatus("Recommended test run requires an identified and authorized card."), true));
        controls.Children.Add(ActionButton("Retry failed", (_, _) => SetStatus("No failed tests to retry.")));
        controls.Children.Add(ActionButton("Cancel run", (_, _) => SetStatus("Test cancellation requested.")));
        controls.Children.Add(ActionButton("Export report", (_, _) => SetStatus("No completed test report is available.")));
        run.Children.Add(controls);
        var progress = new ProgressBar { Value = 0, Maximum = 100, Height = 6 }; run.Children.Add(progress); root.Children.Add(run);

        var suites = Columns(2);
        suites.Children.Add(TestSuite("ASM I/O card", new[] { "Communication & identity", "Manifest authorization", "IP1 / IP2 status", "Blower command + readback", "PWM1 / PWM2 applied values", "WS2812 update + clear", "Preset round trip" }));
        suites.Children.Add(TestSuite("Stepper motion card", new[] { "Communication & identity", "Manifest authorization", "Input and fault status", "Configuration read/write/readback", "Relative and absolute move", "Positive / negative jog", "Home reset and homing", "Busy/conflict safety guard" }));
        root.Children.Add(suites);
        return root;
    }

    private UIElement BuildFirmwarePage()
    {
        var root = Page("Firmware provisioning", "Stage card identity, clean-build firmware, flash through ST-LINK, and verify Modbus readback.");
        root.Children.Add(new InfoBar
        {
            IsOpen = true,
            Severity = InfoBarSeverity.Warning,
            Title = "Live-device operation",
            Message = "Flashing replaces MCU program flash. RDP changes and automatic unlock are never performed. Confirm the physical target and exact serial before continuing."
        });

        var target = Card("Target and prerequisites");
        target.Children.Add(KeyValue("Firmware source", FirmwareProvisioningService.DefaultFirmwareRoot));
        target.Children.Add(KeyValue("Build output", _firmwareProvisioning.ElfPath));
        target.Children.Add(KeyValue("Programmer", FirmwareProvisioningService.DefaultProgrammerPath));
        target.Children.Add(KeyValue("Connected identity", "Generated CDI and manifest package required"));
        root.Children.Add(target);

        var stages = Columns(3);
        var prepare = Card("1. Prepare identity headers");
        prepare.Children.Add(new TextBlock { Text = "Back up existing headers, then stage card_public_key.h, customer ID, and serial number from the current card package.", TextWrapping = TextWrapping.Wrap, Foreground = Brush("TextSecondaryBrush") });
        prepare.Children.Add(ActionButton("Prepare headers", async (_, _) => await PrepareFirmwareAsync(), true));
        stages.Children.Add(new Border { Style = (Style)Application.Current.Resources["CardStyle"], Child = prepare });

        var build = Card("2. Clean build");
        build.Children.Add(new TextBlock { Text = "Run the MinSizeRel CMake clean build and require a new STEPPER_CONTROL_CARD_V2.elf.", TextWrapping = TextWrapping.Wrap, Foreground = Brush("TextSecondaryBrush") });
        build.Children.Add(ActionButton("Build firmware", async (_, _) => await BuildFirmwareAsync(), true));
        stages.Children.Add(new Border { Style = (Style)Application.Current.Resources["CardStyle"], Child = build });

        var probe = Card("3. Inspect ST-LINK");
        probe.Children.Add(new TextBlock { Text = "Read probe, target voltage, MCU, and option bytes. Protected RDP states stop the workflow.", TextWrapping = TextWrapping.Wrap, Foreground = Brush("TextSecondaryBrush") });
        probe.Children.Add(ActionButton("Detect target", async (_, _) => await ProbeTargetAsync(), true));
        stages.Children.Add(new Border { Style = (Style)Application.Current.Resources["CardStyle"], Child = probe });
        root.Children.Add(stages);

        var flash = Card("4. Flash and verify");
        flash.Children.Add(new CheckBox { Name = "FlashAcknowledge", Content = "I verified the physical ST-LINK target and understand that program flash will be replaced." });
        flash.Children.Add(new TextBox { Name = "FlashSerialConfirmation", Header = "Type the exact card serial number to enable flashing", PlaceholderText = "Example: S26050606" });
        flash.Children.Add(ActionButton("Flash firmware and verify card", async (_, _) => await FlashAndVerifyAsync(), true));
        root.Children.Add(flash);

        var log = new TextBox
        {
            Name = "FirmwareOperationLog",
            Header = "Provisioning log",
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 220,
            FontFamily = new FontFamily("Cascadia Mono"),
            Text = "No firmware operation started."
        };
        root.Children.Add(log);
        return root;
    }

    private UIElement BuildAsmPage()
    {
        var root = Page("ASM controls", "PWM, blower, digital input and WS2812 controls for the ASM I/O card.");
        root.Children.Add(AuthGate());
        var live = Columns(4);
        live.Children.Add(ValueCard("PWM1 APPLIED", "0", "0…1023")); live.Children.Add(ValueCard("PWM2 APPLIED", "0", "0…1023"));
        live.Children.Add(ValueCard("FREQUENCY", "0 Hz", "Applied value")); live.Children.Add(ValueCard("MODBUS", "Idle", "No errors")); root.Children.Add(live);

        var two = Columns(2);
        var pwm = Card("PWM outputs");
        var pwmGrid = Columns(3); pwmGrid.Children.Add(Number("PWM1 duty", 0, 0, 1023)); pwmGrid.Children.Add(Number("PWM2 duty", 0, 0, 1023)); pwmGrid.Children.Add(Number("Frequency (Hz)", 1000, 1, 65535)); pwm.Children.Add(pwmGrid);
        pwm.Children.Add(RowWith(ActionButton("Apply PWM", DeviceAction, true), ActionButton("Read back", DeviceAction)));
        two.Children.Add(pwm);
        var io = Card("Outputs & inputs"); io.Children.Add(new ToggleSwitch { Header = "Blower output", OffContent = "Off", OnContent = "On" });
        io.Children.Add(IndicatorRow("IP1", "Inactive", "IP2", "Inactive")); io.Children.Add(IndicatorRow("Board heartbeat", "Firmware-owned", "WS2812 DMA", "Idle")); two.Children.Add(io); root.Children.Add(two);

        var leds = Card("WS2812 lighting");
        var ledGrid = Columns(5); ledGrid.Children.Add(Number("LED address", 0, 0, 7)); ledGrid.Children.Add(Number("Red", 0, 0, 255)); ledGrid.Children.Add(Number("Green", 0, 0, 255)); ledGrid.Children.Add(Number("Blue", 0, 0, 255)); ledGrid.Children.Add(Number("Brightness", 128, 0, 255)); leds.Children.Add(ledGrid);
        leds.Children.Add(RowWith(ActionButton("Update selected LED", DeviceAction, true), ActionButton("Clear all LEDs", DeviceAction), ActionButton("Read output registers", DeviceAction)));
        root.Children.Add(leds);
        var profiles = Card("Preset"); profiles.Children.Add(RowWith(ActionButton("Load config JSON", DeviceAction), ActionButton("Save config JSON", DeviceAction), ActionButton("Sync from device", DeviceAction))); root.Children.Add(profiles);
        return root;
    }

    private UIElement BuildStepperPage()
    {
        var root = Page("Stepper controls", "Motion, homing, input status and drive configuration for the Stepper card.");
        root.Children.Add(AuthGate());
        var live = Columns(4); live.Children.Add(ValueCard("POSITION", "0", "Unsigned pulses", "StepperPosition")); live.Children.Add(ValueCard("STATE", "Idle", "Drive state", "StepperState")); live.Children.Add(ValueCard("FAULT", "0", "No fault", "StepperFault")); live.Children.Add(ValueCard("ACTIVE COMMAND", "0", "Command ID", "StepperCommand")); root.Children.Add(live);
        var inputs = Card("Digital inputs"); inputs.Children.Add(IndicatorRow("IP1", "Inactive", "IP2", "Inactive")); inputs.Children.Add(IndicatorRow("ENC_Z", "Inactive", "ENC_Z raw", "Inactive")); root.Children.Add(inputs);

        var motion = Columns(2);
        var moves = Card("Positioning"); moves.Children.Add(Number("Relative target (signed pulses)", 1000, int.MinValue, int.MaxValue)); moves.Children.Add(ActionButton("Move relative", DeviceAction, true)); moves.Children.Add(Number("Absolute target (pulses)", 0, 0, uint.MaxValue)); moves.Children.Add(ActionButton("Move absolute", DeviceAction, true)); motion.Children.Add(moves);
        var manual = Card("Jog & home"); manual.Children.Add(Number("Jog step chunk", 100, 0, ushort.MaxValue)); manual.Children.Add(RowWith(ActionButton("Jog −", DeviceAction), ActionButton("Jog +", DeviceAction))); manual.Children.Add(RowWith(ActionButton("Reset position / home", DeviceAction), ActionButton("Start homing", DeviceAction, true))); motion.Children.Add(manual); root.Children.Add(motion);

        var config = Card("Drive configuration");
        var cfg = Columns(5); cfg.Children.Add(Number("Microstep", 16, 0, ushort.MaxValue)); cfg.Children.Add(Number("Pulses / rev", 3200, 0, ushort.MaxValue)); cfg.Children.Add(Number("Acceleration", 1000, 0, ushort.MaxValue)); cfg.Children.Add(Number("Deceleration", 1000, 0, ushort.MaxValue)); cfg.Children.Add(Number("Velocity", 2000, 0, ushort.MaxValue)); config.Children.Add(cfg);
        var advanced = new Expander { Header = "Homing, deadband and driver flags" };
        var advancedPanel = new StackPanel { Spacing = 12, Padding = new Thickness(0, 12, 0, 0) };
        var numbers = Columns(5); numbers.Children.Add(Number("Home chunk", 100, 0, ushort.MaxValue)); numbers.Children.Add(Number("Deadband chunk", 10, 0, ushort.MaxValue)); numbers.Children.Add(Number("Homing speed", 500, 0, ushort.MaxValue)); numbers.Children.Add(Number("Deadband speed", 100, 0, ushort.MaxValue)); numbers.Children.Add(Number("Jog chunk", 100, 0, ushort.MaxValue)); advancedPanel.Children.Add(numbers);
        advancedPanel.Children.Add(RowWith(new ToggleSwitch { Header = "Invert direction" }, new ToggleSwitch { Header = "Swap jog inputs" }, new ToggleSwitch { Header = "ENC_Z limit" }, new ToggleSwitch { Header = "ENC_Z active-high" })); advanced.Content = advancedPanel; config.Children.Add(advanced);
        config.Children.Add(RowWith(ActionButton("Read parameters", async (_, _) => await ReadStepperRegistersAsync()), ActionButton("Write parameters", DeviceAction, true), ActionButton("Read driver bits", DeviceAction), ActionButton("Write driver bits", DeviceAction), ActionButton("Load profile", DeviceAction), ActionButton("Save profile", DeviceAction))); root.Children.Add(config);
        return root;
    }

    private UIElement BuildReportsPage()
    {
        var root = Page("Reports & logs", "Audit provisioning, authorization, test evidence and communication events without storing secrets.");
        var filters = Card("Session history"); filters.Children.Add(RowWith(new ComboBox { Header = "Hardware", PlaceholderText = "All hardware", MinWidth = 180 }, new ComboBox { Header = "Result", PlaceholderText = "All results", MinWidth = 160 }, new CalendarDatePicker { Header = "From date" }, ActionButton("Refresh", (_, _) => SetStatus("Report history refreshed.")))); root.Children.Add(filters);
        var empty = Card("No completed runs"); empty.Children.Add(new TextBlock { Text = "Completed test sessions will show serial number, authorization method, app/module versions, measurements and pass/fail evidence here.", Foreground = Brush("TextSecondaryBrush"), TextWrapping = TextWrapping.Wrap });
        empty.Children.Add(RowWith(ActionButton("Export selected JSON", (_, _) => SetStatus("Select a report first.")), ActionButton("Export Markdown summary", (_, _) => SetStatus("Select a report first.")), ActionButton("Open log folder", (_, _) => SetStatus("Log folder requested.")))); root.Children.Add(empty);
        return root;
    }

    private StackPanel Page(string title, string subtitle)
    {
        var panel = new StackPanel { Spacing = 16, MaxWidth = 1260, HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["PageTitleStyle"] });
        panel.Children.Add(new TextBlock { Text = subtitle, Foreground = Brush("TextSecondaryBrush"), Margin = new Thickness(0, -12, 0, 4) });
        return panel;
    }

    private StackPanel Card(string title)
    {
        var content = new StackPanel { Spacing = 12, Background = Brush("SurfaceBrush"), Padding = new Thickness(16) };
        content.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["SectionTitleStyle"] });
        return content;
    }

    private Border ValueCard(string label, string value, string detail, string? valueName = null)
    {
        var valueText = new TextBlock { Text = value, FontFamily = new FontFamily("Cascadia Mono"), FontSize = 21, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        if (!string.IsNullOrWhiteSpace(valueName)) valueText.Name = valueName;
        return new Border
        {
            Style = (Style)Application.Current.Resources["CardStyle"], Margin = new Thickness(4),
            Child = new StackPanel { Spacing = 5, Children = { new TextBlock { Text = label, FontSize = 11, Foreground = Brush("TextSecondaryBrush") }, valueText, new TextBlock { Text = detail, FontSize = 12, Foreground = Brush("TextSecondaryBrush") } } }
        };
    }

    private Border MetricCard(string number, string title, string body, string action, RoutedEventHandler handler) => new()
    {
        Style = (Style)Application.Current.Resources["CardStyle"], Margin = new Thickness(4),
        Child = new StackPanel { Spacing = 10, Children = { new TextBlock { Text = number, FontSize = 12, Foreground = Brush("AccentBrush") }, new TextBlock { Text = title, FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }, new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, Foreground = Brush("TextSecondaryBrush"), MinHeight = 42 }, ActionButton(action, handler, true) } }
    };

    private Grid Columns(int count)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        for (var i = 0; i < count; i++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Loaded += (_, _) => { for (var i = 0; i < grid.Children.Count; i++) Grid.SetColumn((FrameworkElement)grid.Children[i], i % count); };
        return grid;
    }

    private StackPanel Row() => new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private StackPanel RowWith(params UIElement[] controls) { var row = Row(); foreach (var c in controls) row.Children.Add(c); return row; }
    private Button ActionButton(string text, RoutedEventHandler handler, bool primary = false) { var b = new Button { Content = text }; if (primary) b.Style = (Style)Application.Current.Resources["PrimaryButtonStyle"]; b.Click += handler; return b; }
    private TextBox Input(string header, string value) => new() { Header = header, Text = value };
    private NumberBox Number(string header, double value, double min, double max) => new() { Header = header, Value = value, Minimum = min, Maximum = max, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, MinWidth = 145 };
    private TextBox ReadOnlyField(string header, string value) => new() { Header = header, Text = value, IsReadOnly = true, FontFamily = new FontFamily("Cascadia Mono"), TextWrapping = TextWrapping.Wrap };
    private StackPanel KeyValue(string key, string value) => RowWith(new TextBlock { Text = key, Width = 180, Foreground = Brush("TextSecondaryBrush") }, new TextBlock { Text = value });
    private Border AuthGate() => new() { Background = new SolidColorBrush(ColorHelper.FromArgb(38, 244, 184, 96)), BorderBrush = Brush("WarningBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(12), Child = new TextBlock { Text = "Controls are gated by connection + manifest authorization. Settings remain available from the top bar.", Foreground = Brush("WarningBrush") } };
    private Grid CheckRow(string name, string rule, string stateName) { var g = new Grid { Padding = new Thickness(0, 8, 0, 8) }; g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) }); g.ColumnDefinitions.Add(new ColumnDefinition()); g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) }); g.Children.Add(new TextBlock { Text = name }); var r = new TextBlock { Text = rule, Foreground = Brush("TextSecondaryBrush") }; Grid.SetColumn(r, 1); g.Children.Add(r); var state = new TextBlock { Name = stateName, Text = "NOT CHECKED", Foreground = Brush("WarningBrush"), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right }; Grid.SetColumn(state, 2); g.Children.Add(state); return g; }
    private Border StepChip(string number, string label, bool active) => new() { Background = active ? new SolidColorBrush(ColorHelper.FromArgb(48, 57, 198, 212)) : Brush("RaisedBrush"), BorderBrush = active ? Brush("AccentBrush") : Brush("BorderBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16), Padding = new Thickness(12, 6, 12, 6), Child = new TextBlock { Text = $"{number}  {label}", FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = active ? Brush("AccentBrush") : Brush("TextSecondaryBrush") } };
    private Grid IndicatorRow(string a, string av, string b, string bv) { var g = Columns(2); g.Children.Add(KeyValue(a, av)); g.Children.Add(KeyValue(b, bv)); return g; }
    private Border TestSuite(string title, IEnumerable<string> tests) { var p = new StackPanel { Spacing = 9 }; p.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["SectionTitleStyle"] }); foreach (var test in tests) p.Children.Add(RowWith(new FontIcon { Glyph = "\uE73E", Foreground = Brush("TextSecondaryBrush") }, new TextBlock { Text = test })); return new Border { Style = (Style)Application.Current.Resources["CardStyle"], Margin = new Thickness(4), Child = p }; }
    private SolidColorBrush Brush(string key) => (SolidColorBrush)Application.Current.Resources[key];

    private void GenerateKey()
    {
        if (_currentCdi is null)
        {
            SetStatus("Read a card identity or import CDI JSON before generating a key.");
            return;
        }

        _generatedKeyPackage = KeyPackageService.GenerateKeyPackage();
        _rawPublicKey = _generatedKeyPackage.RawPublicKeyHex;
        _sec1PublicKey = _generatedKeyPackage.Sec1PublicKeyHex;
        _fingerprint = _generatedKeyPackage.FingerprintFullHex;
        FindNameInPages<TextBox>("RawKeyOutput")!.Text = _rawPublicKey;
        FindNameInPages<TextBox>("Sec1KeyOutput")!.Text = _sec1PublicKey;
        FindNameInPages<TextBox>("FingerprintOutput")!.Text = _fingerprint;
        SetStatus($"P-256 key generated. Fingerprint {_fingerprint[..4]}-{_fingerprint[^4..]}.");
    }

    private async Task ExportManifestPackageAsync()
    {
        if (_currentCdi is null)
        {
            SetStatus("Export blocked: connect and read a card to generate CDI first.");
            return;
        }

        if (_generatedKeyPackage is null)
        {
            SetStatus("Export blocked: generate the P-256 key pair first.");
            return;
        }

        try
        {
            var product = FindNameInPages<TextBox>("ExportProduct")?.Text?.Trim();
            var variant = FindNameInPages<TextBox>("ExportVariant")?.Text?.Trim();
            var modeBox = FindNameInPages<ComboBox>("ExportMode");
            var includePrivateKey = FindNameInPages<CheckBox>("IncludePrivateKey")?.IsChecked == true;
            if (string.IsNullOrWhiteSpace(product)) throw new InvalidOperationException("Product is required.");
            if (string.IsNullOrWhiteSpace(variant)) throw new InvalidOperationException("Customer variant / batch is required.");

            var cardFolder = Path.Combine(CdiStorageService.DefaultDatabaseRoot, _currentCdi.SerialNumber);
            Directory.CreateDirectory(cardFolder);
            if (_currentCdiPath is null || !File.Exists(_currentCdiPath))
            {
                _currentCdiPath = await _cdiStorage.SaveToDatabaseAsync(_currentCdi);
            }

            var identity = new DeviceIdentity(
                _currentCdi.SerialNumber,
                _currentCdi.SerialNumber,
                _currentCdi.DeviceId,
                _currentCdi.CustomerId);
            var options = new ManifestExportOptions(
                product,
                variant,
                modeBox?.SelectedItem?.ToString() ?? "PRODUCTION",
                includePrivateKey);

            var exportPath = await KeyPackageService.ExportPackageAsync(
                cardFolder,
                identity,
                _generatedKeyPackage,
                options);

            SetStatus(includePrivateKey
                ? $"Manifest package exported with SENSITIVE private key: {exportPath}"
                : $"Manifest package exported successfully: {exportPath}");
        }
        catch (Exception ex)
        {
            SetStatus($"Manifest package export failed: {ex.Message}");
        }
    }

    private T? FindNameInPages<T>(string name) where T : FrameworkElement
    {
        foreach (var page in _pages.Values)
        {
            var found = FindDescendant<T>(page, name);
            if (found is not null) return found;
        }
        return null;
    }

    private static T? FindDescendant<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (root is T typed && typed.Name == name) return typed;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindDescendant<T>(VisualTreeHelper.GetChild(root, i), name);
            if (found is not null) return found;
        }
        return null;
    }

    private async Task ImportManifestAsync()
    {
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                ViewMode = PickerViewMode.List
            };
            picker.FileTypeFilter.Add(".json");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                SetStatus("Manifest import canceled.");
                return;
            }

            _loadedManifest = await _manifestService.LoadAsync(file.Path);
            _loadedManifestPath = file.Path;
            FindNameInPages<TextBlock>("ManifestFileText")!.Text = file.Name;
            _authorized = false;
            AuthText.Text = "VALIDATION REQUIRED";
            AuthBadge.Background = new SolidColorBrush(ColorHelper.FromArgb(48, 244, 184, 96));

            if (_stepperIdentity is not null)
            {
                _stepperIdentity = await _stepperModbus.ReadIdentityAsync();
                ApplyStepperIdentity(_stepperIdentity);
                ApplyManifestValidation(_manifestService.Validate(_stepperIdentity, _loadedManifest));
            }
            else
            {
                SetStatus($"Manifest imported: {file.Path}. Connect and read a card before authorization.");
            }
        }
        catch (Exception ex)
        {
            _loadedManifest = null;
            _loadedManifestPath = null;
            _authorized = false;
            AuthText.Text = "MANIFEST INVALID";
            AuthBadge.Background = new SolidColorBrush(ColorHelper.FromArgb(48, 255, 107, 107));
            SetStatus($"Manifest import failed: {ex.Message}");
        }
    }

    private async Task AuthorizeAsync()
    {
        if (!_connected || _stepperIdentity is null)
        {
            SetStatus("Authorization blocked: connect and read a card first.");
            return;
        }

        if (_loadedManifest is null)
        {
            SetStatus("Authorization blocked: import a provisioning manifest first.");
            return;
        }

        try
        {
            SetStatus("Re-reading live card identity before authorization...");
            _stepperIdentity = await _stepperModbus.ReadIdentityAsync();
            ApplyStepperIdentity(_stepperIdentity);
            ApplyManifestValidation(_manifestService.Validate(_stepperIdentity, _loadedManifest));
        }
        catch (Exception ex)
        {
            _authorized = false;
            AuthText.Text = "READ FAILED";
            AuthBadge.Background = new SolidColorBrush(ColorHelper.FromArgb(48, 255, 107, 107));
            SetStatus($"Authorization identity refresh failed: {ex.Message}. Reconnect after flashing and retry.");
        }
    }

    private void ApplyManifestValidation(ManifestValidationResult result)
    {
        SetAuthCheck("AuthSerial", result.SerialMatches);
        SetAuthCheck("AuthDevice", result.DeviceIdMatches);
        SetAuthCheck("AuthCustomer", result.CustomerIdMatches);
        SetAuthCheck("AuthKey", result.PublicKeyMatches);
        SetAuthCheck("AuthFingerprint", result.FingerprintMatches);
        SetAuthCheck("AuthFirmware", result.FirmwarePolicyMatches);
        _authorized = result.IsAuthorized;

        if (result.IsAuthorized)
        {
            AuthText.Text = "AUTHORIZED";
            AuthBadge.Background = new SolidColorBrush(ColorHelper.FromArgb(48, 72, 199, 142));
            SetStatus($"Manifest verified and controls authorized: {_loadedManifestPath}");
        }
        else
        {
            AuthText.Text = "DENIED";
            AuthBadge.Background = new SolidColorBrush(ColorHelper.FromArgb(48, 255, 107, 107));
            SetStatus($"Manifest denied: {result.Reason}");
        }
    }

    private void SetAuthCheck(string name, bool passed)
    {
        var text = FindNameInPages<TextBlock>(name)!;
        text.Text = passed ? "PASS" : "FAIL";
        text.Foreground = passed ? Brush("SuccessBrush") : Brush("ErrorBrush");
    }

    private void DeviceAction(object sender, RoutedEventArgs e)
    {
        if (!_connected || !_authorized) { SetStatus("Control blocked: connect and authorize the card first."); return; }
        SetStatus($"{((Button)sender).Content} requested for {_deviceProfile} hardware.");
    }

    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is string tag) ShowPage(tag);
    }

    private void ShowPage(string tag)
    {
        PageHost.Children.Clear();
        if (_pages.TryGetValue(tag, out var page)) PageHost.Children.Add(page);
    }

    private void SelectNavigation(string tag)
    {
        var item = Navigation.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => Equals(i.Tag, tag));
        if (item is not null) Navigation.SelectedItem = item;
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) { PositionSettingsDrawer(); SettingsPopup.IsOpen = true; }
    private void CloseSettings_Click(object sender, RoutedEventArgs e) => SettingsPopup.IsOpen = false;
    private void PositionSettingsDrawer() { SettingsPopup.HorizontalOffset = Math.Max(0, Bounds.Width - 444); SettingsPopup.VerticalOffset = 76; }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (PortBox.SelectedItem is not SerialPortDescriptor selectedPort)
        {
            SetStatus("Select an available COM port before connecting.");
            return;
        }

        var selected = (HardwareProfileBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "auto";
        _deviceProfile = selected == "auto" ? "stepper" : selected;
        if (_deviceProfile != "stepper")
        {
            SetStatus("ASM transport migration is not active yet. Select Stepper Motion Card for this firmware.");
            return;
        }

        try
        {
            var baud = int.Parse(((ComboBoxItem)BaudBox.SelectedItem).Content.ToString()!);
            var slave = checked((byte)Math.Round(SlaveIdBox.Value));
            SetStatus($"Opening {selectedPort.PortName} at {baud} baud, slave {slave}...");
            _stepperIdentity = await _stepperModbus.ConnectAndReadIdentityAsync(selectedPort.PortName, baud, slave);
            _connected = true;
            _authorized = false;
            ApplyStepperIdentity(_stepperIdentity);
            string cdiResult;
            try
            {
                _currentCdiPath = await _cdiStorage.SaveToDatabaseAsync(_currentCdi!);
                cdiResult = $" CDI saved to {_currentCdiPath}.";
            }
            catch (Exception cdiException)
            {
                cdiResult = $" CDI is retained internally, but database save failed: {cdiException.Message}";
            }
            await RefreshStepperLiveStatusAsync();
            ConnectionDot.Fill = Brush("SuccessBrush"); ConnectionText.Text = "Connected"; DisconnectButton.IsEnabled = true;
            DeviceTypeText.Text = $"Stepper Motion Card • Product {_stepperIdentity.ProductCode}";
            SerialText.Text = $"SERIAL {_stepperIdentity.SerialNumber}"; AuthText.Text = "NOT AUTHORIZED";
            SettingsPopup.IsOpen = false;
            SetStatus($"Stepper card read through {selectedPort.DisplayName}, slave {slave}. Identity and live registers are available.{cdiResult}");
            SelectNavigation("identity");
        }
        catch (Exception ex)
        {
            _connected = false;
            SetStatus($"Stepper connection/read failed: {ex.Message} Check COM port, 38400 8N1, and firmware slave address 2.");
        }
    }

    private async void DisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        await _stepperModbus.DisconnectAsync();
        _connected = false; _authorized = false; _deviceProfile = "none"; ConnectionDot.Fill = Brush("ErrorBrush"); ConnectionText.Text = "Disconnected"; DeviceTypeText.Text = "No hardware selected"; SerialText.Text = "SERIAL —"; AuthText.Text = "NOT AUTHORIZED"; AuthBadge.Background = new SolidColorBrush(ColorHelper.FromArgb(51, 43, 55, 70)); DisconnectButton.IsEnabled = false; SetStatus("Disconnected. Authorization and hardware writes were cleared.");
    }

    private async void RefreshPorts_Click(object sender, RoutedEventArgs e) =>
        await RefreshPortsAsync(showStatus: true);

    private async Task RefreshPortsAsync(bool showStatus)
    {
        try
        {
            var selectedPort = (PortBox.SelectedItem as SerialPortDescriptor)?.PortName;
            PortBox.IsEnabled = false;

            var ports = await Task.Run(_serialPortScanner.ScanPorts);
            PortBox.ItemsSource = ports;

            var previous = ports.FirstOrDefault(port =>
                string.Equals(port.PortName, selectedPort, StringComparison.OrdinalIgnoreCase));
            if (previous is not null)
            {
                PortBox.SelectedItem = previous;
            }
            else if (ports.Count > 0)
            {
                PortBox.SelectedIndex = 0;
            }

            PortBox.PlaceholderText = ports.Count == 0 ? "No ports found" : "Select a COM port";
            if (showStatus)
            {
                SetStatus(ports.Count == 0
                    ? "No serial ports were reported by Windows."
                    : $"Found {ports.Count} serial port(s): {string.Join(", ", ports.Select(p => p.PortName))}.");
            }
        }
        catch (Exception ex)
        {
            PortBox.ItemsSource = null;
            PortBox.PlaceholderText = "Port scan failed";
            SetStatus($"COM port scan failed: {ex.Message}");
        }
        finally
        {
            PortBox.IsEnabled = true;
        }
    }
    private void ServiceUnlock_Click(object sender, RoutedEventArgs e)
    {
        if (!_connected) { SetStatus("Service unlock requires a connected card."); return; }
        if (string.IsNullOrWhiteSpace(MasterPasswordBox.Password)) { SetStatus("Enter the service password."); return; }
        _authorized = true; MasterPasswordBox.Password = string.Empty; AuthText.Text = "SERVICE OVERRIDE"; AuthBadge.Background = new SolidColorBrush(ColorHelper.FromArgb(48, 244, 184, 96)); SettingsPopup.IsOpen = false; SetStatus("Temporary service override active for this connection.");
    }

    private void SetStatus(string message) => StatusText.Text = $"{DateTime.Now:HH:mm:ss}  {message}";

    private void ApplyStepperIdentity(StepperIdentity identity)
    {
        _currentCdi = CardIdentityCdi.FromStepperIdentity(identity);
        FindNameInPages<TextBlock>("IdentitySerial")!.Text = identity.SerialNumber;
        FindNameInPages<TextBlock>("IdentityFirmware")!.Text = identity.FirmwareVersion;
        FindNameInPages<TextBlock>("IdentityProduct")!.Text = $"Code {identity.ProductCode} / HW {identity.HardwareRevision}";
        FindNameInPages<TextBox>("IdentityUid")!.Text = identity.DeviceId96;
        FindNameInPages<TextBox>("IdentityCustomer")!.Text = identity.CustomerId10;
        FindNameInPages<TextBox>("IdentityFingerprint")!.Text = identity.PublicKeyFingerprintSha256;
        FindNameInPages<TextBox>("IdentityRawKey")!.Text = identity.PublicKeyRawHex;
        FindNameInPages<TextBox>("KeySerial")!.Text = _currentCdi.SerialNumber;
        FindNameInPages<TextBox>("KeyDeviceId")!.Text = _currentCdi.DeviceId;
        FindNameInPages<TextBox>("KeyCustomerId")!.Text = _currentCdi.CustomerId;
    }

    private async Task RefreshStepperIdentityAsync()
    {
        if (!_stepperModbus.IsConnected)
        {
            SetStatus("Connect to the Stepper card first.");
            return;
        }

        _stepperIdentity = await _stepperModbus.ReadIdentityAsync();
        ApplyStepperIdentity(_stepperIdentity);
        await RefreshStepperLiveStatusAsync();
        SetStatus("Stepper identity and live registers refreshed.");
    }

    private async Task SaveCurrentCdiAsync()
    {
        if (_currentCdi is null)
        {
            SetStatus("Connect and read card identity before generating CDI JSON.");
            return;
        }

        try
        {
            _currentCdiPath = await _cdiStorage.SaveToDatabaseAsync(_currentCdi);
            SetStatus($"CDI JSON saved: {_currentCdiPath}. The same identity is ready in Keygen.");
        }
        catch (Exception ex)
        {
            SetStatus($"CDI JSON save failed: {ex.Message}");
        }
    }

    private async Task RefreshStepperLiveStatusAsync()
    {
        var status = await _stepperModbus.ReadLiveStatusAsync();
        FindNameInPages<TextBlock>("StepperPosition")!.Text = status.Position.ToString();
        FindNameInPages<TextBlock>("StepperCommand")!.Text = status.ActiveCommand.ToString();
        FindNameInPages<TextBlock>("StepperFault")!.Text = status.Fault.ToString();
        var states = new List<string>();
        if ((status.Status & 0x0001) != 0) states.Add("Busy");
        if ((status.Status & 0x0002) != 0) states.Add("Jogging");
        if ((status.Status & 0x0008) != 0) states.Add("Homing");
        FindNameInPages<TextBlock>("StepperState")!.Text = states.Count == 0 ? "Idle" : string.Join(" | ", states);
    }

    private async Task ReadStepperRegistersAsync()
    {
        if (!_stepperModbus.IsConnected)
        {
            SetStatus("Connect to the Stepper card first.");
            return;
        }

        try
        {
            var registers = await _stepperModbus.ReadDriveRegistersAsync();
            await RefreshStepperLiveStatusAsync();
            SetStatus($"Read holding registers 0..18. Microstep={registers[6]}, PPR={registers[7]}, Accel={registers[8]}, Decel={registers[9]}, Velocity={registers[10]}, Jog={registers[11]}, Status=0x{registers[12]:X4}, Fault={registers[13]}.");
        }
        catch (Exception ex)
        {
            SetStatus($"Stepper register read failed: {ex.Message}");
        }
    }

    private async Task PrepareFirmwareAsync()
    {
        if (_currentCdi is null)
        {
            SetStatus("Firmware preparation requires a connected/read card identity.");
            return;
        }

        var generatedHeader = Path.Combine(
            CdiStorageService.DefaultDatabaseRoot,
            _currentCdi.SerialNumber,
            "lic_files",
            "card_public_key.h");
        try
        {
            AppendFirmwareLog($"Preparing identity for {_currentCdi.SerialNumber}...");
            var result = await _firmwareProvisioning.PrepareIdentityHeadersAsync(_currentCdi, generatedHeader);
            _firmwarePrepared = true;
            _firmwareBuilt = false;
            AppendFirmwareLog($"Prepared headers. Backup: {result.BackupFolder}");
            AppendFirmwareLog($"Public key: {result.PublicKeyHeader}");
            AppendFirmwareLog($"Customer ID: {result.CustomerIdHeader}");
            AppendFirmwareLog($"Serial: {result.SerialNumberHeader}");
            SetStatus("Firmware identity headers prepared. Run a clean build next.");
        }
        catch (Exception ex)
        {
            _firmwarePrepared = false;
            AppendFirmwareLog($"PREPARE FAILED: {ex.Message}");
            SetStatus($"Firmware preparation failed: {ex.Message}");
        }
    }

    private async Task BuildFirmwareAsync()
    {
        if (!_firmwarePrepared)
        {
            SetStatus("Prepare identity headers before building firmware.");
            return;
        }

        try
        {
            AppendFirmwareLog("Starting MinSizeRel clean build...");
            var result = await _firmwareProvisioning.BuildAsync();
            AppendFirmwareLog(result.Output);
            _firmwareBuilt = result.Succeeded && File.Exists(_firmwareProvisioning.ElfPath);
            if (!_firmwareBuilt) throw new InvalidOperationException($"Build failed with exit code {result.ExitCode} or did not produce the expected ELF.");
            AppendFirmwareLog($"BUILD PASSED: {_firmwareProvisioning.ElfPath}");
            SetStatus("Firmware clean build passed. Inspect the ST-LINK target next.");
        }
        catch (Exception ex)
        {
            _firmwareBuilt = false;
            AppendFirmwareLog($"BUILD FAILED: {ex.Message}");
            SetStatus($"Firmware build failed: {ex.Message}");
        }
    }

    private async Task ProbeTargetAsync()
    {
        try
        {
            AppendFirmwareLog("Inspecting ST-LINK target and option bytes...");
            var result = await _firmwareProvisioning.ProbeStLinkAsync();
            AppendFirmwareLog(result.Output);
            _stLinkReady = result.Succeeded;
            if (!_stLinkReady) throw new InvalidOperationException($"Target inspection failed with exit code {result.ExitCode}.");
            SetStatus("ST-LINK target detected. Review the log and type the serial confirmation before flashing.");
        }
        catch (Exception ex)
        {
            _stLinkReady = false;
            AppendFirmwareLog($"TARGET INSPECTION FAILED: {ex.Message}");
            SetStatus($"ST-LINK target inspection failed: {ex.Message}");
        }
    }

    private async Task FlashAndVerifyAsync()
    {
        if (_currentCdi is null) { SetStatus("No current CDI identity is available."); return; }
        if (!_firmwarePrepared || !_firmwareBuilt || !_stLinkReady)
        {
            SetStatus("Complete Prepare, Build, and Detect Target successfully before flashing.");
            return;
        }
        if (FindNameInPages<CheckBox>("FlashAcknowledge")?.IsChecked != true)
        {
            SetStatus("Acknowledge the live-device flashing warning before continuing.");
            return;
        }

        var confirmation = FindNameInPages<TextBox>("FlashSerialConfirmation")?.Text ?? string.Empty;
        if (!string.Equals(confirmation.Trim(), _currentCdi.SerialNumber, StringComparison.Ordinal))
        {
            SetStatus($"Type the exact serial {_currentCdi.SerialNumber} to confirm the flash target.");
            return;
        }

        if (PortBox.SelectedItem is not SerialPortDescriptor selectedPort)
        {
            SetStatus("Select the card COM port before flashing so post-flash verification can reconnect.");
            return;
        }

        var baud = int.Parse(((ComboBoxItem)BaudBox.SelectedItem).Content.ToString()!);
        var slave = checked((byte)Math.Round(SlaveIdBox.Value));
        try
        {
            AppendFirmwareLog($"FLASH AUTHORIZED by typed serial {_currentCdi.SerialNumber}.");
            AppendFirmwareLog("Disconnecting Modbus session before SWD programming...");
            await _stepperModbus.DisconnectAsync();
            _connected = false;
            ConnectionDot.Fill = Brush("WarningBrush");
            ConnectionText.Text = "Flashing";

            var flash = await _firmwareProvisioning.FlashAsync(confirmation, _currentCdi);
            AppendFirmwareLog(flash.Output);
            if (!flash.Succeeded) throw new InvalidOperationException($"STM32CubeProgrammer returned exit code {flash.ExitCode}.");

            AppendFirmwareLog("Flash completed. Waiting for card restart, then verifying Modbus identity...");
            await Task.Delay(1500);
            _stepperIdentity = await _stepperModbus.ConnectAndReadIdentityAsync(selectedPort.PortName, baud, slave);
            ApplyStepperIdentity(_stepperIdentity);

            var manifestPath = Path.Combine(CdiStorageService.DefaultDatabaseRoot, _currentCdi.SerialNumber, "lic_files", $"{_currentCdi.SerialNumber}_manifest.json");
            var expectedManifest = await _manifestService.LoadAsync(manifestPath);
            var validation = _manifestService.Validate(_stepperIdentity, expectedManifest);
            ApplyManifestValidation(validation);
            _connected = true;
            ConnectionDot.Fill = Brush("SuccessBrush");
            ConnectionText.Text = "Connected";
            DisconnectButton.IsEnabled = true;
            if (!validation.IsAuthorized) throw new InvalidOperationException($"Flash completed but identity verification failed: {validation.Reason}");

            AppendFirmwareLog($"FLASH VERIFIED: serial {_stepperIdentity.SerialNumber}, fingerprint {_stepperIdentity.PublicKeyFingerprintSha256}.");
            SetStatus("Firmware flashed and identity verified successfully.");
        }
        catch (Exception ex)
        {
            _authorized = false;
            AuthText.Text = "VERIFY REQUIRED";
            AppendFirmwareLog($"FLASH/VERIFY FAILED: {ex.Message}");
            SetStatus($"Firmware flash or verification failed: {ex.Message}");
        }
    }

    private void AppendFirmwareLog(string message)
    {
        var log = FindNameInPages<TextBox>("FirmwareOperationLog");
        if (log is null) return;
        var line = $"[{DateTime.Now:HH:mm:ss}] {message.TrimEnd()}";
        log.Text = string.IsNullOrWhiteSpace(log.Text) || log.Text == "No firmware operation started."
            ? line
            : $"{log.Text}{Environment.NewLine}{line}";
    }
}
