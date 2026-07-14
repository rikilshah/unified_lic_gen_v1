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
    public static string DisplayVersion
    {
        get
        {
            var version = typeof(MainWindow).Assembly.GetName().Version ?? new Version(1, 0, 0);
            return $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

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
    private readonly CustomerIdProvisioningService _customerIdProvisioning = new();
    private StepperIdentity? _stepperIdentity;
    private CustomerIdProvisioningSession? _customerIdSession;
    private CardIdentityCdi? _currentCdi;
    private string? _currentCdiPath;
    private readonly CardManifestService _manifestService = new();
    private CardManifest? _loadedManifest;
    private string? _loadedManifestPath;
    private FirmwareTargetProfile _firmwareTarget = FirmwareTargetProfile.Stepper;
    private FirmwareProvisioningService _firmwareProvisioning = new(FirmwareTargetProfile.Stepper);
    private bool _firmwarePrepared;
    private bool _firmwareBuilt;
    private bool _stLinkReady;
    private bool _defaultBaselineVerified;
    private bool _defaultFirmwareBuilt;
    private bool _defaultFirmwareFlashSucceeded;
    private bool _assignedIdentityPrepared;
    private bool _assignedIdentityVerified;
    private bool _publicKeyPrepared;
    private bool _publicKeyVerified;
    private bool _flashDefaultRequested;
    private string? _generatedPackageFolder;
    private int _wizardStep = 1;

    public MainWindow()
    {
        InitializeComponent();
        Title = $"◆ Unified Test & Keygen Dashboard  v{DisplayVersion}";
        HeaderVersionText.Text = $"TEST • AUTH • PROVISION  /  v{DisplayVersion}";
        StatusVersionText.Text = $"UnifiedLicGen  v{DisplayVersion}";
        AppWindow.Resize(new SizeInt32(1280, 800));
        BuildPages();
        Navigation.SelectedItem = Navigation.MenuItems[0];
        ShowPage("firmware");
        PositionSettingsDrawer();
        SizeChanged += (_, _) => PositionSettingsDrawer();
        Navigation.Loaded += async (_, _) => await RefreshPortsAsync(showStatus: false);
    }

    private void BuildPages()
    {
        _pages["firmware"] = BuildMinimalFirmwarePage();
        _pages["asm"] = BuildAsmPage();
        _pages["stepper"] = BuildStepperPage();
        _pages["verify"] = BuildAuthorizationPage();
        _pages["tests"] = BuildTestsPage();
    }

    private UIElement BuildOverviewPage()
    {
        var root = Page("Overview", "Connect, provision, authorize and test either supported hardware family from one workspace.");
        var actions = Columns(3);
        actions.Children.Add(MetricCard("1", "Connect & identify", "Select a card profile and establish the Modbus RTU session.", "Open settings", SettingsButton_Click));
        actions.Children.Add(MetricCard("2", "Provision in four phases", "Default firmware, assigned identity, public keys, and final verification.", "Start workflow", (_, _) => SelectNavigation("firmware")));
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
        var root = Page("Verify board", "Import a trusted manifest and compare it with the connected board.");
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
        var root = Page("4-phase card provisioning", "Follow the numbered path from a blank baseline to final card verification. Only the current valid phase can advance.");
        root.Children.Add(RowWith(
            StepChip("1", "DEFAULT FIRMWARE", true),
            new FontIcon { Glyph = "\uE76C", Foreground = Brush("TextSecondaryBrush"), VerticalAlignment = VerticalAlignment.Center },
            StepChip("2", "SERIAL + CUSTOMER ID", false),
            new FontIcon { Glyph = "\uE76C", Foreground = Brush("TextSecondaryBrush"), VerticalAlignment = VerticalAlignment.Center },
            StepChip("3", "PUBLIC KEYS", false),
            new FontIcon { Glyph = "\uE76C", Foreground = Brush("TextSecondaryBrush"), VerticalAlignment = VerticalAlignment.Center },
            StepChip("4", "FINAL VERIFY", false)));
        root.Children.Add(new InfoBar
        {
            IsOpen = true,
            Severity = InfoBarSeverity.Warning,
            Title = "Live-device operation",
            Message = "Flashing replaces MCU program flash. RDP changes and automatic unlock are never performed. Confirm the physical target and exact serial before continuing."
        });

        var target = Card("Before you start — connect and confirm the physical target");
        var hardware = new ComboBox { Name = "FirmwareHardwareTarget", Header = "Firmware target", SelectedIndex = 0, MinWidth = 280 };
        hardware.Items.Add(new ComboBoxItem { Content = "Stepper Motion Card", Tag = "stepper" });
        hardware.Items.Add(new ComboBoxItem { Content = "ASM I/O Card", Tag = "asm" });
        hardware.SelectionChanged += FirmwareTarget_SelectionChanged;
        target.Children.Add(hardware);
        target.Children.Add(RowWith(new TextBlock { Text = "Repository", Width = 180, Foreground = Brush("TextSecondaryBrush") }, new TextBlock { Name = "FirmwareRepository", Text = _firmwareTarget.RepositoryUrl }));
        target.Children.Add(RowWith(new TextBlock { Text = "Local source", Width = 180, Foreground = Brush("TextSecondaryBrush") }, new TextBlock { Name = "FirmwareSource", Text = _firmwareTarget.LocalRoot }));
        target.Children.Add(RowWith(new TextBlock { Text = "Build output", Width = 180, Foreground = Brush("TextSecondaryBrush") }, new TextBlock { Name = "FirmwareBuildOutput", Text = _firmwareProvisioning.ElfPath }));
        target.Children.Add(KeyValue("Programmer", FirmwareProvisioningService.DefaultProgrammerPath));
        target.Children.Add(new CheckBox { Name = "FlashAcknowledge", Content = "I verified the physical ST-LINK target and understand that program flash will be replaced." });
        target.Children.Add(new TextBox { Name = "FlashSerialConfirmation", Header = "Confirm the serial shown for the current phase", PlaceholderText = "Current or assigned serial" });
        root.Children.Add(target);

        var phase1 = Card("PHASE 1  •  Establish blank default firmware");
        phase1.Children.Add(new TextBlock { Text = "Read the card first. If Customer ID is 0000000000, accept the baseline. Otherwise restore all identity headers from firmware origin/main, build, flash, and verify the blank value.", TextWrapping = TextWrapping.Wrap, Foreground = Brush("TextSecondaryBrush") });
        phase1.Children.Add(new TextBlock { Name = "Phase1Status", Text = "WAITING — connect and read card", Foreground = Brush("WarningBrush"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        phase1.Children.Add(RowWith(
            ActionButton("Accept detected blank baseline", (_, _) => AcceptDefaultBaseline(), true),
            ActionButton("Prepare & build repository default", async (_, _) => await PrepareAndBuildDefaultAsync()),
            ActionButton("Flash default & verify blank", async (_, _) => await FlashDefaultAndVerifyAsync())));
        root.Children.Add(phase1);

        var phase2 = Card("PHASE 2  •  Assign serial and Customer ID");
        phase2.Children.Add(new TextBlock { Text = "Enter the production serial. The dashboard generates one Customer ID, persists the pair, restores the repository default public key, then flashes and verifies serial + Customer ID.", TextWrapping = TextWrapping.Wrap, Foreground = Brush("TextSecondaryBrush") });
        phase2.Children.Add(new TextBox { Name = "ProvisioningSerialInput", Header = "Assigned card serial", PlaceholderText = "S26050606", MaxLength = 9, FontFamily = new FontFamily("Cascadia Mono") });
        phase2.Children.Add(new TextBlock { Name = "CustomerIdProvisioningValue", Text = "LOCKED — complete Phase 1", FontFamily = new FontFamily("Cascadia Mono"), FontSize = 20 });
        phase2.Children.Add(new TextBlock { Name = "Phase2Status", Text = "LOCKED", Foreground = Brush("TextSecondaryBrush"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        phase2.Children.Add(RowWith(
            ActionButton("Generate / recover identity", async (_, _) => await PrepareBlankCustomerIdAsync(), true),
            ActionButton("Prepare & build identity", async (_, _) => await PrepareAndBuildAssignedIdentityAsync()),
            ActionButton("Flash identity & verify", async (_, _) => await FlashAssignedIdentityAndVerifyAsync())));
        root.Children.Add(phase2);

        var phase3 = Card("PHASE 3  •  Generate and flash public keys");
        phase3.Children.Add(new TextBlock { Text = "Generate/export the P-256 package, stage card_public_key.h with the verified serial and Customer ID, then flash and validate the manifest.", TextWrapping = TextWrapping.Wrap, Foreground = Brush("TextSecondaryBrush") });
        phase3.Children.Add(new TextBlock { Name = "Phase3Status", Text = "LOCKED", Foreground = Brush("TextSecondaryBrush"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        phase3.Children.Add(RowWith(
            ActionButton("Open Keygen", (_, _) => SelectNavigation("keygen"), true),
            ActionButton("Prepare & build key firmware", async (_, _) => await PrepareAndBuildPublicKeysAsync()),
            ActionButton("Flash keys & verify manifest", async (_, _) => await FlashPublicKeysAndVerifyAsync())));
        root.Children.Add(phase3);

        var phase4 = Card("PHASE 4  •  Final end-to-end verification");
        phase4.Children.Add(new TextBlock { Text = "Perform a fresh read-only check of hardware profile, MCU UID, assigned serial, Customer ID, public key, fingerprint, firmware metadata, and manifest authorization.", TextWrapping = TextWrapping.Wrap, Foreground = Brush("TextSecondaryBrush") });
        phase4.Children.Add(new TextBlock { Name = "Phase4Status", Text = "LOCKED", Foreground = Brush("TextSecondaryBrush"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        phase4.Children.Add(ActionButton("Run final verification", async (_, _) => await RunFinalProvisioningVerificationAsync(), true));
        root.Children.Add(phase4);

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

    private UIElement BuildMinimalFirmwarePage()
    {
        var root = Page("Card provisioning", "Complete one step at a time.");
        root.Children.Add(new TextBlock { Name = "WizardStepTitle", Text = "STEP 1 OF 4  /  READ CARD", Foreground = Brush("AccentBrush"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        root.Children.Add(new ProgressBar { Name = "WizardProgress", Minimum = 0, Maximum = 4, Value = 1, Height = 6 });

        var target = Card("Target");
        var hardware = new ComboBox { Name = "FirmwareHardwareTarget", Header = "Hardware", SelectedIndex = 0, MinWidth = 280 };
        hardware.Items.Add(new ComboBoxItem { Content = "Stepper Motion Card", Tag = "stepper" });
        hardware.Items.Add(new ComboBoxItem { Content = "ASM I/O Card", Tag = "asm" });
        hardware.SelectionChanged += FirmwareTarget_SelectionChanged;
        target.Children.Add(hardware);
        target.Children.Add(ActionButton("Connection settings", SettingsButton_Click, true));
        target.Children.Add(new TextBlock { Name = "FirmwareRepository", Text = _firmwareTarget.RepositoryUrl, Visibility = Visibility.Collapsed });
        target.Children.Add(new TextBlock { Name = "FirmwareSource", Text = _firmwareTarget.LocalRoot, Visibility = Visibility.Collapsed });
        target.Children.Add(new TextBlock { Name = "FirmwareBuildOutput", Text = _firmwareProvisioning.ElfPath, Visibility = Visibility.Collapsed });
        var workspace = new Grid { ColumnSpacing = 16 };
        workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(290) });
        workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        workspace.Children.Add(target);
        var phaseHost = new StackPanel { Spacing = 10 };
        Grid.SetColumn(phaseHost, 1);
        workspace.Children.Add(phaseHost);
        root.Children.Add(workspace);

        var phase1 = Card("1. Read card");
        phase1.Name = "WizardPhase1";
        phase1.Children.Add(new TextBlock { Name = "Phase1Status", Text = "WAITING - connect and read card", Foreground = Brush("WarningBrush"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        phase1.Children.Add(RowWith(
            ActionButton("Use detected blank card", (_, _) => AcceptDefaultBaseline(), true),
            ActionButton("Prepare default firmware", async (_, _) => await PrepareAndBuildDefaultAsync()),
            ActionButton("Flash default firmware", (_, _) => ShowDefaultFlashSummary())));
        phaseHost.Children.Add(phase1);

        var phase2 = Card("2. Create identity");
        phase2.Name = "WizardPhase2";
        phase2.Visibility = Visibility.Collapsed;
        phase2.Children.Add(new TextBox { Name = "ProvisioningSerialInput", Header = "Serial number", PlaceholderText = "S26050606", MaxLength = 9, FontFamily = new FontFamily("Cascadia Mono") });
        phase2.Children.Add(new TextBlock { Name = "CustomerIdProvisioningValue", Text = "LOCKED", FontFamily = new FontFamily("Cascadia Mono"), FontSize = 18 });
        phase2.Children.Add(new TextBlock { Name = "Phase2Status", Text = "LOCKED", Foreground = Brush("TextSecondaryBrush"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        phase2.Children.Add(ActionButton("Generate Customer ID and CDI", async (_, _) => await PrepareBlankCustomerIdAsync(), true));
        phaseHost.Children.Add(phase2);

        var phase3 = Card("3. Generate files and stage firmware");
        phase3.Name = "WizardPhase3";
        phase3.Visibility = Visibility.Collapsed;
        phase3.Children.Add(new TextBlock { Name = "Phase3Status", Text = "LOCKED", Foreground = Brush("TextSecondaryBrush"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        phase3.Children.Add(ActionButton("Generate package and stage", async (_, _) => await GeneratePackageAndStageFirmwareAsync(), true));
        phaseHost.Children.Add(phase3);

        var phase4 = Card("4. Review and flash");
        phase4.Name = "WizardPhase4";
        phase4.Visibility = Visibility.Collapsed;
        phase4.Children.Add(new TextBlock { Name = "Phase4Status", Text = "LOCKED", Foreground = Brush("TextSecondaryBrush"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        phase4.Children.Add(ActionButton("Review final flash", (_, _) => ShowFlashSummary(), true));
        phaseHost.Children.Add(phase4);
        phaseHost.Children.Add(RowWith(
            ActionButton("Back", WizardBack_Click),
            ActionButton("Continue", WizardNext_Click, true)));
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
        var panel = new StackPanel { Spacing = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["PageTitleStyle"] });
        panel.Children.Add(new TextBlock { Text = subtitle, Foreground = Brush("TextSecondaryBrush"), Margin = new Thickness(0, -12, 0, 4) });
        return panel;
    }

    private StackPanel Card(string title)
    {
        var content = new StackPanel { Spacing = 10, Background = Brush("SurfaceBrush"), Padding = new Thickness(12) };
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
        var text = FindNameInPages<TextBlock>(name);
        if (text is null) return;
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
        if (args.SelectedItemContainer?.Tag is not string tag) return;
        if (tag.StartsWith("wizard", StringComparison.Ordinal) && int.TryParse(tag[6..], out var step))
        {
            ShowPage("firmware");
            ShowWizardStep(step);
            return;
        }
        ShowPage(tag);
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

    private void WizardBack_Click(object sender, RoutedEventArgs e) => ShowWizardStep(Math.Max(1, _wizardStep - 1));

    private void WizardNext_Click(object sender, RoutedEventArgs e)
    {
        var canAdvance = _wizardStep switch
        {
            1 => _defaultBaselineVerified,
            2 => _customerIdSession is not null && _currentCdiPath is not null,
            3 => _publicKeyPrepared,
            _ => false
        };
        if (!canAdvance)
        {
            SetStatus($"Complete step {_wizardStep} before continuing.");
            return;
        }
        ShowWizardStep(Math.Min(4, _wizardStep + 1));
    }

    private void ShowWizardStep(int step)
    {
        _wizardStep = Math.Clamp(step, 1, 4);
        for (var index = 1; index <= 4; index++)
        {
            if (FindNameInPages<StackPanel>($"WizardPhase{index}") is { } panel)
                panel.Visibility = index == _wizardStep ? Visibility.Visible : Visibility.Collapsed;
        }
        if (FindNameInPages<ProgressBar>("WizardProgress") is { } progress) progress.Value = _wizardStep;
        if (FindNameInPages<TextBlock>("WizardStepTitle") is { } title)
        {
            var labels = new[] { "READ CARD", "CREATE IDENTITY", "GENERATE + STAGE", "REVIEW + FLASH" };
            title.Text = $"STEP {_wizardStep} OF 4  /  {labels[_wizardStep - 1]}";
        }
        var navigationItem = Navigation.MenuItems
            .OfType<NavigationViewItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), $"wizard{_wizardStep}", StringComparison.Ordinal));
        if (navigationItem is not null && !ReferenceEquals(Navigation.SelectedItem, navigationItem))
            Navigation.SelectedItem = navigationItem;
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (PortBox.SelectedItem is not SerialPortDescriptor selectedPort)
        {
            SetStatus("Select an available COM port before connecting.");
            return;
        }

        var selected = (HardwareProfileBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "auto";
        _deviceProfile = selected == "auto" ? "stepper" : selected;
        SetFirmwareTarget(FirmwareTargetProfile.FromId(_deviceProfile));

        try
        {
            var baud = int.Parse(((ComboBoxItem)BaudBox.SelectedItem).Content.ToString()!);
            var slave = checked((byte)Math.Round(SlaveIdBox.Value));
            SetStatus($"Opening {selectedPort.PortName} at {baud} baud, slave {slave}...");
            _stepperIdentity = await _stepperModbus.ConnectAndReadIdentityAsync(selectedPort.PortName, baud, slave);
            _connected = true;
            _authorized = false;
            _customerIdSession = null;
            ResetProvisioningPhases();
            if (_stepperIdentity.CustomerId10 == "0000000000")
            {
                ApplyStepperIdentity(_stepperIdentity);
                _defaultBaselineVerified = true;
                SetPhaseStatus("Phase1Status", "COMPLETE — blank default firmware detected", "SuccessBrush");
                SetPhaseStatus("Phase2Status", "READY — enter assigned serial", "AccentBrush");
                var value = FindNameInPages<TextBlock>("CustomerIdProvisioningValue");
                if (value is not null) value.Text = $"READY — ENTER ASSIGNED {_firmwareTarget.SerialPrefix} SERIAL";
                AppendFirmwareLog("Blank card detected. Waiting for the operator-assigned serial before creating final identity.");
            }
            else
            {
                var recovered = await CustomerIdProvisioningService.LoadForDeviceAsync(
                    _stepperIdentity.DeviceId96, _deviceProfile);
                if (recovered is not null &&
                    string.Equals(recovered.SerialNumber, _stepperIdentity.SerialNumber, StringComparison.Ordinal) &&
                    string.Equals(recovered.CustomerId, _stepperIdentity.CustomerId10, StringComparison.Ordinal))
                {
                    _customerIdSession = recovered;
                    ApplyStepperIdentity(_stepperIdentity);
                    _defaultBaselineVerified = true;
                    _assignedIdentityVerified = true;
                    SetPhaseStatus("Phase1Status", "COMPLETE — recovered provisioning baseline", "SuccessBrush");
                    SetPhaseStatus("Phase2Status", "COMPLETE — persisted serial and Customer ID match card", "SuccessBrush");
                    SetPhaseStatus("Phase3Status", "READY — generate or verify public-key package", "AccentBrush");
                    var manifestPath = Path.Combine(CdiStorageService.DefaultDatabaseRoot, recovered.SerialNumber, "lic_files", $"{recovered.SerialNumber}_manifest.json");
                    if (File.Exists(manifestPath))
                    {
                        var manifest = await _manifestService.LoadAsync(manifestPath);
                        var validation = _manifestService.Validate(_stepperIdentity, manifest);
                        if (validation.IsAuthorized)
                        {
                            _publicKeyVerified = true;
                            SetPhaseStatus("Phase3Status", "COMPLETE — manifest identity already matches card", "SuccessBrush");
                            SetPhaseStatus("Phase4Status", "READY — run final read-only verification", "AccentBrush");
                        }
                    }
                }
                else
                {
                    ApplyStepperIdentity(_stepperIdentity);
                    _defaultBaselineVerified = false;
                    SetPhaseStatus("Phase1Status", $"ACTION REQUIRED — card Customer ID is {_stepperIdentity.CustomerId10}", "WarningBrush");
                    SetPhaseStatus("Phase2Status", "LOCKED — establish default baseline", "TextSecondaryBrush");
                }
            }
            string cdiResult;
            if (_stepperIdentity.CustomerId10 == "0000000000")
            {
                cdiResult = " Enter the assigned serial to create the final CDI.";
            }
            else try
            {
                _currentCdiPath = await _cdiStorage.SaveToDatabaseAsync(_currentCdi!);
                cdiResult = $" CDI saved to {_currentCdiPath}.";
            }
            catch (Exception cdiException)
            {
                cdiResult = $" CDI is retained internally, but database save failed: {cdiException.Message}";
            }
            if (_deviceProfile == "stepper") await RefreshStepperLiveStatusAsync();
            ConnectionDot.Fill = Brush("SuccessBrush"); ConnectionText.Text = "Connected"; DisconnectButton.IsEnabled = true;
            DeviceTypeText.Text = $"{_firmwareTarget.DisplayName} • Product {_stepperIdentity.ProductCode}";
            SerialText.Text = $"SERIAL {_stepperIdentity.SerialNumber}";
            if (_loadedManifest is not null)
            {
                ApplyManifestValidation(_manifestService.Validate(_stepperIdentity, _loadedManifest));
            }
            else
            {
                AuthText.Text = "NOT AUTHORIZED";
            }
            SettingsPopup.IsOpen = false;
            var blankNotice = _stepperIdentity.CustomerId10 == "0000000000" ? " Blank card: assigned serial is required before Customer ID generation." : string.Empty;
            SetStatus($"{_firmwareTarget.DisplayName} read through {selectedPort.DisplayName}, slave {slave}. Identity registers are available.{cdiResult}{blankNotice}");
            SelectNavigation("firmware");
        }
        catch (Exception ex)
        {
            _connected = false;
            SetStatus($"Card connection/read failed: {ex.Message} Check the selected hardware profile, COM settings, and slave address.");
        }
    }

    private async void DisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        await _stepperModbus.DisconnectAsync();
        UpdateCustomerIdStatus(null);
        _connected = false; _authorized = false; _deviceProfile = "none"; _customerIdSession = null; ResetProvisioningPhases(); ConnectionDot.Fill = Brush("ErrorBrush"); ConnectionText.Text = "Disconnected"; DeviceTypeText.Text = "No hardware selected"; SerialText.Text = "SERIAL —"; AuthText.Text = "NOT AUTHORIZED"; AuthBadge.Background = new SolidColorBrush(ColorHelper.FromArgb(51, 43, 55, 70)); DisconnectButton.IsEnabled = false; SetStatus("Disconnected. Authorization and hardware writes were cleared.");
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

    private void UpdateCustomerIdStatus(string? customerId, bool pendingFlash = false)
    {
        if (string.IsNullOrWhiteSpace(customerId) || customerId == "0000000000")
        {
            CustomerIdTopText.Text = "CUST ID 0000000000";
            CustomerIdStatusText.Text = "CUST ID  0000000000  /  UNPROVISIONED";
            CustomerIdStatusText.Foreground = Brush("WarningBrush");
            return;
        }

        CustomerIdTopText.Text = $"CUST ID {customerId}";
        CustomerIdStatusText.Text = pendingFlash
            ? $"CUST ID  {customerId}  /  GENERATED - PENDING FLASH"
            : $"CUST ID  {customerId}  /  FLASHED";
        CustomerIdStatusText.Foreground = pendingFlash ? Brush("AccentBrush") : Brush("SuccessBrush");
    }

    private void FirmwareTarget_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox box || box.SelectedItem is not ComboBoxItem item) return;
        var requested = item.Tag?.ToString() ?? "stepper";
        if (_connected && !string.Equals(requested, _deviceProfile, StringComparison.OrdinalIgnoreCase))
        {
            box.SelectedIndex = _deviceProfile == "asm" ? 1 : 0;
            SetStatus("Firmware target must match the connected hardware profile. Disconnect before changing it.");
            return;
        }
        SetFirmwareTarget(FirmwareTargetProfile.FromId(requested));
    }

    private void SetFirmwareTarget(FirmwareTargetProfile profile)
    {
        _firmwareTarget = profile;
        _firmwareProvisioning = new FirmwareProvisioningService(profile);
        _firmwarePrepared = false;
        _firmwareBuilt = false;
        _stLinkReady = false;
        ResetProvisioningPhases();
        var box = FindNameInPages<ComboBox>("FirmwareHardwareTarget");
        var expectedIndex = profile.Id == "asm" ? 1 : 0;
        if (box is not null && box.SelectedIndex != expectedIndex) box.SelectedIndex = expectedIndex;
        var repository = FindNameInPages<TextBlock>("FirmwareRepository");
        var source = FindNameInPages<TextBlock>("FirmwareSource");
        var output = FindNameInPages<TextBlock>("FirmwareBuildOutput");
        var product = FindNameInPages<TextBox>("ExportProduct");
        var serialInput = FindNameInPages<TextBox>("ProvisioningSerialInput");
        if (repository is not null) repository.Text = profile.RepositoryUrl;
        if (source is not null) source.Text = profile.LocalRoot;
        if (output is not null) output.Text = _firmwareProvisioning.ElfPath;
        if (product is not null) product.Text = profile.Id == "asm" ? "VCB240002 ASM I/O Card" : "Stepper Control Card V2";
        if (serialInput is not null) serialInput.PlaceholderText = profile.Id == "asm" ? "A26050605" : "S26050606";
    }

    private async Task PrepareBlankCustomerIdAsync()
    {
        if (!_connected || _stepperIdentity is null)
        {
            SetStatus("Connect and read a blank card before generating its Customer ID.");
            return;
        }
        if (_stepperIdentity.CustomerId10 != "0000000000" && _customerIdSession is null)
        {
            SetStatus($"This card is already provisioned with Customer ID {_stepperIdentity.CustomerId10}. It will not be replaced.");
            return;
        }

        try
        {
            var assignedSerial = CustomerIdProvisioningService.NormalizeSerial(
                FindNameInPages<TextBox>("ProvisioningSerialInput")?.Text);
            if (!CustomerIdProvisioningService.IsValidSerial(assignedSerial))
                throw new InvalidDataException("Enter the assigned serial in XYYMMDDSS format, for example S26050606 or A26050605.");
            if (assignedSerial[0] != _firmwareTarget.SerialPrefix)
                throw new InvalidDataException($"{_firmwareTarget.DisplayName} serial must begin with {_firmwareTarget.SerialPrefix}.");

            _customerIdSession ??= await _customerIdProvisioning.LoadOrCreateAsync(
                assignedSerial, _stepperIdentity.DeviceId96, _deviceProfile);
            if (!string.Equals(_customerIdSession.SerialNumber, assignedSerial, StringComparison.Ordinal))
                throw new InvalidDataException($"Recovered session requires assigned serial {_customerIdSession.SerialNumber}.");
            ApplyStepperIdentity(_stepperIdentity, _customerIdSession.CustomerId, _customerIdSession.SerialNumber);
            _currentCdiPath = await _cdiStorage.SaveToDatabaseAsync(_currentCdi!);
            SetPhaseStatus("Phase3Status", "READY - generate files and stage firmware", "AccentBrush");
            SerialText.Text = $"SERIAL {_customerIdSession.SerialNumber} (PENDING)";
            _assignedIdentityPrepared = false;
            _assignedIdentityVerified = false;
            _publicKeyPrepared = false;
            _publicKeyVerified = false;
            SetPhaseStatus("Phase2Status", $"IDENTITY READY — {_customerIdSession.SerialNumber} / {_customerIdSession.CustomerId}", "AccentBrush");
            AppendFirmwareLog($"Serial {_customerIdSession.SerialNumber} and Customer ID {_customerIdSession.CustomerId} are persisted; retries will reuse both.");
            SetStatus($"Assigned serial {_customerIdSession.SerialNumber} and Customer ID {_customerIdSession.CustomerId} are ready. Generate the key package next.");
            ShowWizardStep(3);
        }
        catch (Exception ex)
        {
            SetStatus($"Customer ID generation/recovery failed: {ex.Message}");
        }
    }

    private void ApplyStepperIdentity(StepperIdentity identity, string? customerIdOverride = null, string? serialOverride = null)
    {
        var effectiveCustomerId = customerIdOverride ?? identity.CustomerId10;
        var effectiveSerial = serialOverride ?? identity.SerialNumber;
        UpdateCustomerIdStatus(effectiveCustomerId, customerIdOverride is not null);
        _currentCdi = new CardIdentityCdi
        {
            SerialNumber = effectiveSerial,
            DeviceId = identity.DeviceId96,
            CustomerId = effectiveCustomerId
        };
        if (FindNameInPages<TextBlock>("IdentitySerial") is { } serial) serial.Text = serialOverride is null ? effectiveSerial : $"{effectiveSerial} (pending first flash)";
        if (FindNameInPages<TextBlock>("IdentityFirmware") is { } firmware) firmware.Text = identity.FirmwareVersion;
        if (FindNameInPages<TextBlock>("IdentityProduct") is { } product) product.Text = $"Code {identity.ProductCode} / HW {identity.HardwareRevision}";
        if (FindNameInPages<TextBox>("IdentityUid") is { } uid) uid.Text = identity.DeviceId96;
        if (FindNameInPages<TextBox>("IdentityCustomer") is { } customer) customer.Text = customerIdOverride is null ? effectiveCustomerId : $"{effectiveCustomerId} (pending first flash)";
        if (FindNameInPages<TextBox>("IdentityFingerprint") is { } fingerprint) fingerprint.Text = identity.PublicKeyFingerprintSha256;
        if (FindNameInPages<TextBox>("IdentityRawKey") is { } rawKey) rawKey.Text = identity.PublicKeyRawHex;
        if (FindNameInPages<TextBox>("KeySerial") is { } keySerial) keySerial.Text = _currentCdi.SerialNumber;
        if (FindNameInPages<TextBox>("KeyDeviceId") is { } keyDevice) keyDevice.Text = _currentCdi.DeviceId;
        if (FindNameInPages<TextBox>("KeyCustomerId") is { } keyCustomer) keyCustomer.Text = _currentCdi.CustomerId;
        var provisioningValue = FindNameInPages<TextBlock>("CustomerIdProvisioningValue");
        if (provisioningValue is not null) provisioningValue.Text = customerIdOverride is null ? effectiveCustomerId : $"{effectiveSerial}  /  {effectiveCustomerId}  •  PENDING FLASH";
    }

    private async Task RefreshStepperIdentityAsync()
    {
        if (!_stepperModbus.IsConnected)
        {
            SetStatus("Connect to the Stepper card first.");
            return;
        }

        _stepperIdentity = await _stepperModbus.ReadIdentityAsync();
        ApplyStepperIdentity(_stepperIdentity, _customerIdSession?.CustomerId, _customerIdSession?.SerialNumber);
        if (_deviceProfile == "stepper") await RefreshStepperLiveStatusAsync();
        SetStatus($"{_firmwareTarget.DisplayName} identity registers refreshed.");
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
        if (FindNameInPages<TextBlock>("StepperPosition") is { } position) position.Text = status.Position.ToString();
        if (FindNameInPages<TextBlock>("StepperCommand") is { } command) command.Text = status.ActiveCommand.ToString();
        if (FindNameInPages<TextBlock>("StepperFault") is { } fault) fault.Text = status.Fault.ToString();
        var states = new List<string>();
        if ((status.Status & 0x0001) != 0) states.Add("Busy");
        if ((status.Status & 0x0002) != 0) states.Add("Jogging");
        if ((status.Status & 0x0008) != 0) states.Add("Homing");
        if (FindNameInPages<TextBlock>("StepperState") is { } state) state.Text = states.Count == 0 ? "Idle" : string.Join(" | ", states);
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

    private void AcceptDefaultBaseline()
    {
        if (!_connected || _stepperIdentity is null)
        {
            SetStatus("Connect and read the card before accepting the default baseline.");
            return;
        }
        if (_stepperIdentity.CustomerId10 != "0000000000")
        {
            SetStatus($"Card is not blank: Customer ID is {_stepperIdentity.CustomerId10}. Prepare and flash repository default firmware first.");
            return;
        }
        _defaultBaselineVerified = true;
        SetPhaseStatus("Phase1Status", "COMPLETE — blank default firmware verified", "SuccessBrush");
        SetPhaseStatus("Phase2Status", "READY — enter assigned serial", "AccentBrush");
        SetStatus("Phase 1 complete. Enter the assigned serial in Phase 2.");
        ShowWizardStep(2);
    }

    private async Task PrepareAndBuildDefaultAsync()
    {
        try
        {
            AppendFirmwareLog($"PHASE 1: restoring {_firmwareTarget.DisplayName} identity headers from origin/main...");
            var prepared = await _firmwareProvisioning.PrepareRepositoryDefaultsAsync(_stepperIdentity?.SerialNumber ?? "blank-target");
            AppendFirmwareLog($"Repository defaults staged. Backup: {prepared.BackupFolder}");
            _defaultFirmwareBuilt = await BuildForPhaseAsync("PHASE 1 default firmware");
            SetPhaseStatus("Phase1Status", _defaultFirmwareBuilt ? "READY TO FLASH — repository default built" : "FAILED — review build log", _defaultFirmwareBuilt ? "AccentBrush" : "ErrorBrush");
        }
        catch (Exception ex)
        {
            _defaultFirmwareBuilt = false;
            AppendFirmwareLog($"PHASE 1 PREPARE FAILED: {ex.Message}");
            SetStatus($"Default firmware preparation failed: {ex.Message}");
        }
    }

    private async Task FlashDefaultAndVerifyAsync()
    {
        if (!_defaultFirmwareBuilt)
        {
            SetStatus("Prepare and build repository default firmware before Phase 1 flash.");
            return;
        }
        if (FlashAcknowledge.IsChecked != true)
        {
            SetStatus("Acknowledge the physical ST-LINK target before default flashing.");
            return;
        }
        var confirmation = FlashSerialConfirmation.Text?.Trim() ?? string.Empty;
        var expectedConfirmation = GetDefaultFlashConfirmation();
        if (!string.Equals(confirmation, expectedConfirmation, StringComparison.Ordinal))
        {
            SetStatus($"Type {expectedConfirmation} to confirm the default flash.");
            return;
        }
        try
        {
            if (_stepperModbus.IsConnected) await _stepperModbus.DisconnectAsync();
            _connected = false;
            _authorized = false;
            _stepperIdentity = null;
            _currentCdi = null;
            _customerIdSession = null;
            SerialText.Text = "SERIAL —";
            AuthText.Text = "NOT AUTHORIZED";
            AuthBadge.Background = new SolidColorBrush(ColorHelper.FromArgb(51, 43, 55, 70));
            ConnectionDot.Fill = Brush("WarningBrush");
            ConnectionText.Text = "SWD flashing";
            var flash = await _firmwareProvisioning.FlashDefaultAsync(confirmation, expectedConfirmation);
            if (!flash.Succeeded) throw new InvalidOperationException($"STM32CubeProgrammer returned exit code {flash.ExitCode}.");
            _defaultFirmwareFlashSucceeded = true;
            _defaultBaselineVerified = false;
            _defaultFirmwareBuilt = false;
            ConnectionText.Text = "Default flashed";
            DisconnectButton.IsEnabled = false;
            UpdateCustomerIdStatus(null);
            SetPhaseStatus("Phase1Status", "DEFAULT FLASHED - connect through Modbus to continue", "SuccessBrush");
            SetStatus("Default firmware flashed through SWD without Modbus. Connect and identify the card to continue.");
        }
        catch (Exception ex)
        {
            _defaultFirmwareFlashSucceeded = false;
            _defaultBaselineVerified = false;
            AppendFirmwareLog($"PHASE 1 SWD FLASH FAILED: {ex.Message}");
            SetPhaseStatus("Phase1Status", "FAILED - default SWD flash did not complete", "ErrorBrush");
            SetStatus($"Phase 1 failed: {ex.Message}");
        }
    }

    private async Task PrepareAndBuildAssignedIdentityAsync()
    {
        if (!_defaultBaselineVerified || _customerIdSession is null || _currentCdi is null)
        {
            SetStatus("Complete Phase 1, enter the assigned serial, and generate/recover Customer ID first.");
            return;
        }
        try
        {
            AppendFirmwareLog("PHASE 2: staging assigned serial + Customer ID with repository default public key...");
            var prepared = await _firmwareProvisioning.PrepareAssignedIdentityHeadersAsync(_currentCdi);
            AppendFirmwareLog($"Phase 2 headers staged. Backup: {prepared.BackupFolder}");
            _assignedIdentityPrepared = await BuildForPhaseAsync("PHASE 2 assigned identity firmware");
            SetPhaseStatus("Phase2Status", _assignedIdentityPrepared ? "READY TO FLASH — identity firmware built" : "FAILED — review build log", _assignedIdentityPrepared ? "AccentBrush" : "ErrorBrush");
        }
        catch (Exception ex)
        {
            _assignedIdentityPrepared = false;
            AppendFirmwareLog($"PHASE 2 PREPARE FAILED: {ex.Message}");
            SetStatus($"Assigned identity preparation failed: {ex.Message}");
        }
    }

    private async Task FlashAssignedIdentityAndVerifyAsync()
    {
        if (!_assignedIdentityPrepared || _customerIdSession is null || _currentCdi is null)
        {
            SetStatus("Prepare and build Phase 2 identity firmware first.");
            return;
        }
        if (!VerifyFlashConfirmation(_currentCdi.SerialNumber)) return;
        try
        {
            await _customerIdProvisioning.MarkFlashStartedAsync(_customerIdSession);
            var readback = await FlashAndReconnectForPhaseAsync(_currentCdi, _currentCdi.SerialNumber, "PHASE 2");
            RequireAssignedIdentityReadback(readback, _currentCdi);
            _stepperIdentity = readback;
            ApplyStepperIdentity(readback);
            _assignedIdentityVerified = true;
            _assignedIdentityPrepared = false;
            SetPhaseStatus("Phase2Status", "COMPLETE — serial and Customer ID verified", "SuccessBrush");
            SetPhaseStatus("Phase3Status", "READY — generate and export P-256 package", "AccentBrush");
            SetStatus("Phase 2 complete. Generate/export the public-key package in Keygen.");
        }
        catch (Exception ex)
        {
            _assignedIdentityVerified = false;
            AppendFirmwareLog($"PHASE 2 FLASH/VERIFY FAILED: {ex.Message}");
            SetPhaseStatus("Phase2Status", "FAILED — assigned identity readback mismatch", "ErrorBrush");
            SetStatus($"Phase 2 failed: {ex.Message}");
        }
    }

    private async Task GeneratePackageAndStageFirmwareAsync()
    {
        if (!_defaultBaselineVerified || _customerIdSession is null || _currentCdi is null)
        {
            SetStatus("Complete card read and identity generation first.");
            return;
        }

        try
        {
            SetPhaseStatus("Phase3Status", "WORKING - generating package", "WarningBrush");
            _generatedKeyPackage = KeyPackageService.GenerateKeyPackage();
            _rawPublicKey = _generatedKeyPackage.RawPublicKeyHex;
            _sec1PublicKey = _generatedKeyPackage.Sec1PublicKeyHex;
            _fingerprint = _generatedKeyPackage.FingerprintFullHex;

            _currentCdiPath = await _cdiStorage.SaveToDatabaseAsync(_currentCdi);
            var cardFolder = Path.Combine(CdiStorageService.DefaultDatabaseRoot, _currentCdi.SerialNumber);
            var identity = KeyPackageService.ParseIdentityJson(await File.ReadAllTextAsync(_currentCdiPath));
            var product = _firmwareTarget.Id == "asm" ? "VCB240002 ASM I/O Card" : "Stepper Control Card V2";
            _generatedPackageFolder = await KeyPackageService.ExportPackageAsync(
                cardFolder,
                identity,
                _generatedKeyPackage,
                new ManifestExportOptions(product, "PRODUCTION", "PRODUCTION", IncludePrivateKey: true));

            var publicKeyHeader = Path.Combine(_generatedPackageFolder, "card_public_key.h");
            await _firmwareProvisioning.PrepareIdentityHeadersAsync(_currentCdi, publicKeyHeader);
            _publicKeyPrepared = await BuildForPhaseAsync("FINAL staged firmware");
            _firmwarePrepared = _publicKeyPrepared;
            _firmwareBuilt = _publicKeyPrepared;
            _loadedManifestPath = Path.Combine(_generatedPackageFolder, $"{_currentCdi.SerialNumber}_manifest.json");
            _loadedManifest = await _manifestService.LoadAsync(_loadedManifestPath);

            SetPhaseStatus("Phase3Status", "COMPLETE - files saved and firmware staged", "SuccessBrush");
            SetPhaseStatus("Phase4Status", "READY - review final flash", "AccentBrush");
            SetStatus($"Package saved to {_generatedPackageFolder}. Firmware is staged and built.");
            ShowWizardStep(4);
        }
        catch (Exception ex)
        {
            _publicKeyPrepared = false;
            _firmwarePrepared = false;
            _firmwareBuilt = false;
            SetPhaseStatus("Phase3Status", "FAILED - package or staging error", "ErrorBrush");
            SetStatus($"Package generation or firmware staging failed: {ex.Message}");
        }
    }

    private void ShowDefaultFlashSummary()
    {
        if (!_defaultFirmwareBuilt)
        {
            SetStatus("Prepare default firmware before flashing it.");
            return;
        }
        _flashDefaultRequested = true;
        var expectedConfirmation = GetDefaultFlashConfirmation();
        var modbusIdentity = _stepperModbus.IsConnected && _stepperIdentity is not null
            ? $"Serial {_stepperIdentity.SerialNumber}"
            : "Unavailable - blank-card fallback";
        FlashSummaryText.Text = $"DEFAULT FIRMWARE{Environment.NewLine}{Environment.NewLine}Hardware: {_firmwareTarget.DisplayName}{Environment.NewLine}Modbus identity: {modbusIdentity}{Environment.NewLine}Confirmation: {expectedConfirmation}{Environment.NewLine}Firmware: {_firmwareProvisioning.ElfPath}";
        FlashSerialConfirmation.Header = $"Type {expectedConfirmation} to confirm";
        FlashSerialConfirmation.Text = string.Empty;
        FlashAcknowledge.IsChecked = false;
        FlashProgressPanel.Visibility = Visibility.Collapsed;
        FinalFlashButton.IsEnabled = true;
        PositionFlashSummary();
        FlashSummaryPopup.IsOpen = true;
    }

    private string GetDefaultFlashConfirmation()
    {
        if (_stepperModbus.IsConnected && _stepperIdentity is not null &&
            CustomerIdProvisioningService.IsValidSerial(_stepperIdentity.SerialNumber))
            return _stepperIdentity.SerialNumber;
        return FirmwareProvisioningService.BlankCardFlashConfirmation;
    }

    private void ShowFlashSummary()
    {
        if (!_publicKeyPrepared || _currentCdi is null || string.IsNullOrWhiteSpace(_generatedPackageFolder))
        {
            SetStatus("Generate the package and stage firmware before final flash.");
            return;
        }
        _flashDefaultRequested = false;
        FlashSummaryText.Text = $"Hardware: {_firmwareTarget.DisplayName}{Environment.NewLine}Serial: {_currentCdi.SerialNumber}{Environment.NewLine}Customer ID: {_currentCdi.CustomerId}{Environment.NewLine}Fingerprint: {_fingerprint}{Environment.NewLine}Package: {_generatedPackageFolder}{Environment.NewLine}Firmware: {_firmwareProvisioning.ElfPath}";
        FlashSerialConfirmation.Header = "Type the assigned serial to confirm";
        FlashSerialConfirmation.Text = string.Empty;
        FlashAcknowledge.IsChecked = false;
        FlashProgressPanel.Visibility = Visibility.Collapsed;
        FinalFlashButton.IsEnabled = true;
        PositionFlashSummary();
        FlashSummaryPopup.IsOpen = true;
    }

    private async void FinalFlash_Click(object sender, RoutedEventArgs e)
    {
        FinalFlashButton.IsEnabled = false;
        FlashAcknowledge.IsEnabled = false;
        FlashSerialConfirmation.IsEnabled = false;
        SetFlashProgress(10, "Validating confirmation...");
        try
        {
            if (_flashDefaultRequested)
            {
                SetFlashProgress(25, "Probing ST-LINK...");
                FlashProgressBar.IsIndeterminate = true;
                FlashProgressText.Text = "Programming default firmware through ST-LINK...";
                await FlashDefaultAndVerifyAsync();
                FlashProgressBar.IsIndeterminate = false;
                SetFlashProgress(_defaultFirmwareFlashSucceeded ? 100 : 0, _defaultFirmwareFlashSucceeded ? "Default firmware flashed. Connect through Modbus to continue." : "Default flash failed. Check status.");
                return;
            }
            if (!_publicKeyPrepared || _currentCdi is null)
            {
                SetStatus("Final firmware is not ready.");
                SetFlashProgress(0, "Final firmware is not ready.");
                return;
            }
            SetFlashProgress(25, "Probing ST-LINK...");
            _stLinkReady = await ProbeForPhaseAsync("FINAL FLASH");
            if (!_stLinkReady)
            {
                SetFlashProgress(0, "ST-LINK probe failed.");
                return;
            }
            FlashProgressBar.IsIndeterminate = true;
            FlashProgressText.Text = "Programming firmware, reconnecting, and verifying identity...";
            await FlashAndVerifyAsync();
            FlashProgressBar.IsIndeterminate = false;
            _publicKeyVerified = _authorized;
            SetPhaseStatus("Phase4Status", _publicKeyVerified ? "COMPLETE - flash and verification passed" : "FAILED - verification did not pass", _publicKeyVerified ? "SuccessBrush" : "ErrorBrush");
            SetFlashProgress(_publicKeyVerified ? 100 : 0, _publicKeyVerified ? "Flash and identity verification complete." : "Verification failed. Check status.");
        }
        finally
        {
            FlashProgressBar.IsIndeterminate = false;
            FinalFlashButton.IsEnabled = true;
            FlashAcknowledge.IsEnabled = true;
            FlashSerialConfirmation.IsEnabled = true;
        }
    }

    private void SetFlashProgress(double value, string message)
    {
        FlashProgressPanel.Visibility = Visibility.Visible;
        FlashProgressBar.Value = value;
        FlashProgressText.Text = message;
    }

    private void CloseFlashSummary_Click(object sender, RoutedEventArgs e) => FlashSummaryPopup.IsOpen = false;

    private void PositionFlashSummary()
    {
        FlashSummaryPopup.HorizontalOffset = Math.Max(24, (Bounds.Width - 520) / 2);
        FlashSummaryPopup.VerticalOffset = Math.Max(76, (Bounds.Height - 480) / 2);
    }

    private async Task PrepareAndBuildPublicKeysAsync()
    {
        if (!_assignedIdentityVerified || _currentCdi is null)
        {
            SetStatus("Complete Phase 2 identity verification before public-key firmware.");
            return;
        }
        var generatedHeader = Path.Combine(CdiStorageService.DefaultDatabaseRoot, _currentCdi.SerialNumber, "lic_files", "card_public_key.h");
        try
        {
            AppendFirmwareLog("PHASE 3: staging generated public key with verified serial and Customer ID...");
            var prepared = await _firmwareProvisioning.PrepareIdentityHeadersAsync(_currentCdi, generatedHeader);
            AppendFirmwareLog($"Phase 3 headers staged. Backup: {prepared.BackupFolder}");
            _publicKeyPrepared = await BuildForPhaseAsync("PHASE 3 public-key firmware");
            SetPhaseStatus("Phase3Status", _publicKeyPrepared ? "READY TO FLASH — public-key firmware built" : "FAILED — review build log", _publicKeyPrepared ? "AccentBrush" : "ErrorBrush");
        }
        catch (Exception ex)
        {
            _publicKeyPrepared = false;
            AppendFirmwareLog($"PHASE 3 PREPARE FAILED: {ex.Message}");
            SetStatus($"Public-key firmware preparation failed: {ex.Message}");
        }
    }

    private async Task FlashPublicKeysAndVerifyAsync()
    {
        if (!_publicKeyPrepared || _currentCdi is null)
        {
            SetStatus("Prepare and build Phase 3 public-key firmware first.");
            return;
        }
        _firmwarePrepared = true;
        _firmwareBuilt = true;
        _stLinkReady = await ProbeForPhaseAsync("PHASE 3");
        if (!_stLinkReady) return;
        await FlashAndVerifyAsync();
        _publicKeyVerified = _authorized;
        if (_publicKeyVerified)
        {
            _publicKeyPrepared = false;
            SetPhaseStatus("Phase3Status", "COMPLETE — public key and manifest verified", "SuccessBrush");
            SetPhaseStatus("Phase4Status", "READY — run final read-only verification", "AccentBrush");
        }
        else
        {
            SetPhaseStatus("Phase3Status", "FAILED — manifest verification did not pass", "ErrorBrush");
        }
    }

    private async Task RunFinalProvisioningVerificationAsync()
    {
        if (!_publicKeyVerified || _currentCdi is null || !_stepperModbus.IsConnected)
        {
            SetStatus("Complete Phase 3 and remain connected before final verification.");
            return;
        }
        try
        {
            var expected = _currentCdi;
            var readback = await _stepperModbus.ReadIdentityAsync();
            RequireAssignedIdentityReadback(readback, expected);
            var manifestPath = Path.Combine(CdiStorageService.DefaultDatabaseRoot, expected.SerialNumber, "lic_files", $"{expected.SerialNumber}_manifest.json");
            var manifest = await _manifestService.LoadAsync(manifestPath);
            var validation = _manifestService.Validate(readback, manifest);
            ApplyManifestValidation(validation);
            if (!validation.IsAuthorized) throw new InvalidOperationException(validation.Reason);
            _stepperIdentity = readback;
            ApplyStepperIdentity(readback);
            SetPhaseStatus("Phase4Status", "COMPLETE — entire provisioning process verified", "SuccessBrush");
            AppendFirmwareLog($"FINAL VERIFICATION PASSED: {_firmwareTarget.DisplayName}, {readback.SerialNumber}, Customer {readback.CustomerId10}, fingerprint {readback.PublicKeyFingerprintSha256}.");
            SetStatus("All four provisioning phases completed and verified.");
        }
        catch (Exception ex)
        {
            SetPhaseStatus("Phase4Status", "FAILED — final verification mismatch", "ErrorBrush");
            AppendFirmwareLog($"FINAL VERIFICATION FAILED: {ex.Message}");
            SetStatus($"Final verification failed: {ex.Message}");
        }
    }

    private async Task<bool> BuildForPhaseAsync(string phase)
    {
        AppendFirmwareLog($"{phase}: configuring and clean-building {_firmwareTarget.BuildPreset}...");
        var result = await _firmwareProvisioning.BuildAsync();
        AppendFirmwareLog(result.Output);
        var passed = result.Succeeded && File.Exists(_firmwareProvisioning.ElfPath);
        if (!passed) throw new InvalidOperationException($"{phase} build failed or expected ELF was not produced.");
        AppendFirmwareLog($"{phase} BUILD PASSED: {_firmwareProvisioning.ElfPath}");
        return true;
    }

    private async Task<bool> ProbeForPhaseAsync(string phase)
    {
        AppendFirmwareLog($"{phase}: detecting ST-LINK and checking option bytes...");
        var probe = await _firmwareProvisioning.ProbeStLinkAsync();
        AppendFirmwareLog(probe.Output);
        if (!probe.Succeeded)
        {
            SetStatus($"{phase} target detection failed. Review the provisioning log.");
            return false;
        }
        return true;
    }

    private bool VerifyFlashConfirmation(string expectedSerial)
    {
        if (FlashAcknowledge.IsChecked != true)
        {
            SetStatus("Acknowledge the live-device warning before flashing.");
            return false;
        }
        var confirmation = CustomerIdProvisioningService.NormalizeSerial(FlashSerialConfirmation.Text);
        if (!string.Equals(confirmation, expectedSerial, StringComparison.Ordinal))
        {
            SetStatus($"Type exact serial {expectedSerial} in the flash confirmation field.");
            return false;
        }
        return true;
    }

    private async Task<StepperIdentity> FlashAndReconnectForPhaseAsync(CardIdentityCdi cdi, string confirmationSerial, string phase)
    {
        if (PortBox.SelectedItem is not SerialPortDescriptor selectedPort) throw new InvalidOperationException("Select the card COM port for post-flash verification.");
        if (!await ProbeForPhaseAsync(phase)) throw new InvalidOperationException("ST-LINK target detection failed.");
        var baud = int.Parse(((ComboBoxItem)BaudBox.SelectedItem).Content.ToString()!);
        var slave = checked((byte)Math.Round(SlaveIdBox.Value));
        AppendFirmwareLog($"{phase}: disconnecting Modbus and flashing confirmed target {confirmationSerial}...");
        await _stepperModbus.DisconnectAsync();
        _connected = false;
        ConnectionDot.Fill = Brush("WarningBrush");
        ConnectionText.Text = "Flashing";
        var flash = await _firmwareProvisioning.FlashAsync(confirmationSerial, cdi);
        AppendFirmwareLog(flash.Output);
        if (!flash.Succeeded) throw new InvalidOperationException($"STM32CubeProgrammer returned exit code {flash.ExitCode}.");
        await Task.Delay(1500);
        var readback = await _stepperModbus.ConnectAndReadIdentityAsync(selectedPort.PortName, baud, slave);
        _connected = true;
        ConnectionDot.Fill = Brush("SuccessBrush");
        ConnectionText.Text = "Connected";
        DisconnectButton.IsEnabled = true;
        return readback;
    }

    private static void RequireAssignedIdentityReadback(StepperIdentity readback, CardIdentityCdi expected)
    {
        if (!string.Equals(readback.SerialNumber, expected.SerialNumber, StringComparison.Ordinal))
            throw new InvalidOperationException($"Serial readback mismatch. Expected {expected.SerialNumber}, card returned {readback.SerialNumber}.");
        if (!string.Equals(readback.CustomerId10, expected.CustomerId, StringComparison.Ordinal))
            throw new InvalidOperationException($"Customer ID readback mismatch. Expected {expected.CustomerId}, card returned {readback.CustomerId10}.");
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
            if (!string.Equals(_firmwareTarget.Id, _deviceProfile, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Firmware target does not match the connected hardware profile.");
            if (_stepperIdentity?.CustomerId10 == "0000000000" && _customerIdSession is null)
                throw new InvalidOperationException("Generate or recover the blank card Customer ID before preparing firmware.");
            AppendFirmwareLog($"Preparing identity for {_currentCdi.SerialNumber}...");
            var result = await _firmwareProvisioning.PrepareIdentityHeadersAsync(_currentCdi, generatedHeader);
            _firmwarePrepared = true;
            _firmwareBuilt = false;
            AppendFirmwareLog($"Prepared headers. Backup: {result.BackupFolder}");
            AppendFirmwareLog($"Public key: {result.PublicKeyHeader}");
            AppendFirmwareLog($"Customer ID: {result.CustomerIdHeader}");
            AppendFirmwareLog($"Serial: {result.SerialNumberHeader}");
            SetStatus($"{_firmwareTarget.DisplayName} identity headers prepared. Run a clean {_firmwareTarget.BuildPreset} build next.");
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
            AppendFirmwareLog($"Starting {_firmwareTarget.DisplayName} {_firmwareTarget.BuildPreset} clean build...");
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
        if (FlashAcknowledge.IsChecked != true)
        {
            SetStatus("Acknowledge the live-device flashing warning before continuing.");
            return;
        }

        var confirmation = FlashSerialConfirmation.Text ?? string.Empty;
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
        var expectedCdi = _currentCdi;
        try
        {
            AppendFirmwareLog($"FLASH AUTHORIZED by typed serial {_currentCdi.SerialNumber}.");
            AppendFirmwareLog("Disconnecting Modbus session before SWD programming...");
            await _stepperModbus.DisconnectAsync();
            _connected = false;
            ConnectionDot.Fill = Brush("WarningBrush");
            ConnectionText.Text = "Flashing";

            if (_customerIdSession is not null)
            {
                await _customerIdProvisioning.MarkFlashStartedAsync(_customerIdSession);
                AppendFirmwareLog($"Customer ID {_customerIdSession.CustomerId} is now locked as final truth for this provisioning session.");
            }

            var flash = await _firmwareProvisioning.FlashAsync(confirmation, expectedCdi);
            AppendFirmwareLog(flash.Output);
            if (!flash.Succeeded) throw new InvalidOperationException($"STM32CubeProgrammer returned exit code {flash.ExitCode}.");

            AppendFirmwareLog("Flash completed. Waiting for card restart, then verifying Modbus identity...");
            await Task.Delay(1500);
            _stepperIdentity = await _stepperModbus.ConnectAndReadIdentityAsync(selectedPort.PortName, baud, slave);
            if (!string.Equals(_stepperIdentity.SerialNumber, expectedCdi.SerialNumber, StringComparison.Ordinal))
                throw new InvalidOperationException($"Serial readback mismatch. Expected {expectedCdi.SerialNumber}, card returned {_stepperIdentity.SerialNumber}.");
            if (!string.Equals(_stepperIdentity.CustomerId10, expectedCdi.CustomerId, StringComparison.Ordinal))
                throw new InvalidOperationException($"Customer ID readback mismatch. Expected {expectedCdi.CustomerId}, card returned {_stepperIdentity.CustomerId10}.");
            ApplyStepperIdentity(_stepperIdentity);

            var manifestPath = Path.Combine(CdiStorageService.DefaultDatabaseRoot, expectedCdi.SerialNumber, "lic_files", $"{expectedCdi.SerialNumber}_manifest.json");
            var expectedManifest = await _manifestService.LoadAsync(manifestPath);
            var validation = _manifestService.Validate(_stepperIdentity, expectedManifest);
            ApplyManifestValidation(validation);
            _connected = true;
            ConnectionDot.Fill = Brush("SuccessBrush");
            ConnectionText.Text = "Connected";
            DisconnectButton.IsEnabled = true;
            if (!validation.IsAuthorized) throw new InvalidOperationException($"Flash completed but identity verification failed: {validation.Reason}");

            if (_customerIdSession is not null)
            {
                await _customerIdProvisioning.MarkVerifiedAsync(_customerIdSession);
                AppendFirmwareLog($"Customer ID VERIFIED: {_customerIdSession.CustomerId} exactly matches Modbus readback.");
            }

            AppendFirmwareLog($"FLASH VERIFIED: serial {_stepperIdentity.SerialNumber}, fingerprint {_stepperIdentity.PublicKeyFingerprintSha256}.");
            DeviceTypeText.Text = $"{_firmwareTarget.DisplayName} • Product {_stepperIdentity.ProductCode}";
            SetStatus($"{_firmwareTarget.DisplayName} firmware, Customer ID, and manifest identity verified successfully.");
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

    private void ResetProvisioningPhases()
    {
        _defaultBaselineVerified = false;
        _defaultFirmwareBuilt = false;
        _defaultFirmwareFlashSucceeded = false;
        _assignedIdentityPrepared = false;
        _assignedIdentityVerified = false;
        _publicKeyPrepared = false;
        _publicKeyVerified = false;
        _generatedPackageFolder = null;
        SetPhaseStatus("Phase1Status", "WAITING — connect and read card", "WarningBrush");
        SetPhaseStatus("Phase2Status", "LOCKED", "TextSecondaryBrush");
        SetPhaseStatus("Phase3Status", "LOCKED", "TextSecondaryBrush");
        SetPhaseStatus("Phase4Status", "LOCKED", "TextSecondaryBrush");
        ShowWizardStep(1);
    }

    private void SetPhaseStatus(string name, string text, string brushKey)
    {
        var status = FindNameInPages<TextBlock>(name);
        if (status is null) return;
        status.Text = text;
        status.Foreground = Brush(brushKey);
    }
}
