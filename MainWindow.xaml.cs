using System.Security.Cryptography;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using UnifiedLicGen.Models;
using UnifiedLicGen.Services;
using KeyGeneratorUi.Services;
using Windows.Storage.Pickers;
using Windows.Graphics;
using Windows.UI;

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
    private StepperIdentity? _asmIdentity;
    private bool _asmAuthorized;
    private bool _stepperAuthorized;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _inputPollTimer;
    private bool _inputPollInProgress;
    private CancellationTokenSource? _rainbowCts;
    private readonly Dictionary<string, Storyboard> _indicatorAnimations = new();
    private Storyboard? _fanAnimation;
    private bool _syncingAsmControls;
    private bool _lastBlowerOn;
    private bool _lastOnboardLedOn;
    private Color _selectedWs2812Color = Colors.Black;
    private bool _syncingWs2812Color;
    private CardIdentityCdi? _currentCdi;
    private string? _currentCdiPath;
    private readonly CardManifestService _manifestService = new();
    private CardManifest? _loadedManifest;
    private string? _loadedManifestPath;
    private readonly Dictionary<string, CardManifest> _trustedManifests = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _trustedManifestPaths = new(StringComparer.OrdinalIgnoreCase);
    private static string ManifestPreferenceFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnifiedLicGen");
    private static string LegacyManifestPreferencePath => Path.Combine(ManifestPreferenceFolder, "manifest.path");
    private static string ManifestPreferencePath => LegacyManifestPreferencePath;
    private static string ManifestPreferenceFile(string profileId) => Path.Combine(ManifestPreferenceFolder, $"manifest.{profileId}.path");
    private static string DefaultManifestPath => Path.Combine(AppContext.BaseDirectory, "manifests", "default_manifest.json");
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
    private bool _maintenanceReflashRequested;
    private CardIdentityCdi? _maintenanceExpectedCdi;
    private CardManifest? _maintenanceManifest;
    private string? _maintenanceManifestPath;
    private string? _maintenanceElfSha256;
    private FirmwareTargetProfile? _maintenanceTarget;
    private FirmwareProvisioningService? _maintenanceProvisioning;
    private string? _generatedPackageFolder;
    private int _wizardStep = 1;

    public MainWindow()
    {
        InitializeComponent();
        _inputPollTimer = DispatcherQueue.CreateTimer();
        _inputPollTimer.Interval = TimeSpan.FromMilliseconds(300);
        _inputPollTimer.IsRepeating = true;
        _inputPollTimer.Tick += InputPollTimer_Tick;
        Title = $"◆ Unified Test & Keygen Dashboard  v{DisplayVersion}";
        HeaderVersionText.Text = $"TEST • AUTH • PROVISION  /  v{DisplayVersion}";
        StatusVersionText.Text = $"UnifiedLicGen  v{DisplayVersion}";
        AppWindow.Resize(new SizeInt32(1440, 900));
        BuildPages();
        Navigation.SelectedItem = Navigation.MenuItems[0];
        ShowPage("firmware");
        PositionSettingsDrawer();
        SizeChanged += (_, _) => PositionSettingsDrawer();
        Closed += (_, _) => { StopRainbowSweep(false); _inputPollTimer.Stop(); _stepperModbus.Dispose(); };
        Navigation.Loaded += async (_, _) =>
        {
            await RefreshPortsAsync(showStatus: false);
            await RestoreManifestAsync();
        };
    }

    private void BuildPages()
    {
        _pages["firmware"] = BuildMinimalFirmwarePage();
        _pages["asm"] = BuildAsmControlPage();
        _pages["stepper"] = BuildStepperControlPageV2();
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

        var notice = new InfoBar { IsOpen = true, Severity = InfoBarSeverity.Informational, Title = "Shared SOP, separate hardware", Message = "Identity, provisioning and authorization are shared. ASM and Stepper controls remain safely isolated." };
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
        var load = Card("Trusted manifests");
        var row = Row();
        row.Children.Add(ActionButton("Import manifest", async (_, _) => await ImportManifestAsync(), true));
        row.Children.Add(new TextBlock { Text = "Import once for each connected card. The serial routes it automatically.", Foreground = Brush("TextSecondaryBrush"), VerticalAlignment = VerticalAlignment.Center });
        load.Children.Add(row);
        load.Children.Add(CompactStatus("ASM", "Not loaded", "AsmManifestFileText"));
        load.Children.Add(CompactStatus("Stepper", "Not loaded", "StepperManifestFileText"));
        root.Children.Add(load);

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
        target.Children.Add(new Border { Height = 1, Margin = new Thickness(0, 8, 0, 4), Background = Brush("BorderBrush") });
        target.Children.Add(new TextBlock { Text = "MAINTENANCE", FontSize = 11, Foreground = Brush("AccentBrush"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        target.Children.Add(new TextBlock
        {
            Text = "Rebuild and reflash a finalized card. Existing identity and license files remain unchanged.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("TextSecondaryBrush")
        });
        target.Children.Add(ActionButton("Reflash finalized firmware", async (_, _) => await PrepareMaintenanceReflashAsync()));
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

    private UIElement BuildAsmControlPage()
    {
        var root = Page("ASM controls", "Compact live control for PWM, digital I/O and WS2812 outputs.");
        root.Children.Add(AuthGate());
        var live = Columns(4);
        live.Children.Add(ValueCard("PWM1", "0", "Applied / 1023", "AsmPwm1Applied"));
        live.Children.Add(ValueCard("PWM2", "0", "Applied / 1023", "AsmPwm2Applied"));
        live.Children.Add(ValueCard("FREQUENCY", "0 Hz", "Applied", "AsmFrequencyApplied"));
        live.Children.Add(ValueCard("STATUS", "Idle", "Communication", "AsmModbusStatus")); root.Children.Add(live);

        var work = Columns(3);
        var pwm = Card("PWM outputs");
        var pwmFields = Columns(3);
        var pwm1 = Number("PWM1 duty", 0, 0, 1023); pwm1.Name = "AsmPwm1"; pwm1.MinWidth = 80; pwmFields.Children.Add(pwm1);
        var pwm2 = Number("PWM2 duty", 0, 0, 1023); pwm2.Name = "AsmPwm2"; pwm2.MinWidth = 80; pwmFields.Children.Add(pwm2);
        var frequency = Number("Frequency (Hz)", 1000, 1, 11718); frequency.Name = "AsmFrequency"; frequency.MinWidth = 80; pwmFields.Children.Add(frequency);
        pwm.Children.Add(pwmFields);
        pwm.Children.Add(ActionButton("Apply and verify", async (_, _) => await ApplyAsmPwmAsync(), true)); work.Children.Add(pwm);

        var io = Card("Inputs and outputs");
        var blower = new ToggleSwitch { Name = "AsmBlower", Header = "Blower", OffContent = "Off", OnContent = "On" };
        blower.Toggled += AsmOutput_Toggled;
        var fan = BuildFanIndicator();
        var onboardLed = new ToggleSwitch { Name = "AsmOnboardLed", Header = "Onboard LED", OffContent = "Off", OnContent = "On" };
        onboardLed.Toggled += AsmOutput_Toggled;
        var outputs = Columns(2);
        outputs.Children.Add(RowWith(blower, fan));
        outputs.Children.Add(onboardLed);
        io.Children.Add(outputs);
        var inputs = Columns(2);
        inputs.Children.Add(InputIndicator("IP1", "AsmIp1", "AsmIp1Dot"));
        inputs.Children.Add(InputIndicator("IP2", "AsmIp2", "AsmIp2Dot"));
        io.Children.Add(inputs);
        work.Children.Add(io);

        var diagnostics = Card("Diagnostics");
        var diagnosticValues = Columns(2);
        diagnosticValues.Children.Add(CompactStatus("WS2812 DMA", "Idle", "AsmWsBusy"));
        diagnosticValues.Children.Add(CompactStatus("TIM1 prescaler", "—", "AsmPrescaler"));
        diagnosticValues.Children.Add(CompactStatus("Clock division", "—", "AsmClockDivision"));
        diagnosticValues.Children.Add(CompactStatus("Pedal events", "None", "AsmPedals"));
        diagnosticValues.Children.Add(CompactStatus("Firmware", "—", "AsmFirmware"));
        diagnostics.Children.Add(diagnosticValues);
        diagnostics.Children.Add(ActionButton("Refresh status", async (_, _) => await RefreshAsmStateAsync(), true)); work.Children.Add(diagnostics);
        root.Children.Add(work);

        var leds = Card("WS2812 lighting");
        var lighting = new Grid { ColumnSpacing = 16, HorizontalAlignment = HorizontalAlignment.Stretch };
        lighting.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
        lighting.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5, GridUnitType.Star) });
        lighting.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(7, GridUnitType.Star) });
        lighting.Children.Add(BuildWs2812ColorPreview());

        var rgb = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        rgb.Children.Add(BuildRgbSlider("AsmLedRed", "R", Brush("ErrorBrush")));
        rgb.Children.Add(BuildRgbSlider("AsmLedGreen", "G", Brush("SuccessBrush")));
        rgb.Children.Add(BuildRgbSlider("AsmLedBlue", "B", new SolidColorBrush(ColorHelper.FromArgb(255, 88, 166, 255))));
        Grid.SetColumn(rgb, 1); lighting.Children.Add(rgb);

        var ledControls = new StackPanel { Spacing = 7 };
        var deviceFields = Columns(3);
        var address = Number("LED address", 0, 0, 7); address.Name = "AsmLedAddress"; address.MinWidth = 80; deviceFields.Children.Add(address);
        var brightness = Number("Brightness", 128, 0, 255); brightness.Name = "AsmLedBrightness"; brightness.MinWidth = 80; deviceFields.Children.Add(brightness);
        var rainbowSpeed = Number("Rainbow frame (ms)", 180, 80, 2000); rainbowSpeed.Name = "AsmRainbowInterval"; rainbowSpeed.MinWidth = 100; deviceFields.Children.Add(rainbowSpeed);
        ledControls.Children.Add(deviceFields);
        var ledActions = Columns(4);
        ledActions.Children.Add(ActionButton("Update LED", async (_, _) => await UpdateAsmLedAsync(), true));
        ledActions.Children.Add(ActionButton("Apply all", async (_, _) => await UpdateAllAsmLedsAsync()));
        ledActions.Children.Add(ActionButton("Set brightness", async (_, _) => await SetAsmBrightnessAsync()));
        ledActions.Children.Add(ActionButton("Clear", async (_, _) => await ClearAsmLedsAsync()));
        ledControls.Children.Add(ledActions);
        var animationActions = Columns(2);
        animationActions.Children.Add(ActionButton("Start rainbow", (_, _) => StartRainbowSweep(), true));
        animationActions.Children.Add(ActionButton("Stop rainbow", (_, _) => StopRainbowSweep()));
        ledControls.Children.Add(animationActions);
        Grid.SetColumn(ledControls, 2); lighting.Children.Add(ledControls);
        leds.Children.Add(lighting);
        root.Children.Add(leds);
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
        leds.Children.Add(RowWith(ActionButton("Update selected LED", DeviceAction, true), ActionButton("Clear all LEDs", DeviceAction), ActionButton("Refresh outputs", DeviceAction)));
        root.Children.Add(leds);
        var profiles = Card("Preset"); profiles.Children.Add(RowWith(ActionButton("Load config JSON", DeviceAction), ActionButton("Save config JSON", DeviceAction), ActionButton("Sync from device", DeviceAction))); root.Children.Add(profiles);
        return root;
    }

    private UIElement BuildStepperControlPageV2()
    {
        var root = Page("Stepper controls", "Motion and setup grouped by positioning, jog, reference and drive configuration.");
        root.Children.Add(AuthGate());
        var live = Columns(4);
        live.Children.Add(ValueCard("POSITION", "0", "Pulses", "StepperPosition")); live.Children.Add(ValueCard("STATE", "Idle", "Drive", "StepperState"));
        live.Children.Add(ValueCard("FAULT", "0", "Code", "StepperFault")); live.Children.Add(ValueCard("COMMAND", "0", "Active pulses", "StepperCommand")); root.Children.Add(live);

        var actions = Columns(3);
        var positioning = Card("Positioning");
        var relativeRow = Columns(2);
        var relative = Number("Relative pulses", 1000, int.MinValue, int.MaxValue); relative.Name = "StepperRelative"; relativeRow.Children.Add(relative);
        relativeRow.Children.Add(ActionButton("Move relative", async (_, _) => await MoveStepperRelativeAsync(), true)); positioning.Children.Add(relativeRow);
        var absoluteRow = Columns(2);
        var absolute = Number("Absolute pulses", 0, 0, uint.MaxValue); absolute.Name = "StepperAbsolute"; absoluteRow.Children.Add(absolute);
        absoluteRow.Children.Add(ActionButton("Move absolute", async (_, _) => await MoveStepperAbsoluteAsync(), true)); positioning.Children.Add(absoluteRow); actions.Children.Add(positioning);

        var manual = Card("Manual movement");
        var jog = Number("Jog chunk", 100, 0, ushort.MaxValue); jog.Name = "StepperJogChunk"; manual.Children.Add(jog);
        manual.Children.Add(RowWith(ActionButton("Jog −", async (_, _) => await JogStepperAsync(false)), ActionButton("Jog +", async (_, _) => await JogStepperAsync(true), true)));
        manual.Children.Add(CompactStatus("Inputs", "None active", "StepperInputSummary")); actions.Children.Add(manual);

        var reference = Card("Reference");
        reference.Children.Add(new TextBlock { Text = "Reset establishes position zero. Homing runs the configured sequence.", Foreground = Brush("TextSecondaryBrush"), TextWrapping = TextWrapping.Wrap });
        reference.Children.Add(RowWith(ActionButton("Reset position", async (_, _) => await ResetStepperPositionAsync()), ActionButton("Start homing", async (_, _) => await StartStepperHomingAsync(), true)));
        reference.Children.Add(ActionButton("Refresh live state", async (_, _) => await RefreshStepperDashboardAsync())); actions.Children.Add(reference); root.Children.Add(actions);

        var config = Card("Drive setup");
        var cfg = Columns(5);
        AddNamedNumber(cfg, "StepperMicrostep", "Microstep", 16); AddNamedNumber(cfg, "StepperPpr", "Pulses / rev", 3200);
        AddNamedNumber(cfg, "StepperAcceleration", "Acceleration", 1000); AddNamedNumber(cfg, "StepperDeceleration", "Deceleration", 1000); AddNamedNumber(cfg, "StepperVelocity", "Velocity", 2000); config.Children.Add(cfg);
        var advanced = new Expander { Header = "Advanced homing, deadband and driver flags" };
        var advancedPanel = new StackPanel { Spacing = 8, Padding = new Thickness(0, 8, 0, 0) };
        var numbers = Columns(5); AddNamedNumber(numbers, "StepperHomeChunk", "Home chunk", 100); AddNamedNumber(numbers, "StepperDeadband", "Deadband", 10);
        AddNamedNumber(numbers, "StepperHomingSpeed", "Home speed", 500); AddNamedNumber(numbers, "StepperDeadbandSpeed", "Deadband speed", 100); advancedPanel.Children.Add(numbers);
        var flags = Columns(4);
        flags.Children.Add(new ToggleSwitch { Name = "StepperInvertDirection", Header = "Invert direction" });
        flags.Children.Add(new ToggleSwitch { Name = "StepperSwapJog", Header = "Swap jog" });
        flags.Children.Add(new ToggleSwitch { Name = "StepperEncLimit", Header = "ENC_Z limit" });
        flags.Children.Add(new ToggleSwitch { Name = "StepperEncActiveHigh", Header = "ENC_Z active-high" });
        advancedPanel.Children.Add(flags);
        advanced.Content = advancedPanel; config.Children.Add(advanced);
        config.Children.Add(RowWith(ActionButton("Refresh setup", async (_, _) => await ReadStepperConfigurationAsync(), true), ActionButton("Write and verify", async (_, _) => await WriteStepperConfigurationAsync()))); root.Children.Add(config);
        return root;
    }

    private static void AddNamedNumber(Panel panel, string name, string header, double value)
    { panel.Children.Add(new NumberBox { Name = name, Header = header, Value = value, Minimum = 0, Maximum = ushort.MaxValue, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, MinWidth = 112, HorizontalAlignment = HorizontalAlignment.Stretch }); }

    private UIElement BuildStepperControlPage()
    {
        var root = Page("Stepper controls", "Compact motion control grouped by positioning, manual movement and drive setup.");
        root.Children.Add(AuthGate());
        var live = Columns(4);
        live.Children.Add(ValueCard("POSITION", "0", "Pulses", "StepperPosition")); live.Children.Add(ValueCard("STATE", "Idle", "Drive", "StepperState"));
        live.Children.Add(ValueCard("FAULT", "0", "Code", "StepperFault")); live.Children.Add(ValueCard("COMMAND", "0", "Active ID", "StepperCommand")); root.Children.Add(live);

        var actionGrid = Columns(3);
        var positioning = Card("Positioning  ·  target moves");
        positioning.Children.Add(Number("Relative pulses", 1000, int.MinValue, int.MaxValue)); positioning.Children.Add(ActionButton("Move relative", DeviceAction, true));
        positioning.Children.Add(Number("Absolute pulses", 0, 0, uint.MaxValue)); positioning.Children.Add(ActionButton("Move absolute", DeviceAction, true)); actionGrid.Children.Add(positioning);
        var manual = Card("Manual movement  ·  jog"); manual.Children.Add(Number("Jog chunk", 100, 0, ushort.MaxValue));
        manual.Children.Add(RowWith(ActionButton("Jog −", DeviceAction), ActionButton("Jog +", DeviceAction))); actionGrid.Children.Add(manual);
        var recovery = Card("Reference  ·  home & reset"); recovery.Children.Add(ActionButton("Reset position", DeviceAction)); recovery.Children.Add(ActionButton("Start homing", DeviceAction, true));
        recovery.Children.Add(CompactStatus("Inputs", "IP1 · IP2 · ENC_Z", "StepperInputSummary")); actionGrid.Children.Add(recovery); root.Children.Add(actionGrid);

        var config = Card("Drive setup");
        var cfg = Columns(5); cfg.Children.Add(Number("Microstep", 16, 0, ushort.MaxValue)); cfg.Children.Add(Number("Pulses / rev", 3200, 0, ushort.MaxValue)); cfg.Children.Add(Number("Acceleration", 1000, 0, ushort.MaxValue)); cfg.Children.Add(Number("Deceleration", 1000, 0, ushort.MaxValue)); cfg.Children.Add(Number("Velocity", 2000, 0, ushort.MaxValue)); config.Children.Add(cfg);
        var advanced = new Expander { Header = "Advanced homing, deadband and driver flags" };
        var advancedPanel = new StackPanel { Spacing = 8, Padding = new Thickness(0, 8, 0, 0) };
        var numbers = Columns(5); numbers.Children.Add(Number("Home chunk", 100, 0, ushort.MaxValue)); numbers.Children.Add(Number("Deadband", 10, 0, ushort.MaxValue)); numbers.Children.Add(Number("Home speed", 500, 0, ushort.MaxValue)); numbers.Children.Add(Number("Deadband speed", 100, 0, ushort.MaxValue)); numbers.Children.Add(Number("Jog chunk", 100, 0, ushort.MaxValue)); advancedPanel.Children.Add(numbers);
        advancedPanel.Children.Add(RowWith(new ToggleSwitch { Header = "Invert direction" }, new ToggleSwitch { Header = "Swap jog" }, new ToggleSwitch { Header = "ENC_Z limit" }, new ToggleSwitch { Header = "ENC_Z active-high" })); advanced.Content = advancedPanel; config.Children.Add(advanced);
        config.Children.Add(RowWith(ActionButton("Read all", async (_, _) => await ReadStepperRegistersAsync(), true), ActionButton("Write setup", DeviceAction), ActionButton("Driver bits", DeviceAction), ActionButton("Load profile", DeviceAction), ActionButton("Save profile", DeviceAction))); root.Children.Add(config);
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
        var panel = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["PageTitleStyle"] });
        panel.Children.Add(new TextBlock { Text = subtitle, Foreground = Brush("TextSecondaryBrush"), Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap });
        return panel;
    }

    private StackPanel Card(string title)
    {
        var content = new StackPanel { Spacing = 8, Background = Brush("SurfaceBrush"), Padding = new Thickness(12) };
        content.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["SectionTitleStyle"] });
        return content;
    }

    private Border ValueCard(string label, string value, string detail, string? valueName = null)
    {
        var valueText = new TextBlock { Text = value, FontFamily = new FontFamily("Cascadia Mono"), FontSize = 21, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        if (!string.IsNullOrWhiteSpace(valueName)) valueText.Name = valueName;
        return new Border
        {
            Style = (Style)Application.Current.Resources["CardStyle"],
            Child = new StackPanel { Spacing = 5, Children = { new TextBlock { Text = label, FontSize = 11, Foreground = Brush("TextSecondaryBrush") }, valueText, new TextBlock { Text = detail, FontSize = 12, Foreground = Brush("TextSecondaryBrush") } } }
        };
    }

    private Border MetricCard(string number, string title, string body, string action, RoutedEventHandler handler) => new()
    {
        Style = (Style)Application.Current.Resources["CardStyle"],
        Child = new StackPanel { Spacing = 10, Children = { new TextBlock { Text = number, FontSize = 12, Foreground = Brush("AccentBrush") }, new TextBlock { Text = title, FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }, new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, Foreground = Brush("TextSecondaryBrush"), MinHeight = 42 }, ActionButton(action, handler, true) } }
    };

    private Grid Columns(int count)
    {
        var grid = new Grid { ColumnSpacing = 12, RowSpacing = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
        for (var i = 0; i < count; i++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Loaded += (_, _) =>
        {
            var rowCount = Math.Max(1, (int)Math.Ceiling(grid.Children.Count / (double)count));
            while (grid.RowDefinitions.Count < rowCount) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var i = 0; i < grid.Children.Count; i++)
            {
                Grid.SetColumn((FrameworkElement)grid.Children[i], i % count);
                Grid.SetRow((FrameworkElement)grid.Children[i], i / count);
            }
        };
        return grid;
    }

    private StackPanel Row() => new() { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
    private StackPanel RowWith(params UIElement[] controls) { var row = Row(); foreach (var c in controls) row.Children.Add(c); return row; }
    private Button ActionButton(string text, RoutedEventHandler handler, bool primary = false) { var b = new Button { Content = text, MinHeight = 34, Padding = new Thickness(12, 5, 12, 5), HorizontalAlignment = HorizontalAlignment.Stretch }; if (primary) b.Style = (Style)Application.Current.Resources["PrimaryButtonStyle"]; b.Click += handler; return b; }
    private TextBox Input(string header, string value) => new() { Header = header, Text = value };
    private NumberBox Number(string header, double value, double min, double max) => new() { Header = header, Value = value, Minimum = min, Maximum = max, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, MinWidth = 112, HorizontalAlignment = HorizontalAlignment.Stretch };
    private TextBox ReadOnlyField(string header, string value) => new() { Header = header, Text = value, IsReadOnly = true, FontFamily = new FontFamily("Cascadia Mono"), TextWrapping = TextWrapping.Wrap };
    private StackPanel KeyValue(string key, string value) => RowWith(new TextBlock { Text = key, Width = 180, Foreground = Brush("TextSecondaryBrush") }, new TextBlock { Text = value });
    private Border AuthGate() => new() { Background = new SolidColorBrush(ColorHelper.FromArgb(38, 244, 184, 96)), BorderBrush = Brush("WarningBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 7, 10, 7), Child = new TextBlock { Text = "Controls require this card's verified manifest.", Foreground = Brush("WarningBrush") } };
    private Grid CheckRow(string name, string rule, string stateName) { var g = new Grid { Padding = new Thickness(0, 8, 0, 8) }; g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) }); g.ColumnDefinitions.Add(new ColumnDefinition()); g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) }); g.Children.Add(new TextBlock { Text = name }); var r = new TextBlock { Text = rule, Foreground = Brush("TextSecondaryBrush") }; Grid.SetColumn(r, 1); g.Children.Add(r); var state = new TextBlock { Name = stateName, Text = "NOT CHECKED", Foreground = Brush("WarningBrush"), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right }; Grid.SetColumn(state, 2); g.Children.Add(state); return g; }
    private Border StepChip(string number, string label, bool active) => new() { Background = active ? new SolidColorBrush(ColorHelper.FromArgb(48, 57, 198, 212)) : Brush("RaisedBrush"), BorderBrush = active ? Brush("AccentBrush") : Brush("BorderBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16), Padding = new Thickness(12, 6, 12, 6), Child = new TextBlock { Text = $"{number}  {label}", FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = active ? Brush("AccentBrush") : Brush("TextSecondaryBrush") } };
    private Grid IndicatorRow(string a, string av, string b, string bv) { var g = Columns(2); g.Children.Add(KeyValue(a, av)); g.Children.Add(KeyValue(b, bv)); return g; }
    private Grid CompactStatus(string label, string value, string name)
    {
        var text = new TextBlock { Name = name, Text = value, FontFamily = new FontFamily("Cascadia Mono"), HorizontalAlignment = HorizontalAlignment.Right };
        var grid = new Grid { Padding = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = label, Foreground = Brush("TextSecondaryBrush") });
        Grid.SetColumn(text, 1); grid.Children.Add(text); return grid;
    }
    private Border TestSuite(string title, IEnumerable<string> tests) { var p = new StackPanel { Spacing = 9 }; p.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["SectionTitleStyle"] }); foreach (var test in tests) p.Children.Add(RowWith(new FontIcon { Glyph = "\uE73E", Foreground = Brush("TextSecondaryBrush") }, new TextBlock { Text = test })); return new Border { Style = (Style)Application.Current.Resources["CardStyle"], Child = p }; }
    private SolidColorBrush Brush(string key) => (SolidColorBrush)Application.Current.Resources[key];

    private UIElement BuildWs2812ColorPreview()
    {
        var preview = new Border
        {
            Name = "AsmColorPreview", Width = 64, Height = 64, Background = new SolidColorBrush(_selectedWs2812Color),
            BorderBrush = Brush("BorderBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8)
        };
        return new StackPanel
        {
            Spacing = 5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                preview,
                new TextBlock { Name = "AsmColorHexValue", Text = "#000000", FontFamily = new FontFamily("Cascadia Mono"), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center }
            }
        };
    }

    private Grid BuildRgbSlider(string name, string label, SolidColorBrush accent)
    {
        var slider = new Slider
        {
            Name = name, Minimum = 0, Maximum = 255, StepFrequency = 1, SmallChange = 1, LargeChange = 16,
            Foreground = accent, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 140
        };
        slider.ValueChanged += Ws2812Channel_ValueChanged;
        var value = new TextBlock
        {
            Name = $"{name}Value", Text = "0", FontFamily = new FontFamily("Cascadia Mono"), FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center
        };
        var row = new Grid { Height = 29, ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        row.Children.Add(new TextBlock { Text = label, Foreground = accent, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(slider, 1); row.Children.Add(slider);
        Grid.SetColumn(value, 2); row.Children.Add(value);
        return row;
    }

    private void Ws2812Channel_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs args)
    {
        if (sender is not Slider slider) return;
        SetText($"{slider.Name}Value", Math.Round(args.NewValue).ToString());
        if (_syncingWs2812Color) return;
        var channels = ReadWs2812Channels();
        _selectedWs2812Color = Color.FromArgb(255, (byte)channels.R, (byte)channels.G, (byte)channels.B);
        UpdateWs2812ColorPreview();
    }

    private (ushort R, ushort G, ushort B) ReadWs2812Channels() =>
        (SliderChannel("AsmLedRed"), SliderChannel("AsmLedGreen"), SliderChannel("AsmLedBlue"));

    private ushort SliderChannel(string name)
    {
        var slider = FindNameInPages<Slider>(name) ?? throw new InvalidOperationException($"{name} control is unavailable.");
        return (ushort)Math.Clamp(Math.Round(slider.Value), 0, 255);
    }

    private void SetWs2812ColorChannels(ushort red, ushort green, ushort blue)
    {
        var channels = new[] { ("AsmLedRed", red), ("AsmLedGreen", green), ("AsmLedBlue", blue) };
        _syncingWs2812Color = true;
        foreach (var (name, rawValue) in channels)
        {
            var value = Math.Min(rawValue, (ushort)255);
            if (FindNameInPages<Slider>(name) is { } slider) slider.Value = value;
            SetText($"{name}Value", value.ToString());
        }
        _syncingWs2812Color = false;
        _selectedWs2812Color = Color.FromArgb(255, (byte)Math.Min(red, (ushort)255), (byte)Math.Min(green, (ushort)255), (byte)Math.Min(blue, (ushort)255));
        UpdateWs2812ColorPreview();
    }

    private void UpdateWs2812ColorPreview()
    {
        if (FindNameInPages<Border>("AsmColorPreview") is { } preview) preview.Background = new SolidColorBrush(_selectedWs2812Color);
        SetText("AsmColorHexValue", $"#{_selectedWs2812Color.R:X2}{_selectedWs2812Color.G:X2}{_selectedWs2812Color.B:X2}");
    }

    internal static (ushort R, ushort G, ushort B) Ws2812Channels(Color color) => (color.R, color.G, color.B);

    private Grid InputIndicator(string label, string textName, string dotName)
    {
        var dot = new Microsoft.UI.Xaml.Shapes.Ellipse { Name = dotName, Width = 12, Height = 12, Fill = Brush("ErrorBrush"), VerticalAlignment = VerticalAlignment.Center };
        var text = new TextBlock { Name = textName, Text = "Inactive", FontFamily = new FontFamily("Cascadia Mono"), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right };
        var grid = new Grid { Height = 30, ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(dot); var title = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(title, 1); grid.Children.Add(title); Grid.SetColumn(text, 2); grid.Children.Add(text);
        return grid;
    }

    private Grid BuildFanIndicator()
    {
        var fan = new Grid { Name = "AsmFan", Width = 24, Height = 24, VerticalAlignment = VerticalAlignment.Center, RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5), RenderTransform = new RotateTransform() };
        foreach (var angle in new[] { 0d, 120d, 240d })
        {
            var bladeLayer = new Grid { Width = 24, Height = 24, RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5), RenderTransform = new RotateTransform { Angle = angle } };
            bladeLayer.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 7, Height = 10, Fill = Brush("AccentBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0) });
            fan.Children.Add(bladeLayer);
        }
        fan.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 6, Height = 6, Fill = Brush("AccentBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        return fan;
    }

    private void SetInputIndicator(string textName, string dotName, bool active)
    {
        SetText(textName, active ? "Active" : "Inactive");
        if (FindNameInPages<Microsoft.UI.Xaml.Shapes.Ellipse>(dotName) is not { } dot) return;
        dot.Fill = active ? Brush("SuccessBrush") : Brush("ErrorBrush");
        if (active)
        {
            if (!_indicatorAnimations.TryGetValue(dotName, out var pulse))
            {
                var animation = new DoubleAnimation { From = 1, To = 0.35, Duration = TimeSpan.FromMilliseconds(500), AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
                Storyboard.SetTarget(animation, dot); Storyboard.SetTargetProperty(animation, "Opacity");
                pulse = new Storyboard(); pulse.Children.Add(animation); _indicatorAnimations[dotName] = pulse;
            }
            pulse.Begin();
        }
        else if (_indicatorAnimations.TryGetValue(dotName, out var pulse)) { pulse.Stop(); dot.Opacity = 1; }
    }

    private void UpdateFanAnimation(bool running)
    {
        if (FindNameInPages<Grid>("AsmFan") is not { } fan) return;
        if (running)
        {
            if (_fanAnimation is null)
            {
                var rotation = new DoubleAnimation { From = 0, To = 360, Duration = TimeSpan.FromMilliseconds(700), RepeatBehavior = RepeatBehavior.Forever };
                Storyboard.SetTarget(rotation, fan); Storyboard.SetTargetProperty(rotation, "(UIElement.RenderTransform).(RotateTransform.Angle)");
                _fanAnimation = new Storyboard(); _fanAnimation.Children.Add(rotation);
            }
            _fanAnimation.Begin();
        }
        else { _fanAnimation?.Stop(); if (fan.RenderTransform is RotateTransform transform) transform.Angle = 0; }
    }

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

            await LoadManifestAsync(file.Path, persist: true);
            AuthText.Text = "VALIDATION REQUIRED";
            AuthBadge.Background = new SolidColorBrush(ColorHelper.FromArgb(48, 244, 184, 96));

            if (_connected)
            {
                ValidateManifestAgainstConnectedCards();
            }
            else
            {
                SetStatus($"Manifest imported: {file.Path}. Connect and read a card before authorization.");
            }
        }
        catch (Exception ex)
        {
            AuthText.Text = "MANIFEST INVALID";
            AuthBadge.Background = new SolidColorBrush(ColorHelper.FromArgb(48, 255, 107, 107));
            SetStatus($"Manifest import failed; existing trusted card manifests were preserved: {ex.Message}");
        }
    }

    private async Task AuthorizeAsync()
    {
        if (!_connected || (_stepperIdentity is null && _asmIdentity is null))
        {
            SetStatus("Authorization blocked: connect and read a card first.");
            return;
        }
        if (_trustedManifests.Count == 0)
        {
            SetStatus("Authorization blocked: import the manifest for each card you want to control.");
            return;
        }

        try
        {
            SetStatus("Re-reading live card identity before authorization...");
            if (_asmIdentity is not null) _asmIdentity = await _stepperModbus.ReadIdentityAsync(_stepperModbus.AsmSlaveId);
            if (_stepperIdentity is not null && FirmwareTargetProfile.FromSerial(_stepperIdentity.SerialNumber)?.Id == "stepper")
                _stepperIdentity = await _stepperModbus.ReadIdentityAsync(_stepperModbus.StepperSlaveId);
            else if (_asmIdentity is not null)
                _stepperIdentity = _asmIdentity;
            if (_asmIdentity is not null) SetCardIdentityHeader("asm", _asmIdentity.SerialNumber, _asmIdentity.CustomerId10);
            if (_stepperIdentity is not null && FirmwareTargetProfile.FromSerial(_stepperIdentity.SerialNumber)?.Id == "stepper")
                SetCardIdentityHeader("stepper", _stepperIdentity.SerialNumber, _stepperIdentity.CustomerId10);
            ValidateManifestAgainstConnectedCards();
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
    private void PositionSettingsDrawer() { SettingsPopup.HorizontalOffset = Math.Max(0, Bounds.Width - 444); SettingsPopup.VerticalOffset = 84; }

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

        try
        {
            var baud = int.Parse(((ComboBoxItem)BaudBox.SelectedItem).Content.ToString()!);
            var asmSlave = checked((byte)Math.Round(AsmSlaveIdBox.Value));
            var stepperSlave = checked((byte)Math.Round(StepperSlaveIdBox.Value));
            if (asmSlave == stepperSlave) throw new InvalidOperationException("ASM and Stepper must use different slave IDs.");
            ClearCardIdentityHeaders();
            SetStatus($"Opening {selectedPort.PortName} at {baud} baud; probing ASM {asmSlave} and Stepper {stepperSlave}...");
            await _stepperModbus.OpenAsync(selectedPort.PortName, baud);
            _stepperModbus.AsmSlaveId = asmSlave; _stepperModbus.StepperSlaveId = stepperSlave;
            _asmIdentity = await TryReadIdentityAsync(asmSlave, "asm");
            _stepperIdentity = await TryReadIdentityAsync(stepperSlave, "stepper");
            if (_asmIdentity is null && _stepperIdentity is null) throw new IOException("Neither configured slave returned a valid identity.");
            _deviceProfile = _asmIdentity is not null && _stepperIdentity is not null ? "both" : _asmIdentity is not null ? "asm" : "stepper";
            var provisioningProfile = FirmwareTargetProfile.ResolveConnectedTarget(
                _firmwareTarget,
                _asmIdentity is not null,
                _stepperIdentity is not null);
            var primary = provisioningProfile.Id == "asm" ? _asmIdentity! : _stepperIdentity!;
            _stepperIdentity ??= primary; // Legacy Admin provisioning uses this as the active identity.
            SetFirmwareTarget(provisioningProfile);
            _connected = true;
            _authorized = _asmAuthorized = _stepperAuthorized = false;
            _customerIdSession = null;
            ResetProvisioningPhases();
            if (primary.CustomerId10 == "0000000000")
            {
                ApplyStepperIdentity(primary);
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
                    primary.DeviceId96, provisioningProfile.Id);
                if (recovered is not null &&
                    string.Equals(recovered.SerialNumber, primary.SerialNumber, StringComparison.Ordinal) &&
                    string.Equals(recovered.CustomerId, primary.CustomerId10, StringComparison.Ordinal))
                {
                    _customerIdSession = recovered;
                    ApplyStepperIdentity(primary);
                    _defaultBaselineVerified = true;
                    _assignedIdentityVerified = true;
                    SetPhaseStatus("Phase1Status", "COMPLETE — recovered provisioning baseline", "SuccessBrush");
                    SetPhaseStatus("Phase2Status", "COMPLETE — persisted serial and Customer ID match card", "SuccessBrush");
                    SetPhaseStatus("Phase3Status", "READY — generate or verify public-key package", "AccentBrush");
                    var manifestPath = Path.Combine(CdiStorageService.DefaultDatabaseRoot, recovered.SerialNumber, "lic_files", $"{recovered.SerialNumber}_manifest.json");
                    if (File.Exists(manifestPath))
                    {
                        var manifest = await _manifestService.LoadAsync(manifestPath);
                        var validation = _manifestService.Validate(primary, manifest);
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
                    ApplyStepperIdentity(primary);
                    _defaultBaselineVerified = false;
                    SetPhaseStatus("Phase1Status", $"ACTION REQUIRED — card Customer ID is {primary.CustomerId10}", "WarningBrush");
                    SetPhaseStatus("Phase2Status", "LOCKED — establish default baseline", "TextSecondaryBrush");
                }
            }
            string cdiResult;
            if (primary.CustomerId10 == "0000000000")
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
            if (provisioningProfile.Id == "stepper") await RefreshStepperLiveStatusAsync();
            if (_asmIdentity is not null) StartInputPolling();
            ConnectionDot.Fill = Brush("SuccessBrush"); ConnectionText.Text = "Connected"; DisconnectButton.IsEnabled = true;
            DeviceTypeText.Text = _deviceProfile == "both" ? "2 cards online" : _deviceProfile == "asm" ? $"ASM · ID {asmSlave}" : $"Stepper · ID {stepperSlave}";
            if (_asmIdentity is not null) SetCardIdentityHeader("asm", _asmIdentity.SerialNumber, _asmIdentity.CustomerId10);
            if (_stepperIdentity is not null && FirmwareTargetProfile.FromSerial(_stepperIdentity.SerialNumber)?.Id == "stepper")
                SetCardIdentityHeader("stepper", _stepperIdentity.SerialNumber, _stepperIdentity.CustomerId10);
            await LoadKnownManifestsForConnectedCardsAsync();
            if (_trustedManifests.Count > 0)
            {
                ValidateManifestAgainstConnectedCards();
            }
            else
            {
                AuthText.Text = "NOT AUTHORIZED";
            }
            SettingsPopup.IsOpen = false;
            var blankNotice = primary.CustomerId10 == "0000000000" ? " Blank card: assigned serial is required before Customer ID generation." : string.Empty;
            var stepperDetected = _stepperIdentity is not null &&
                                  FirmwareTargetProfile.FromSerial(_stepperIdentity.SerialNumber)?.Id == "stepper";
            SetStatus($"{(_asmIdentity is not null ? $"ASM slave {asmSlave}" : "ASM not found")}; {(stepperDetected ? $"Stepper slave {stepperSlave}" : "Stepper not found")} on {selectedPort.DisplayName}. Selected reflash target: {provisioningProfile.DisplayName}.{cdiResult}{blankNotice}");
            SelectNavigation("firmware");
        }
        catch (Exception ex)
        {
            _connected = false;
            SetStatus($"Card connection/read failed: {ex.Message} Check the COM settings and both slave addresses.");
        }
    }

    private void ValidateManifestAgainstConnectedCards()
    {
        _trustedManifests.TryGetValue("asm", out var asmManifest);
        _trustedManifests.TryGetValue("stepper", out var stepperManifest);
        var actualStepper = _stepperIdentity is not null &&
                            FirmwareTargetProfile.FromSerial(_stepperIdentity.SerialNumber)?.Id == "stepper"
            ? _stepperIdentity
            : null;
        var authorization = _manifestService.ValidateConnectedCards(_asmIdentity, asmManifest, actualStepper, stepperManifest);
        _asmAuthorized = authorization.AsmAuthorized;
        _stepperAuthorized = authorization.StepperAuthorized;
        _authorized = authorization.AnyAuthorized;

        var displayed = authorization.Stepper ?? authorization.Asm;
        if (displayed is not null)
        {
            SetAuthCheck("AuthSerial", displayed.SerialMatches);
            SetAuthCheck("AuthDevice", displayed.DeviceIdMatches);
            SetAuthCheck("AuthCustomer", displayed.CustomerIdMatches);
            SetAuthCheck("AuthKey", displayed.PublicKeyMatches);
            SetAuthCheck("AuthFingerprint", displayed.FingerprintMatches);
            SetAuthCheck("AuthFirmware", displayed.FirmwarePolicyMatches);
        }

        AuthBadge.Background = new SolidColorBrush(ColorHelper.FromArgb(
            48,
            _authorized ? (byte)72 : (byte)255,
            _authorized ? (byte)199 : (byte)107,
            _authorized ? (byte)142 : (byte)107));
        AuthText.Text = authorization.AuthorizedCount switch
        {
            2 => "2 CARDS VERIFIED",
            1 when _asmAuthorized => "ASM VERIFIED",
            1 => "STEPPER VERIFIED",
            _ => "NOT AUTHORIZED"
        };

        var missing = string.Join(" + ", new[]
        {
            _asmIdentity is not null && asmManifest is null ? "ASM manifest" : null,
            actualStepper is not null && stepperManifest is null ? "Stepper manifest" : null
        }.Where(value => value is not null));
        if (_authorized)
        {
            var targets = string.Join(" + ", new[] { _asmAuthorized ? "ASM" : null, _stepperAuthorized ? "Stepper" : null }.Where(value => value is not null));
            SetStatus(string.IsNullOrWhiteSpace(missing)
                ? $"Verified controls enabled for {targets}."
                : $"Verified controls enabled for {targets}; import the {missing} to unlock the other card.");
        }
        else
        {
            SetStatus(string.IsNullOrWhiteSpace(missing)
                ? "Connected card manifests did not pass identity verification."
                : $"Controls remain locked: import the {missing}.");
        }
    }

    private async Task<StepperIdentity?> TryReadIdentityAsync(byte slaveId, string expectedProfile)
    {
        try
        {
            var identity = await _stepperModbus.ReadIdentityAsync(slaveId);
            return FirmwareTargetProfile.FromSerial(identity.SerialNumber)?.Id == expectedProfile ? identity : null;
        }
        catch { return null; }
    }

    private async void DisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        StopRainbowSweep(false);
        _inputPollTimer.Stop();
        await _stepperModbus.DisconnectAsync();
        UpdateCustomerIdStatus(null);
        _connected = false; _authorized = _asmAuthorized = _stepperAuthorized = false; _asmIdentity = _stepperIdentity = null; _customerIdSession = null; ResetProvisioningPhases(); _deviceProfile = "none"; ConnectionDot.Fill = Brush("ErrorBrush"); ConnectionText.Text = "Disconnected"; DeviceTypeText.Text = "No cards"; ClearCardIdentityHeaders(); AuthText.Text = "NOT AUTHORIZED"; AuthBadge.Background = new SolidColorBrush(ColorHelper.FromArgb(51, 43, 55, 70)); DisconnectButton.IsEnabled = false; SetStatus("Disconnected. Authorization and hardware writes were cleared.");
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
        _authorized = true;
        _asmAuthorized = _asmIdentity is not null;
        _stepperAuthorized = _stepperIdentity is not null && FirmwareTargetProfile.FromSerial(_stepperIdentity.SerialNumber)?.Id == "stepper";
        MasterPasswordBox.Password = string.Empty; AuthText.Text = "SERVICE OVERRIDE"; AuthBadge.Background = new SolidColorBrush(ColorHelper.FromArgb(48, 244, 184, 96)); SettingsPopup.IsOpen = false; SetStatus("Temporary service override active for this connection.");
    }

    private void SetStatus(string message) => StatusText.Text = $"{DateTime.Now:HH:mm:ss}  {message}";

    private void UpdateCustomerIdStatus(string? customerId, bool pendingFlash = false)
    {
        if (string.IsNullOrWhiteSpace(customerId) || customerId == "0000000000")
        {
            CustomerIdStatusText.Text = $"{_firmwareTarget.Id.ToUpperInvariant()}  CUST ID  0000000000  /  UNPROVISIONED";
            CustomerIdStatusText.Foreground = Brush("WarningBrush");
            return;
        }

        CustomerIdStatusText.Text = pendingFlash
            ? $"{_firmwareTarget.Id.ToUpperInvariant()}  CUST ID  {customerId}  /  PENDING FLASH"
            : $"{_firmwareTarget.Id.ToUpperInvariant()}  CUST ID  {customerId}  /  FLASHED";
        CustomerIdStatusText.Foreground = pendingFlash ? Brush("AccentBrush") : Brush("SuccessBrush");
    }

    private void SetCardIdentityHeader(string profileId, string serialNumber, string customerId, bool pendingFlash = false)
    {
        var isAsm = string.Equals(profileId, "asm", StringComparison.OrdinalIgnoreCase);
        var group = isAsm ? AsmIdentityGroup : StepperIdentityGroup;
        var serial = isAsm ? AsmSerialTopText : StepperSerialTopText;
        var customer = isAsm ? AsmCustomerIdTopText : StepperCustomerIdTopText;
        group.Visibility = Visibility.Visible;
        serial.Text = serialNumber;
        customer.Text = customerId;
        customer.Foreground = pendingFlash
            ? Brush("AccentBrush")
            : customerId == "0000000000" ? Brush("WarningBrush") : Brush("TextPrimaryBrush");
        UpdateWindowTitleSerials();
    }

    private void ClearCardIdentityHeaders()
    {
        AsmIdentityGroup.Visibility = Visibility.Collapsed;
        StepperIdentityGroup.Visibility = Visibility.Collapsed;
        AsmSerialTopText.Text = StepperSerialTopText.Text = "—";
        AsmCustomerIdTopText.Text = StepperCustomerIdTopText.Text = "0000000000";
        UpdateWindowTitleSerials();
    }

    private void UpdateWindowTitleSerials()
    {
        Title = FormatWindowTitle(
            DisplayVersion,
            AsmIdentityGroup.Visibility == Visibility.Visible ? AsmSerialTopText.Text : null,
            StepperIdentityGroup.Visibility == Visibility.Visible ? StepperSerialTopText.Text : null);
    }

    internal static string FormatWindowTitle(string version, string? asmSerial, string? stepperSerial)
    {
        var cards = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(asmSerial)) cards.Add($"ASM {asmSerial}");
        if (!string.IsNullOrWhiteSpace(stepperSerial)) cards.Add($"Stepper {stepperSerial}");
        var suffix = cards.Count == 0 ? string.Empty : $"  —  {string.Join("  |  ", cards)}";
        return $"◆ Unified Test & Keygen Dashboard  v{version}{suffix}";
    }

    private void FirmwareTarget_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox box || box.SelectedItem is not ComboBoxItem item) return;
        var requested = item.Tag?.ToString() ?? "stepper";
        if (_connected && _deviceProfile != "both" && !string.Equals(requested, _deviceProfile, StringComparison.OrdinalIgnoreCase))
        {
            box.SelectedIndex = _deviceProfile == "asm" ? 1 : 0;
            SetStatus("Firmware target must match the connected hardware profile. Disconnect before changing it.");
            return;
        }
        var selected = FirmwareTargetProfile.FromId(requested);
        SetFirmwareTarget(selected);
        SetStatus($"Selected firmware target: {selected.DisplayName}. Reflash will use {selected.LocalRoot}.");
    }

    private void SetFirmwareTarget(FirmwareTargetProfile profile)
    {
        _firmwareTarget = profile;
        _firmwareProvisioning = new FirmwareProvisioningService(profile);
        _maintenanceReflashRequested = false;
        _maintenanceExpectedCdi = null;
        _maintenanceManifest = null;
        _maintenanceManifestPath = null;
        _maintenanceElfSha256 = null;
        _maintenanceTarget = null;
        _maintenanceProvisioning = null;
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

    private void StartInputPolling()
    {
        var interval = double.IsNaN(LiveStatusIntervalBox.Value) ? 300 : Math.Clamp(LiveStatusIntervalBox.Value, 100, 5000);
        _inputPollTimer.Interval = TimeSpan.FromMilliseconds(interval);
        _inputPollTimer.Start();
    }

    private async void InputPollTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (_inputPollInProgress || !_connected || _asmIdentity is null || !_stepperModbus.IsConnected) return;
        _inputPollInProgress = true;
        try
        {
            var status = await _stepperModbus.ReadAsmInputStatusAsync();
            SetInputIndicator("AsmIp1", "AsmIp1Dot", (status & 0x0008) != 0);
            SetInputIndicator("AsmIp2", "AsmIp2Dot", (status & 0x0010) != 0);
            SetText("AsmModbusStatus", (status & 0x0020) != 0 ? "Error" : "Online");
        }
        catch
        {
            SetText("AsmModbusStatus", "Polling error");
        }
        finally { _inputPollInProgress = false; }
    }

    private async Task RestoreManifestAsync()
    {
        var restored = 0;
        foreach (var profile in new[] { "asm", "stepper" })
        {
            var preference = ManifestPreferenceFile(profile);
            if (!File.Exists(preference)) continue;
            var profilePath = (await File.ReadAllTextAsync(preference)).Trim();
            if (!File.Exists(profilePath)) continue;
            try
            {
                await LoadManifestAsync(profilePath, persist: false);
                restored++;
            }
            catch (Exception ex)
            {
                SetStatus($"Saved {profile} manifest could not be loaded: {ex.Message}");
            }
        }
        if (restored > 0)
        {
            SetStatus($"Restored {restored} trusted card manifest{(restored == 1 ? string.Empty : "s")}.");
            return;
        }

        var saved = File.Exists(ManifestPreferencePath) ? (await File.ReadAllTextAsync(ManifestPreferencePath)).Trim() : null;
        var path = !string.IsNullOrWhiteSpace(saved) && File.Exists(saved) ? saved : DefaultManifestPath;
        if (!File.Exists(path))
        {
            UpdateManifestSlotDisplay();
            SetStatus("No previous manifest was found. Import one, or place default_manifest.json in the manifests folder.");
            return;
        }

        try
        {
            await LoadManifestAsync(path, persist: false);
            SetStatus(path == saved ? $"Restored manifest: {path}" : $"Loaded default manifest: {path}");
        }
        catch (Exception ex)
        {
            SetStatus($"Saved/default manifest could not be loaded: {ex.Message}");
        }
    }

    private async Task LoadManifestAsync(string path, bool persist)
    {
        _loadedManifest = await _manifestService.LoadAsync(path);
        _loadedManifestPath = path;
        var profile = CardManifestService.ManifestProfile(_loadedManifest);
        _trustedManifests[profile] = _loadedManifest;
        _trustedManifestPaths[profile] = path;
        UpdateManifestSlotDisplay();
        if (persist)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ManifestPreferencePath)!);
            await File.WriteAllTextAsync(ManifestPreferencePath, path);
            await File.WriteAllTextAsync(ManifestPreferenceFile(profile), path);
        }
    }

    private void UpdateManifestSlotDisplay()
    {
        SetText("AsmManifestFileText", _trustedManifestPaths.TryGetValue("asm", out var asmPath) ? Path.GetFileName(asmPath) : "Not loaded");
        SetText("StepperManifestFileText", _trustedManifestPaths.TryGetValue("stepper", out var stepperPath) ? Path.GetFileName(stepperPath) : "Not loaded");
    }

    private async Task LoadKnownManifestsForConnectedCardsAsync()
    {
        foreach (var (profile, identity) in new[] { ("asm", _asmIdentity), ("stepper", GetDetectedStepperIdentity()) })
        {
            if (identity is null) continue;
            if (_trustedManifests.TryGetValue(profile, out var existing) && _manifestService.Validate(identity, existing).IsAuthorized)
                continue;
            var databasePath = Path.Combine(CdiStorageService.DefaultDatabaseRoot, identity.SerialNumber, "lic_files", $"{identity.SerialNumber}_manifest.json");
            if (!File.Exists(databasePath)) continue;
            try { await LoadManifestAsync(databasePath, persist: true); }
            catch (Exception ex) { SetStatus($"{profile} manifest auto-load failed: {ex.Message}"); }
        }
    }

    private StepperIdentity? GetDetectedStepperIdentity() =>
        _stepperIdentity is not null && FirmwareTargetProfile.FromSerial(_stepperIdentity.SerialNumber)?.Id == "stepper"
            ? _stepperIdentity
            : null;

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
            SetCardIdentityHeader(_firmwareTarget.Id, _customerIdSession.SerialNumber, _customerIdSession.CustomerId, pendingFlash: true);
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
        var identityProfile = FirmwareTargetProfile.FromSerial(effectiveSerial)?.Id ?? _firmwareTarget.Id;
        SetCardIdentityHeader(identityProfile, effectiveSerial, effectiveCustomerId, customerIdOverride is not null);
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

        _stepperIdentity = await _stepperModbus.ReadIdentityAsync(_firmwareTarget.Id == "asm" ? _stepperModbus.AsmSlaveId : _stepperModbus.StepperSlaveId);
        ApplyStepperIdentity(_stepperIdentity, _customerIdSession?.CustomerId, _customerIdSession?.SerialNumber);
        if (_deviceProfile == "stepper") await RefreshStepperLiveStatusAsync();
        SetStatus($"{_firmwareTarget.DisplayName} identity refreshed.");
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
        var activeInputs = new List<string>();
        if (status.Ip1) activeInputs.Add("IP1"); if (status.Ip2) activeInputs.Add("IP2"); if (status.EncZ) activeInputs.Add("ENC_Z"); if (status.EncZRaw) activeInputs.Add("ENC_Z raw");
        SetText("StepperInputSummary", activeInputs.Count == 0 ? "None active" : string.Join(" · ", activeInputs));
    }

    private bool CanControlStepper()
    {
        if (!_connected || !_stepperModbus.IsConnected) { SetStatus("Stepper control blocked: connect through global Modbus settings first."); return false; }
        if (_stepperIdentity is null || FirmwareTargetProfile.FromSerial(_stepperIdentity.SerialNumber)?.Id != "stepper") { SetStatus("Stepper control blocked: no Stepper card was detected at its configured slave ID."); return false; }
        if (!_stepperAuthorized) { SetStatus("Stepper control blocked: verify a manifest matching the Stepper card."); return false; }
        return true;
    }

    private async Task RunStepperCommandAsync(string operation, Func<Task> command)
    {
        if (!CanControlStepper()) return;
        try { await command(); await Task.Delay(80); await RefreshStepperLiveStatusAsync(); SetStatus($"Stepper {operation} accepted and register readback passed."); }
        catch (Exception ex) { SetStatus($"Stepper {operation} failed: {ex.Message}"); }
    }

    private static int I32(NumberBox? box, string label)
    {
        if (box is null || double.IsNaN(box.Value) || box.Value < int.MinValue || box.Value > int.MaxValue) throw new InvalidOperationException($"Enter a valid {label}.");
        return checked((int)box.Value);
    }

    private static uint U32(NumberBox? box, string label)
    {
        if (box is null || double.IsNaN(box.Value) || box.Value < uint.MinValue || box.Value > uint.MaxValue) throw new InvalidOperationException($"Enter a valid {label}.");
        return checked((uint)box.Value);
    }

    private Task MoveStepperRelativeAsync() => RunStepperCommandAsync("relative move", () => _stepperModbus.MoveStepperRelativeAsync(I32(FindNameInPages<NumberBox>("StepperRelative"), "relative target")));
    private Task MoveStepperAbsoluteAsync() => RunStepperCommandAsync("absolute move", () => _stepperModbus.MoveStepperAbsoluteAsync(U32(FindNameInPages<NumberBox>("StepperAbsolute"), "absolute target")));
    private Task JogStepperAsync(bool positive) => RunStepperCommandAsync(positive ? "positive jog" : "negative jog", async () =>
    {
        var chunk = U16(FindNameInPages<NumberBox>("StepperJogChunk"), "jog chunk");
        await _stepperModbus.SetStepperJogChunkAsync(chunk);
        await _stepperModbus.JogStepperAsync(positive);
    });
    private Task ResetStepperPositionAsync() => RunStepperCommandAsync("position reset", () => _stepperModbus.ResetStepperPositionAsync());
    private Task StartStepperHomingAsync() => RunStepperCommandAsync("homing", () => _stepperModbus.StartStepperHomingAsync());

    private async Task RefreshStepperDashboardAsync()
    {
        if (!CanControlStepper()) return;
        try { await RefreshStepperLiveStatusAsync(); SetStatus("Stepper inputs, position, command, status and fault refreshed."); }
        catch (Exception ex) { SetStatus($"Stepper live refresh failed: {ex.Message}"); }
    }

    private async Task ReadStepperConfigurationAsync()
    {
        if (!CanControlStepper()) return;
        try
        {
            var config = await _stepperModbus.ReadStepperConfigurationAsync(); ApplyStepperConfiguration(config); await RefreshStepperLiveStatusAsync();
            SetStatus("Stepper drive setup and live status synchronized.");
        }
        catch (Exception ex) { SetStatus($"Stepper setup refresh failed: {ex.Message}"); }
    }

    private Task WriteStepperConfigurationAsync() => RunStepperCommandAsync("configuration", async () =>
    {
        var config = ReadStepperConfigurationFromUi(); await _stepperModbus.WriteStepperConfigurationAsync(config);
        var readback = await _stepperModbus.ReadStepperConfigurationAsync();
        if (readback != config) throw new InvalidDataException("Configuration readback does not match the requested values.");
        ApplyStepperConfiguration(readback);
    });

    private StepperDriveConfiguration ReadStepperConfigurationFromUi()
    {
        ushort bits = 0;
        if (FindNameInPages<ToggleSwitch>("StepperInvertDirection")?.IsOn == true) bits |= 0x0001;
        if (FindNameInPages<ToggleSwitch>("StepperSwapJog")?.IsOn == true) bits |= 0x0002;
        if (FindNameInPages<ToggleSwitch>("StepperEncLimit")?.IsOn == true) bits |= 0x0004;
        if (FindNameInPages<ToggleSwitch>("StepperEncActiveHigh")?.IsOn == true) bits |= 0x0008;
        return new StepperDriveConfiguration(U16(FindNameInPages<NumberBox>("StepperMicrostep"), "microstep"), U16(FindNameInPages<NumberBox>("StepperPpr"), "pulses/rev"),
            U16(FindNameInPages<NumberBox>("StepperAcceleration"), "acceleration"), U16(FindNameInPages<NumberBox>("StepperDeceleration"), "deceleration"), U16(FindNameInPages<NumberBox>("StepperVelocity"), "velocity"),
            U16(FindNameInPages<NumberBox>("StepperJogChunk"), "jog chunk"), bits, U16(FindNameInPages<NumberBox>("StepperHomeChunk"), "home chunk"), U16(FindNameInPages<NumberBox>("StepperDeadband"), "deadband"),
            U16(FindNameInPages<NumberBox>("StepperHomingSpeed"), "homing speed"), U16(FindNameInPages<NumberBox>("StepperDeadbandSpeed"), "deadband speed"));
    }

    private void ApplyStepperConfiguration(StepperDriveConfiguration config)
    {
        SetNumber("StepperMicrostep", config.Microstep); SetNumber("StepperPpr", config.PulsesPerRevolution); SetNumber("StepperAcceleration", config.Acceleration); SetNumber("StepperDeceleration", config.Deceleration);
        SetNumber("StepperVelocity", config.Velocity); SetNumber("StepperJogChunk", config.JogChunk); SetNumber("StepperHomeChunk", config.HomeChunk); SetNumber("StepperDeadband", config.DeadbandChunk);
        SetNumber("StepperHomingSpeed", config.HomingSpeed); SetNumber("StepperDeadbandSpeed", config.DeadbandSpeed);
        if (FindNameInPages<ToggleSwitch>("StepperInvertDirection") is { } a) a.IsOn = (config.ConfigBits & 1) != 0;
        if (FindNameInPages<ToggleSwitch>("StepperSwapJog") is { } b) b.IsOn = (config.ConfigBits & 2) != 0;
        if (FindNameInPages<ToggleSwitch>("StepperEncLimit") is { } c) c.IsOn = (config.ConfigBits & 4) != 0;
        if (FindNameInPages<ToggleSwitch>("StepperEncActiveHigh") is { } d) d.IsOn = (config.ConfigBits & 8) != 0;
    }

    private bool CanControlAsm()
    {
        if (!_connected || !_stepperModbus.IsConnected) { SetStatus("ASM control blocked: connect through the global Modbus settings first."); return false; }
        if (_asmIdentity is null) { SetStatus("ASM control blocked: no ASM card was detected at its configured slave ID."); return false; }
        if (!_asmAuthorized) { SetStatus("ASM control blocked: verify a manifest matching the ASM card."); return false; }
        return true;
    }

    private static ushort U16(NumberBox? box, string label)
    {
        if (box is null || double.IsNaN(box.Value) || box.Value < 0 || box.Value > ushort.MaxValue)
            throw new InvalidOperationException($"Enter a valid {label} value.");
        return checked((ushort)box.Value);
    }

    private async Task RunAsmCommandAsync(string operation, Func<Task> command)
    {
        if (!CanControlAsm()) return;
        try { await command(); await RefreshAsmStateAsync(false); SetStatus($"ASM {operation} applied and verified."); }
        catch (Exception ex) { SetStatus($"ASM {operation} failed: {ex.Message}"); }
    }

    private Task ApplyAsmPwmAsync() => RunAsmCommandAsync("PWM",
        () => _stepperModbus.WriteAsmPwmAsync(U16(FindNameInPages<NumberBox>("AsmPwm1"), "PWM1"), U16(FindNameInPages<NumberBox>("AsmPwm2"), "PWM2"), U16(FindNameInPages<NumberBox>("AsmFrequency"), "frequency")));

    private Task ApplyAsmOutputsAsync() => RunAsmCommandAsync("digital outputs",
        () => _stepperModbus.WriteAsmOutputsAsync(FindNameInPages<ToggleSwitch>("AsmBlower")?.IsOn == true, FindNameInPages<ToggleSwitch>("AsmOnboardLed")?.IsOn == true));

    private async void AsmOutput_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncingAsmControls || sender is not ToggleSwitch toggle) return;
        if (!CanControlAsm())
        {
            _syncingAsmControls = true;
            toggle.IsOn = toggle.Name == "AsmBlower" ? _lastBlowerOn : _lastOnboardLedOn;
            _syncingAsmControls = false;
            return;
        }
        if (toggle.Name == "AsmBlower") UpdateFanAnimation(toggle.IsOn);
        await ApplyAsmOutputsAsync();
    }

    private Task UpdateAsmLedAsync() => RunAsmCommandAsync("selected LED", () =>
    {
        var color = Ws2812Channels(_selectedWs2812Color);
        return _stepperModbus.UpdateAsmLedAsync(U16(FindNameInPages<NumberBox>("AsmLedAddress"), "LED address"), color.R, color.G, color.B);
    });

    private Task UpdateAllAsmLedsAsync() => RunAsmCommandAsync("all LEDs", () =>
    {
        var color = Ws2812Channels(_selectedWs2812Color);
        return _stepperModbus.UpdateAllAsmLedsAsync(color.R, color.G, color.B);
    });

    private Task SetAsmBrightnessAsync() => RunAsmCommandAsync("brightness",
        () => _stepperModbus.SetAsmBrightnessAsync(U16(FindNameInPages<NumberBox>("AsmLedBrightness"), "brightness")));

    private Task ClearAsmLedsAsync() => RunAsmCommandAsync("LED clear", () => _stepperModbus.ClearAsmLedsAsync());

    private void StartRainbowSweep()
    {
        if (!CanControlAsm() || _rainbowCts is not null) return;
        var intervalBox = FindNameInPages<NumberBox>("AsmRainbowInterval");
        var interval = intervalBox is null || double.IsNaN(intervalBox.Value) ? 180 : Math.Clamp(intervalBox.Value, 80, 2000);
        _rainbowCts = new CancellationTokenSource();
        _ = RunRainbowSweepAsync(TimeSpan.FromMilliseconds(interval), _rainbowCts.Token);
        SetStatus($"WS2812 rainbow sweep started · 8 LEDs · {interval:0} ms/frame.");
    }

    private void StopRainbowSweep(bool announce = true)
    {
        if (_rainbowCts is null) return;
        _rainbowCts.Cancel();
        _rainbowCts.Dispose();
        _rainbowCts = null;
        if (announce) SetStatus("WS2812 rainbow sweep stopped.");
    }

    private async Task RunRainbowSweepAsync(TimeSpan frameInterval, CancellationToken cancellationToken)
    {
        var phase = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                for (ushort address = 0; address < 8; address++)
                {
                    var color = RainbowColor((phase + address * 32) & 0xFF);
                    await _stepperModbus.UpdateAsmLedAsync(address, color.R, color.G, color.B, cancellationToken);
                }
                phase = (phase + 8) & 0xFF;
                await Task.Delay(frameInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _rainbowCts?.Dispose(); _rainbowCts = null;
            SetStatus($"WS2812 rainbow sweep stopped: {ex.Message}");
        }
    }

    private static (ushort R, ushort G, ushort B) RainbowColor(int position)
    {
        if (position < 85) return ((ushort)(255 - position * 3), (ushort)(position * 3), 0);
        if (position < 170)
        {
            position -= 85;
            return (0, (ushort)(255 - position * 3), (ushort)(position * 3));
        }
        position -= 170;
        return ((ushort)(position * 3), 0, (ushort)(255 - position * 3));
    }

    private async Task RefreshAsmStateAsync(bool announce = true)
    {
        if (!CanControlAsm()) return;
        try
        {
            var state = await _stepperModbus.ReadAsmStateAsync();
            SetText("AsmPwm1Applied", state.Pwm1Applied.ToString()); SetText("AsmPwm2Applied", state.Pwm2Applied.ToString());
            SetText("AsmFrequencyApplied", $"{state.FrequencyAppliedHz} Hz");
            SetText("AsmModbusStatus", state.CommunicationError ? "Error" : "Online");
            SetInputIndicator("AsmIp1", "AsmIp1Dot", state.Ip1Active); SetInputIndicator("AsmIp2", "AsmIp2Dot", state.Ip2Active);
            SetText("AsmWsBusy", state.Ws2812Busy ? "Busy" : "Idle"); SetText("AsmPrescaler", state.Prescaler.ToString()); SetText("AsmClockDivision", state.ClockDivision.ToString());
            SetText("AsmPedals", state.Pedal1Latched || state.Pedal2Latched ? $"{(state.Pedal1Latched ? "P1 " : "")}{(state.Pedal2Latched ? "P2" : "")}".Trim() : "None");
            SetText("AsmFirmware", $"{state.FirmwareMajor}.{state.FirmwareMinor}");
            _syncingAsmControls = true;
            _lastBlowerOn = state.BlowerOn; _lastOnboardLedOn = state.OnboardLedOn;
            if (FindNameInPages<ToggleSwitch>("AsmBlower") is { } blower) blower.IsOn = state.BlowerOn;
            if (FindNameInPages<ToggleSwitch>("AsmOnboardLed") is { } led) led.IsOn = state.OnboardLedOn;
            _syncingAsmControls = false; UpdateFanAnimation(state.BlowerOn);
            SetNumber("AsmPwm1", state.Outputs.Pwm1Duty); SetNumber("AsmPwm2", state.Outputs.Pwm2Duty); SetNumber("AsmFrequency", state.Outputs.FrequencyHz);
            SetNumber("AsmLedAddress", state.Outputs.LedAddress); SetWs2812ColorChannels(state.Outputs.Red, state.Outputs.Green, state.Outputs.Blue); SetNumber("AsmLedBrightness", state.Outputs.Brightness);
            if (announce) SetStatus("ASM inputs, outputs, timing and diagnostics synchronized.");
        }
        catch (Exception ex)
        {
            if (!announce) throw;
            SetStatus($"ASM status refresh failed: {ex.Message}");
        }
    }

    private void SetText(string name, string value) { if (FindNameInPages<TextBlock>(name) is { } text) text.Text = value; }
    private void SetNumber(string name, ushort value) { if (FindNameInPages<NumberBox>(name) is { } box) box.Value = value; }

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
            SetStatus($"Stepper setup refreshed. Microstep={registers[6]}, PPR={registers[7]}, Accel={registers[8]}, Decel={registers[9]}, Velocity={registers[10]}, Jog={registers[11]}, Status=0x{registers[12]:X4}, Fault={registers[13]}.");
        }
        catch (Exception ex)
        {
            SetStatus($"Stepper setup refresh failed: {ex.Message}");
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
        var confirmation = CustomerIdProvisioningService.NormalizeSerial(FlashSerialConfirmation.Text);
        if (!CustomerIdProvisioningService.IsValidSerial(confirmation) || confirmation[0] != _firmwareTarget.SerialPrefix)
        {
            SetStatus($"Enter any valid {_firmwareTarget.SerialPrefix}YYMMDDSS serial to confirm the default flash.");
            return;
        }
        try
        {
            SetFlashProgress(15, $"Writing serial {confirmation} into default firmware...");
            await _firmwareProvisioning.PrepareDefaultFirmwareWithSerialAsync(confirmation);
            SetFlashProgress(25, "Rebuilding default firmware with assigned serial...");
            var build = await _firmwareProvisioning.BuildAsync();
            if (!build.Succeeded || !File.Exists(_firmwareProvisioning.ElfPath))
                throw new InvalidOperationException("Default firmware rebuild with assigned serial failed.");

            if (_stepperModbus.IsConnected) await _stepperModbus.DisconnectAsync();
            _connected = false;
            _authorized = _asmAuthorized = _stepperAuthorized = false;
            _asmIdentity = _stepperIdentity = null;
            _currentCdi = null;
            _customerIdSession = null;
            StopRainbowSweep(false);
            _inputPollTimer.Stop();
            ClearCardIdentityHeaders();
            AuthText.Text = "NOT AUTHORIZED";
            AuthBadge.Background = new SolidColorBrush(ColorHelper.FromArgb(51, 43, 55, 70));
            ConnectionDot.Fill = Brush("WarningBrush");
            ConnectionText.Text = "SWD flashing";
            var flash = await _firmwareProvisioning.FlashDefaultAsync(confirmation, _firmwareTarget.SerialPrefix);
            if (!flash.Succeeded) throw new InvalidOperationException($"STM32CubeProgrammer returned exit code {flash.ExitCode}.");
            _defaultFirmwareFlashSucceeded = true;
            _defaultBaselineVerified = false;
            _defaultFirmwareBuilt = false;
            ConnectionText.Text = "Default flashed";
            DisconnectButton.IsEnabled = false;
            UpdateCustomerIdStatus(null);
            SetPhaseStatus("Phase1Status", "DEFAULT FLASHED - connect through Modbus to continue", "SuccessBrush");
            SetStatus($"Default firmware with serial {confirmation} flashed through SWD. Connect and identify the card to continue.");
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
            await LoadManifestAsync(Path.Combine(_generatedPackageFolder, $"{_currentCdi.SerialNumber}_manifest.json"), persist: true);

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

    private StepperIdentity? GetActiveFirmwareTargetIdentity(FirmwareTargetProfile? target = null)
    {
        target ??= _firmwareTarget;
        if (target.Id == "asm") return _asmIdentity;
        return _stepperIdentity is not null &&
               FirmwareTargetProfile.FromSerial(_stepperIdentity.SerialNumber)?.Id == "stepper"
            ? _stepperIdentity
            : null;
    }

    private async Task<(CardManifest Manifest, string Path)> ResolveMaintenanceManifestAsync(
        StepperIdentity identity,
        FirmwareTargetProfile target)
    {
        if (_trustedManifests.TryGetValue(target.Id, out var trustedManifest) &&
            _trustedManifestPaths.TryGetValue(target.Id, out var trustedPath))
        {
            var trustedValidation = _manifestService.Validate(identity, trustedManifest);
            if (trustedValidation.IsAuthorized &&
                string.Equals(trustedManifest.SerialNumber.Trim(), identity.SerialNumber, StringComparison.OrdinalIgnoreCase))
                return (trustedManifest, trustedPath);
        }
        if (_loadedManifest is not null && !string.IsNullOrWhiteSpace(_loadedManifestPath))
        {
            var loadedValidation = _manifestService.Validate(identity, _loadedManifest);
            if (loadedValidation.IsAuthorized &&
                string.Equals(_loadedManifest.SerialNumber.Trim(), identity.SerialNumber, StringComparison.OrdinalIgnoreCase))
                return (_loadedManifest, _loadedManifestPath);
        }

        var databasePath = Path.Combine(
            CdiStorageService.DefaultDatabaseRoot,
            identity.SerialNumber,
            "lic_files",
            $"{identity.SerialNumber}_manifest.json");
        if (!File.Exists(databasePath))
            throw new FileNotFoundException(
                "Maintenance reflash requires the existing trusted manifest. Import it in Verify board or restore it to the card's lic_files folder.",
                databasePath);

        var manifest = await _manifestService.LoadAsync(databasePath);
        if (!string.Equals(manifest.SerialNumber.Trim(), identity.SerialNumber, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Existing manifest must explicitly contain the connected card's serial number for maintenance reflash.");
        var validation = _manifestService.Validate(identity, manifest);
        if (!validation.IsAuthorized)
            throw new InvalidOperationException($"Existing manifest does not match the connected finalized card: {validation.Reason}");
        return (manifest, databasePath);
    }

    private async Task PrepareMaintenanceReflashAsync()
    {
        try
        {
            var target = _firmwareTarget;
            var provisioning = new FirmwareProvisioningService(target);
            var identity = GetActiveFirmwareTargetIdentity(target)
                ?? throw new InvalidOperationException($"Connect and identify the selected {target.DisplayName} before maintenance reflash.");
            if (identity.CustomerId10 == "0000000000")
                throw new InvalidOperationException("Maintenance reflash is only available for finalized cards. Use the default/provisioning workflow for an unprovisioned card.");

            var expectedCdi = new CardIdentityCdi
            {
                SerialNumber = identity.SerialNumber,
                DeviceId = identity.DeviceId96,
                CustomerId = identity.CustomerId10
            };
            SetStatus("Validating finalized firmware headers and existing manifest...");
            var (manifest, manifestPath) = await ResolveMaintenanceManifestAsync(identity, target);
            var headers = await provisioning.ValidateCurrentIdentityHeadersAsync(expectedCdi, identity.PublicKeyRawHex);
            if (!headers.IsValid)
                throw new InvalidOperationException($"Maintenance reflash blocked: {headers.Reason}. Select the correct finalized firmware source; no identity files were changed.");

            AppendFirmwareLog($"MAINTENANCE: finalized identity and manifest verified for {identity.SerialNumber}.");
            AppendFirmwareLog("MAINTENANCE: clean-building current source without generating or staging license files...");
            var build = await provisioning.BuildAsync();
            AppendFirmwareLog(build.Output);
            if (!build.Succeeded || !File.Exists(provisioning.ElfPath))
                throw new InvalidOperationException($"Maintenance build failed with exit code {build.ExitCode}, or the expected ELF was not produced.");

            _maintenanceExpectedCdi = expectedCdi;
            _maintenanceManifest = manifest;
            _maintenanceManifestPath = manifestPath;
            _maintenanceElfSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(provisioning.ElfPath)));
            _maintenanceTarget = target;
            _maintenanceProvisioning = provisioning;
            _maintenanceReflashRequested = true;
            _flashDefaultRequested = false;
            FlashSummaryText.Text =
                $"MAINTENANCE REFLASH{Environment.NewLine}{Environment.NewLine}" +
                $"Hardware: {target.DisplayName}{Environment.NewLine}" +
                $"Serial: {identity.SerialNumber}{Environment.NewLine}" +
                $"Customer ID: {identity.CustomerId10}{Environment.NewLine}" +
                $"Manifest: {manifestPath}{Environment.NewLine}" +
                $"Firmware: {provisioning.ElfPath}{Environment.NewLine}{Environment.NewLine}" +
                "No CDI, key, manifest, or identity header will be generated or changed.";
            FlashSerialConfirmation.Header = "Type the existing card serial to confirm";
            FlashSerialConfirmation.Text = string.Empty;
            FlashAcknowledge.IsChecked = false;
            FlashProgressPanel.Visibility = Visibility.Collapsed;
            FinalFlashButton.IsEnabled = true;
            PositionFlashSummary();
            FlashSummaryPopup.IsOpen = true;
            SetStatus($"Maintenance firmware built for {identity.SerialNumber}. Review and confirm the reflash.");
        }
        catch (Exception ex)
        {
            _maintenanceReflashRequested = false;
            _maintenanceExpectedCdi = null;
            _maintenanceManifest = null;
            _maintenanceManifestPath = null;
            _maintenanceElfSha256 = null;
            _maintenanceTarget = null;
            _maintenanceProvisioning = null;
            AppendFirmwareLog($"MAINTENANCE PREPARE BLOCKED: {ex.Message}");
            SetStatus(ex.Message);
        }
    }

    private void ShowDefaultFlashSummary()
    {
        if (!_defaultFirmwareBuilt)
        {
            SetStatus("Prepare default firmware before flashing it.");
            return;
        }
        _maintenanceReflashRequested = false;
        _flashDefaultRequested = true;
        var modbusIdentity = _stepperModbus.IsConnected && _stepperIdentity is not null
            ? $"Serial {_stepperIdentity.SerialNumber}"
            : "Unavailable - SWD-only blank card";
        FlashSummaryText.Text = $"DEFAULT FIRMWARE{Environment.NewLine}{Environment.NewLine}Hardware: {_firmwareTarget.DisplayName}{Environment.NewLine}Modbus identity: {modbusIdentity}{Environment.NewLine}Confirmation: any valid {_firmwareTarget.SerialPrefix}YYMMDDSS serial{Environment.NewLine}Firmware: {_firmwareProvisioning.ElfPath}";
        FlashSerialConfirmation.Header = $"Enter {_firmwareTarget.SerialPrefix}YYMMDDSS serial to confirm";
        FlashSerialConfirmation.Text = string.Empty;
        FlashAcknowledge.IsChecked = false;
        FlashProgressPanel.Visibility = Visibility.Collapsed;
        FinalFlashButton.IsEnabled = true;
        PositionFlashSummary();
        FlashSummaryPopup.IsOpen = true;
    }

    private void ShowFlashSummary()
    {
        if (!_publicKeyPrepared || _currentCdi is null || string.IsNullOrWhiteSpace(_generatedPackageFolder))
        {
            SetStatus("Generate the package and stage firmware before final flash.");
            return;
        }
        _maintenanceReflashRequested = false;
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
            if (_maintenanceReflashRequested)
            {
                FlashProgressBar.IsIndeterminate = true;
                FlashProgressText.Text = "Reflashing finalized firmware, reconnecting, and verifying identity...";
                await FlashMaintenanceAndVerifyAsync();
                return;
            }
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

    private async Task FlashMaintenanceAndVerifyAsync()
    {
        if (_maintenanceExpectedCdi is null || _maintenanceManifest is null ||
            string.IsNullOrWhiteSpace(_maintenanceManifestPath) || string.IsNullOrWhiteSpace(_maintenanceElfSha256) ||
            _maintenanceTarget is null || _maintenanceProvisioning is null)
        {
            SetFlashProgress(0, "Maintenance reflash is not prepared.");
            SetStatus("Prepare the finalized maintenance firmware again before flashing.");
            return;
        }
        if (FlashAcknowledge.IsChecked != true)
        {
            SetFlashProgress(0, "Physical target acknowledgement is required.");
            SetStatus("Acknowledge the live-device flashing warning before continuing.");
            return;
        }

        var confirmation = FlashSerialConfirmation.Text?.Trim() ?? string.Empty;
        if (!string.Equals(confirmation, _maintenanceExpectedCdi.SerialNumber, StringComparison.Ordinal))
        {
            SetFlashProgress(0, $"Type {_maintenanceExpectedCdi.SerialNumber} exactly.");
            SetStatus($"Type the exact existing serial {_maintenanceExpectedCdi.SerialNumber} to confirm the maintenance target.");
            return;
        }
        if (PortBox.SelectedItem is not SerialPortDescriptor selectedPort)
        {
            SetFlashProgress(0, "Select a COM port for post-flash verification.");
            SetStatus("Select the card COM port before maintenance flashing.");
            return;
        }

        var expectedCdi = _maintenanceExpectedCdi;
        var expectedManifest = _maintenanceManifest;
        var expectedManifestPath = _maintenanceManifestPath;
        var target = _maintenanceTarget;
        var provisioning = _maintenanceProvisioning;
        var targetProfile = target.Id;
        var baud = int.Parse(((ComboBoxItem)BaudBox.SelectedItem).Content.ToString()!);
        var asmSlave = checked((byte)Math.Round(AsmSlaveIdBox.Value));
        var stepperSlave = checked((byte)Math.Round(StepperSlaveIdBox.Value));
        try
        {
            var activeIdentity = GetActiveFirmwareTargetIdentity(target)
                ?? throw new InvalidOperationException("The selected finalized card is no longer connected.");
            RequireAssignedIdentityReadback(activeIdentity, expectedCdi);
            var currentHeaders = await provisioning.ValidateCurrentIdentityHeadersAsync(expectedCdi, activeIdentity.PublicKeyRawHex);
            if (!currentHeaders.IsValid)
                throw new InvalidOperationException($"Firmware identity headers changed after the maintenance build: {currentHeaders.Reason}");
            var currentElfHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(provisioning.ElfPath)));
            if (!string.Equals(currentElfHash, _maintenanceElfSha256, StringComparison.Ordinal))
                throw new InvalidOperationException("The built firmware artifact changed after review. Prepare the maintenance reflash again.");

            SetFlashProgress(20, "Disconnecting Modbus and probing ST-LINK...");
            StopRainbowSweep(false);
            _inputPollTimer.Stop();
            if (_stepperModbus.IsConnected) await _stepperModbus.DisconnectAsync();
            _connected = false;
            _authorized = _asmAuthorized = _stepperAuthorized = false;
            ConnectionDot.Fill = Brush("WarningBrush");
            ConnectionText.Text = "SWD flashing";
            AuthText.Text = "VERIFY REQUIRED";
            AppendFirmwareLog($"MAINTENANCE FLASH AUTHORIZED by exact serial {confirmation}.");

            var flash = await provisioning.FlashAsync(confirmation, expectedCdi);
            AppendFirmwareLog(flash.Output);
            if (!flash.Succeeded)
                throw new InvalidOperationException($"STM32CubeProgrammer returned exit code {flash.ExitCode}.");

            SetFlashProgress(75, "Firmware written. Reconnecting to the shared Modbus bus...");
            await Task.Delay(1500);
            await _stepperModbus.OpenAsync(selectedPort.PortName, baud);
            _stepperModbus.AsmSlaveId = asmSlave;
            _stepperModbus.StepperSlaveId = stepperSlave;
            var actualAsm = await TryReadIdentityAsync(asmSlave, "asm");
            var actualStepper = await TryReadIdentityAsync(stepperSlave, "stepper");
            var targetReadback = targetProfile == "asm" ? actualAsm : actualStepper;
            if (targetReadback is null)
                throw new InvalidOperationException($"Flashed {targetProfile.ToUpperInvariant()} card did not return a valid identity on its configured Modbus slave.");

            RequireAssignedIdentityReadback(targetReadback, expectedCdi);
            var validation = _manifestService.Validate(targetReadback, expectedManifest);
            if (!validation.IsAuthorized)
                throw new InvalidOperationException($"Firmware was written, but final manifest verification failed: {validation.Reason}");

            _asmIdentity = actualAsm;
            _stepperIdentity = actualStepper ?? actualAsm;
            _deviceProfile = actualAsm is not null && actualStepper is not null
                ? "both"
                : actualAsm is not null ? "asm" : "stepper";
            _loadedManifest = expectedManifest;
            _loadedManifestPath = expectedManifestPath;
            _trustedManifests[targetProfile] = expectedManifest;
            _trustedManifestPaths[targetProfile] = expectedManifestPath;
            UpdateManifestSlotDisplay();
            _asmAuthorized = targetProfile == "asm";
            _stepperAuthorized = targetProfile == "stepper";
            _connected = true;
            ApplyStepperIdentity(targetReadback);
            ClearCardIdentityHeaders();
            if (actualAsm is not null) SetCardIdentityHeader("asm", actualAsm.SerialNumber, actualAsm.CustomerId10);
            if (actualStepper is not null) SetCardIdentityHeader("stepper", actualStepper.SerialNumber, actualStepper.CustomerId10);
            ApplyManifestValidation(validation);
            _asmAuthorized = targetProfile == "asm";
            _stepperAuthorized = targetProfile == "stepper";
            _authorized = true;
            ConnectionDot.Fill = Brush("SuccessBrush");
            ConnectionText.Text = "Connected";
            DisconnectButton.IsEnabled = true;
            DeviceTypeText.Text = _deviceProfile == "both" ? "ASM + Stepper cards" : target.DisplayName;
            if (actualAsm is not null) StartInputPolling();
            AppendFirmwareLog($"MAINTENANCE REFLASH VERIFIED: {targetReadback.SerialNumber}, Customer {targetReadback.CustomerId10}, fingerprint {targetReadback.PublicKeyFingerprintSha256}.");
            SetFlashProgress(100, "Finalized firmware reflashed and identity verified. License files were unchanged.");
            SetStatus($"{target.DisplayName} {targetReadback.SerialNumber} reflashed and verified without regenerating license files.");
        }
        catch (Exception ex)
        {
            if (_stepperModbus.IsConnected) await _stepperModbus.DisconnectAsync();
            _connected = false;
            _authorized = _asmAuthorized = _stepperAuthorized = false;
            _asmIdentity = _stepperIdentity = null;
            _deviceProfile = "none";
            ClearCardIdentityHeaders();
            ConnectionDot.Fill = Brush("ErrorBrush");
            ConnectionText.Text = "Disconnected";
            DisconnectButton.IsEnabled = false;
            AuthText.Text = "VERIFY REQUIRED";
            AuthBadge.Background = new SolidColorBrush(ColorHelper.FromArgb(48, 255, 107, 107));
            AppendFirmwareLog($"MAINTENANCE FLASH/VERIFY FAILED: {ex.Message}");
            SetFlashProgress(0, "Maintenance flash or verification failed. Reconnect and inspect status.");
            SetStatus($"Maintenance reflash failed: {ex.Message}");
        }
        finally
        {
            _maintenanceReflashRequested = false;
            _maintenanceExpectedCdi = null;
            _maintenanceManifest = null;
            _maintenanceManifestPath = null;
            _maintenanceElfSha256 = null;
            _maintenanceTarget = null;
            _maintenanceProvisioning = null;
        }
    }

    private void SetFlashProgress(double value, string message)
    {
        FlashProgressPanel.Visibility = Visibility.Visible;
        FlashProgressBar.Value = value;
        FlashProgressText.Text = message;
    }

    private void CloseFlashSummary_Click(object sender, RoutedEventArgs e)
    {
        FlashSummaryPopup.IsOpen = false;
        _maintenanceReflashRequested = false;
        _maintenanceExpectedCdi = null;
        _maintenanceManifest = null;
        _maintenanceManifestPath = null;
        _maintenanceElfSha256 = null;
        _maintenanceTarget = null;
        _maintenanceProvisioning = null;
    }

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
            var readback = await _stepperModbus.ReadIdentityAsync(
                _firmwareTarget.Id == "asm" ? _stepperModbus.AsmSlaveId : _stepperModbus.StepperSlaveId);
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
        var slave = checked((byte)Math.Round(_firmwareTarget.Id == "asm" ? AsmSlaveIdBox.Value : StepperSlaveIdBox.Value));
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
        var slave = checked((byte)Math.Round(_firmwareTarget.Id == "asm" ? AsmSlaveIdBox.Value : StepperSlaveIdBox.Value));
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
