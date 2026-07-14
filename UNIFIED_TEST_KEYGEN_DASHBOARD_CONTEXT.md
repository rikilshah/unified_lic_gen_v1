# Unified Test and Keygen Dashboard — Project Context and Implementation Plan

Date: 2026-07-13  
Source projects: `D:\vs_dotnet\key_genui_v1`, `D:\vs_dotnet\modbus_dash_asm_v1`, `D:\vs_dotnet\modbus_dash_v1`

Authoritative repositories:

- dashboard: [rikilshah/unified_lic_gen_v1](https://github.com/rikilshah/unified_lic_gen_v1);
- Stepper firmware: [rikilshah/stepper_control_card_v2](https://github.com/rikilshah/stepper_control_card_v2);
- ASM firmware: [rikilshah/VCB240002](https://github.com/rikilshah/VCB240002).

## 1. Objective

Build one well-designed WinUI 3 desktop application for the complete card lifecycle:

1. discover and connect to a card;
2. read and verify its identity;
3. export identity data for provisioning;
4. generate its ECDSA P-256 key package;
5. import the resulting provisioning manifest;
6. authenticate the connected card;
7. run the correct functional tests and controls for that card type;
8. save an auditable test result.

The application should unify the operator experience and shared infrastructure. It must not combine the two incompatible device register maps into one service.

## 2. Executive Summary of the Existing Projects

| Project | Primary purpose | Mature capabilities | Main limitation to address |
| --- | --- | --- | --- |
| `key_genui_v1` | Local card key generation and provisioning-package export | Identity JSON validation, P-256 key generation, public-key formats, fingerprinting, safe package export and backups | Separate wizard/application; targets .NET 10 while dashboards target .NET 8 |
| `modbus_dash_asm_v1` | Test/control dashboard for an ASM I/O card | Manifest auth, identity display/export, PWM, blower, WS2812, input/event status, config presets, serial monitoring | Code-behind architecture, no automated tests, device-specific register map |
| `modbus_dash_v1` / `ModBus_Ctrl_v2` | Test/control dashboard for a stepper card | Manifest auth, stepper move/jog/home/config, safety guards, identity export, config profiles, tests | Code-behind architecture and device-specific motion register map |

The reusable center is: serial discovery, Modbus session lifecycle, identity reconstruction, provisioning manifest validation, public-key fingerprinting, authorization state, logging, configuration persistence, and consistent operator feedback.

## 3. Project Context

### 3.1 `key_genui_v1` — Provisioning and Key Generation

This is a standalone WinUI 3 utility targeting `net10.0-windows10.0.19041.0`. Most domain behavior is concentrated in `Services/KeyPackageService.cs`, with a wizard-style UI in `Views/MainPage`.

#### Input contract

The imported JSON contains:

```json
{
  "serial_no": "A26050601",
  "devid": "123456789ABCDEF087654321",
  "custid": "9428577894"
}
```

Normalization and validation rules:

- serial: one letter plus eight digits; normalize the letter to uppercase;
- device ID: exactly 24 hexadecimal characters (96 bits) after removing accepted separators and `0x`;
- customer ID: exactly 10 decimal digits;
- supported aliases exist for all three fields;
- any missing or malformed required value blocks generation.

#### Cryptographic outputs

- ECDSA P-256 (`secp256r1` / `nistP256`) key pair;
- raw public key: 64 bytes, `X || Y`;
- SEC1 public key: 65 bytes, `04 || X || Y`;
- fingerprint: uppercase `SHA-256` of the decoded 64 raw public-key bytes;
- optional PEM private key, exported only after explicit sensitive-key consent.

The fingerprint must never be calculated over ASCII hex or over the 65-byte SEC1 representation.

#### Export contract

The chosen parent directory receives a `lic_files` child folder. Before overwrite, existing top-level package files are moved to `lic_files\bak\<timestamp_ms>\`.

The package contains:

- `<serial>_manifest.json`;
- `<serial>_pubkey_raw.hex`;
- `<serial>_pubkey_sec1.hex`;
- `<serial>_pubkey_fingerprint.txt`;
- `<serial>_pubkey_registers_be.txt`;
- `card_public_key.h` with 32 big-endian 16-bit public-key words;
- optional `<serial>_app_privkey_sensitive.pem`;
- `README_v1_0.md`.

The manifest contains public values only. Private key material must never be displayed by default or written into the manifest.

### 3.2 `modbus_dash_asm_v1` — ASM Card Test Dashboard

This is a WinUI 3 application targeting .NET 8, using NModbus over serial RTU. It controls an I/O-oriented card with PWM, blower, addressable LEDs, and digital inputs. It is explicitly not the stepper-motion application.

#### Connection and lifecycle

- Modbus RTU over RS-485;
- default slave ID `1`, baud `38400`, `8N1`;
- raw `SerialPort.GetPortNames()` is authoritative; registry metadata provides optional friendly names;
- one service owns the connection and serializes Modbus operations;
- serial removal and health failures are monitored;
- controls remain disabled until authentication succeeds.

#### Identity and authentication

The app reads:

- STM32 UID from input registers `4..9`;
- 10-digit customer ID from `10..15`;
- raw P-256 public key from `16..47`;
- firmware version from `48..49`;
- hardware revision, serial number, and product code from `52..58`.

It validates device ID, customer ID, raw key, and SHA-256 fingerprint against a provisioning manifest. Optional serial, firmware, and backend authorization checks may further restrict access. Any parse, read, mismatch, backend, or transport failure denies access.

#### Device controls

- PWM1 and PWM2 duty (`0..1023`);
- PWM frequency with applied-value readback;
- blower control;
- WS2812 pixel address, RGB values, brightness, update, and clear;
- live status for blower, board heartbeat LED, WS2812 activity, IP1/IP2, and Modbus errors;
- optional pedal/event coil reads;
- save/load dashboard presets.

The board LED is firmware-owned as a communication heartbeat and should not be presented as a normal controllable output.

#### Known technical condition

The app is code-behind driven, has no MVVM layer, and has no automated test project. PWM validation constants require continued alignment with firmware. A complete LED-strip state cannot be reconstructed after reconnect because firmware lacks full per-pixel readback.

### 3.3 `modbus_dash_v1` / `ModBus_Ctrl_v2` — Stepper Test Dashboard

This is a WinUI 3 .NET 8 desktop application for a stepper-motion controller. It uses NModbus Serial, WinUIEx, COM friendly-name discovery, polling, JSON profiles, manifest authorization, and temporary service unlock.

#### Connection and lifecycle

- Modbus RTU over RS-485;
- default slave ID `1`, baud `38400`, `8N1`;
- Modbus operations are serialized through a `SemaphoreSlim`;
- blocking serial operations run away from the UI thread;
- live status is normally polled at approximately 300 ms;
- disconnect stops polling/watchdogs and clears authorization.

#### Identity and authentication

Identity is reconstructed from input registers:

- position `0..1` and active command `2..3`;
- STM32 UID `4..9`;
- customer ID `10..15`;
- raw P-256 public key `16..47`;
- firmware version `48..49`.

The project imports a provisioning manifest and fail-closed validates device ID, customer ID, raw public key, and fingerprint before enabling motion. It can export card identity JSON for Keygen. A temporary master unlock exists for service use and is reset on disconnect.

#### Device controls

- relative and absolute moves;
- positive and negative jog;
- immediate position/home reset;
- homing sequence using `ENC_Z`;
- velocity, acceleration, deceleration, microstep, pulses/revolution, jog chunk, home/deadband values;
- direction and input behavior flags;
- live busy, jogging, mode, homing, fault, position, command, and discrete-input status;
- save/load drive profiles.

Motion writes are guarded by a fresh status read before issuing a command. Paired 32-bit register values are low-word first: `(high << 16) | low`.

#### Verification and risks

The repository includes xUnit coverage for identity reconstruction, manifests, aliases, key validation, exports, and master unlock. Documented risks include clean polling shutdown, awaited observer restart, and possible register coherence concerns between firmware IRQ writes and reads.

## 4. Shared Contracts That Must Become One Implementation

### 4.1 Canonical identity model

Use one immutable normalized model throughout the application:

```csharp
public sealed record DeviceIdentity(
    string SerialNumber,
    string DeviceId96,
    string CustomerId10,
    string PublicKeyRawHex,
    string PublicKeySec1Hex,
    string PublicKeyFingerprintSha256,
    ushort FirmwareMajor,
    ushort FirmwareMinor,
    ushort? HardwareRevision,
    ushort? ProductCode);
```

Not every source initially supplies every value. Imported CDI JSON supplies serial/device/customer values; a live authenticated session supplies the public key and firmware metadata; generated packages supply all manifest key fields.

### 4.2 Canonical authorization rule

Privileged controls are enabled only when:

```text
connected
AND manifest_is_valid
AND card_uid == manifest_uid
AND card_customer_id == manifest_customer_id
AND card_raw_public_key == hex_decode(manifest_raw_public_key)
AND SHA256(card_raw_public_key) == manifest_fingerprint
AND optional_serial_check_passes
AND optional_firmware_check_passes
AND optional_backend_check_allows
```

Disconnect, device change, manifest change, communication failure, or test cancellation must invalidate authorization. Private keys are not part of dashboard/card comparison.

### 4.3 Device-specific register maps

Do not create one static register class containing both devices. Define a device module contract and give each module its own protocol implementation:

```csharp
public interface IDeviceModule
{
    string Id { get; }
    string DisplayName { get; }
    Task<DeviceProbeResult> ProbeAsync(IModbusSession session, CancellationToken ct);
    Task<DeviceIdentity> ReadIdentityAsync(IModbusSession session, CancellationToken ct);
    IReadOnlyList<TestDefinition> Tests { get; }
    object CreateControlViewModel(DeviceSession session);
}
```

Initial modules:

- `AsmDeviceModule`: PWM, blower, inputs, WS2812 and applied-value checks;
- `StepperDeviceModule`: motion, jog, home, drive configuration and safety checks.

Auto-detection should use stable product code, hardware revision, firmware signature, or a safe read-only probe. If detection is ambiguous, ask the operator to select the device profile; never probe by writing.

## 5. Proposed Solution Architecture

Target one supported runtime across all projects, preferably .NET 10 if deployment machines support it; otherwise move Keygen down to .NET 8. Do not mix target frameworks inside the first unified executable without a deliberate compatibility reason.

```text
UnifiedLicGen.sln
├── UnifiedLicGen.App                 WinUI shell, navigation, DI, themes
├── UnifiedLicGen.Core                identity, manifests, auth, test/result models
├── UnifiedLicGen.Crypto              P-256 generation, formats, fingerprint, export
├── UnifiedLicGen.Modbus              serial discovery and serialized RTU session
├── UnifiedLicGen.Device.Asm          ASM register map, commands, tests, UI module
├── UnifiedLicGen.Device.Stepper      stepper register map, commands, tests, UI module
└── UnifiedLicGen.Tests               unit, protocol, package, workflow tests
```

Recommended boundaries:

- UI view models depend on interfaces, never on `SerialPort` or NModbus directly.
- `IModbusSession` owns the port, master, timeout, retry policy, semaphore, and disposal.
- each device module converts registers to typed snapshots and exposes meaningful commands;
- `ProvisioningService` owns CDI parsing, aliases, normalization, and manifest validation;
- `KeyPackageService` owns key generation and atomic/safe artifact export;
- `AuthorizationService` produces a structured decision with per-field results;
- `TestRunner` executes cancellable tests and records measurements/evidence;
- a session state machine is the only authority for navigation and command enablement.

Suggested application state machine:

```text
Disconnected
  -> Connecting
  -> ConnectedUnidentified
  -> Identified
  -> AwaitingManifest | ProvisioningRequired
  -> Authorizing
  -> Authorized
  -> Testing
  -> TestComplete

Any state -> CommunicationError -> Disconnected
Any auth mismatch -> Denied (controls disabled)
```

## 6. Dashboard UX Proposal

Use a left `NavigationView` with a persistent top status strip. Avoid exposing all engineering controls on the landing page.

### 6.1 Persistent status strip

Always show:

- connection badge and COM port;
- detected card type;
- serial number and short fingerprint;
- authorization badge (`Not checked`, `Authorized`, `Denied`, `Service override`);
- current test state;
- a prominent Disconnect/E-stop-safe action appropriate to the device.

### 6.2 Pages

#### Overview

Operator-oriented starting page with a guided checklist:

1. select/refresh COM port;
2. connect and identify;
3. load or generate license package;
4. authorize;
5. run recommended tests;
6. export report.

Use large status cards and one clear primary action. Show technical register details only in expandable diagnostics.

#### Device Identity

Show normalized serial, UID, customer ID, firmware, product/hardware revision, raw key, and fingerprint. Provide `Export CDI JSON` and copy actions. Each field should show read status and source.

#### Keygen & Provisioning

Integrate the current three-step wizard:

- import CDI JSON or use identity from the connected card;
- validate and generate/regenerate P-256 keys;
- select TEST/PRODUCTION metadata and export the package.

Private-key export belongs in a collapsed Advanced section, off by default, with explicit confirmation and a visible warning. After export, offer to load the newly generated manifest into the current session.

#### Authorization

Import a manifest and display a comparison table:

| Check | Manifest | Connected card | Result |
| --- | --- | --- | --- |
| Serial (when present) | value | value | pass/fail |
| Device ID | value | value | pass/fail |
| Customer ID | value | value | pass/fail |
| Raw public key | abbreviated | abbreviated | pass/fail |
| Fingerprint | value | value | pass/fail |
| Firmware/backend | requirement | observed | pass/fail |

Display a human-readable deny reason while retaining detailed error data in logs. Service override must be clearly marked, time/session scoped, and auditable—not visually equivalent to manifest authorization.

#### Firmware Provisioning

For the Stepper Control Card, provide a guarded workflow that stages generated identity headers into the firmware source, performs a clean MinSizeRel build, probes the SWD target, flashes the ELF, reconnects over Modbus, and validates live identity against the generated manifest. Keep this on a dedicated page with an operation log.

Flash requires all prior stages, physical-target acknowledgement, and exact entry of the card serial. Never change RDP option bytes automatically; an unlock can mass-erase the target. See [FIRMWARE_PROVISIONING_AND_FLASH.md](FIRMWARE_PROVISIONING_AND_FLASH.md) for the implemented flow.

#### Test Center

Select tests based on the detected module. Support `Run recommended`, individual tests, cancel, retry failed, and export report.

ASM test groups:

- communication and identity;
- authorization;
- input status/IP1/IP2;
- blower on/off with status confirmation;
- PWM channel setpoint and applied readback;
- WS2812 pixel/update/clear (operator visual confirmation where readback is unavailable);
- configuration save/load round trip.

Stepper test groups:

- communication and identity;
- authorization;
- discrete inputs and fault status;
- configuration read/write/readback;
- low-risk relative move;
- absolute move;
- positive/negative jog;
- home reset/homing sequence;
- busy-state and conflicting-command safety guards.

Potentially moving hardware must require an operator acknowledgement, conservative defaults, visible live status, cancellation/stop handling, and precondition checks. Tests must not run motion automatically merely because authorization passed.

#### Manual Controls

Render the appropriate device module only after identification. Disable all writes until authorized. Keep requested and applied values visually distinct. Group dangerous motion actions separately from configuration.

#### Reports & Logs

Record card identity, manifest fingerprint, app/module versions, port/settings, test timestamps, measurements, pass/fail/skip, operator notes, authorization method, and error details. Export JSON plus a readable Markdown or HTML summary. Never include private-key material.

#### Settings & Diagnostics

Include serial defaults, polling intervals, log location, advanced raw register diagnostics (read-only by default), theme, and support bundle export. Arbitrary writes should require a separate engineering mode and are outside the first release.

## 7. Test Model

Represent tests as data-backed operations rather than button handlers:

```csharp
public sealed record TestDefinition(
    string Id,
    string Name,
    RiskLevel Risk,
    bool RequiresAuthorization,
    bool RequiresOperatorConfirmation,
    Func<TestContext, CancellationToken, Task<TestResult>> ExecuteAsync);
```

A result should include status, start/end time, expected value, actual value, units, error code, operator confirmation, and structured evidence. This allows the same tests to power the UI, batch runs, and reports.

## 8. Safety and Security Requirements

- Fail closed on malformed files, incomplete identity reads, mismatches, unknown devices, timeouts, and backend errors.
- Never store a private key in a manifest, report, log, recent-files preview, or normal application settings.
- Zero/dispose sensitive key buffers where practical and avoid keeping generated private keys after export.
- Keep production private-key export disabled by default.
- Do not imply that host-side authorization secures the Modbus bus. Existing ASM documentation states the UI is the gatekeeper; another Modbus master on the bus can still command firmware.
- Invalidate auth on reconnect or identity change.
- Serialize all Modbus frames and separate polling from command transactions.
- Pause or coordinate polling during multi-step command/test sequences.
- Require fresh status before stepper motion and block conflicting operations while busy/homing/faulted.
- Use atomic package writes and retain the existing timestamped backup behavior.
- Replace the documented default `admin` service password before production; store only an appropriate verifier or use Windows/operator authentication.

- Treat firmware flashing as destructive: require prepare/build/probe gates, explicit acknowledgement, and exact serial confirmation.
- Refuse automatic option-byte or RDP changes. A protected target requires a separate, reviewed recovery procedure.
- Do not report flash success until the board reconnects and its live identity matches the session manifest.

## 9. Implementation Phases

### Phase 1 — Foundation

- create the solution and align target framework/package versions;
- extract canonical identity, manifest, fingerprint, and validation models;
- create serial scanner and single serialized Modbus session abstraction;
- add structured logging and application/session state machine;
- port existing identity and manifest tests.

Exit criterion: a test harness can connect, read a typed identity through a selected module, and produce an authorization decision without UI code owning Modbus calls.

### Phase 2 — Unified shell and authorization

- build the NavigationView shell, status strip, Overview, Identity, and Authorization pages;
- implement read-only device probing and explicit fallback selection;
- invalidate authorization correctly on every session transition;
- add field-by-field authorization results.

Exit criterion: both card types can be identified and authorized in one executable, with all writes still gated.

### Phase 3 — Keygen integration

- port CDI import/aliases and `KeyPackageService` behind interfaces;
- preserve all artifact names, big-endian key words, backups, and README generation;
- enable connected-card identity as wizard input;
- support immediate loading of the exported manifest.

Exit criterion: an operator can connect, export/use identity, generate a package, load its manifest, and authorize without switching applications.

### Phase 4 — Device control modules

- port ASM typed snapshots/commands and UI;
- port stepper typed snapshots/commands and UI;
- retain range checks, applied readback, motion guards, and resource lifecycle fixes;
- remove duplicated code-behind orchestration.

Exit criterion: feature parity with both existing dashboards through module-specific pages.

### Phase 5 — Test Center and reporting

- implement test definitions, runner, cancellation, evidence, and result persistence;
- add recommended ASM and stepper suites;
- add operator confirmation steps and report export.

Exit criterion: each supported card can complete a repeatable, auditable test workflow.

### Phase 6 — Hardening and release

- unit-test normalization, crypto formats, register decoding, auth, and test decisions;
- use fake Modbus transports for sequencing, timeout, cancellation, and mismatch tests;
- run hardware-in-loop acceptance tests on both card types;
- verify x64 deployment first, then x86/ARM64 only if required;
- publish as a folder/MSIX-compatible layout, not a fragile single-file WinUI binary.

### Phase 7 - Guarded Stepper firmware provisioning

- stage `card_public_key.h`, `customer_id_config.h`, and `serial_number_config.h` with timestamped backups;
- run a clean MinSizeRel build and retain tool output in the UI log;
- detect ST-LINK and reject protected RDP states without changing option bytes;
- require acknowledgement and exact serial confirmation before programming;
- reconnect over Modbus after reset and compare live identity with the generated manifest.

Exit criterion: the dashboard completes a confirmed Stepper update and only declares success after identity readback passes.

## 10. Migration Map

| Existing component | Unified destination | Action |
| --- | --- | --- |
| Keygen `KeyPackageService` | `UnifiedLicGen.Crypto` | Extract and retain artifact contract; split parsing, crypto, and file export responsibilities |
| Three `ProvisioningManifest` models | `UnifiedLicGen.Core` | Replace with one canonical model plus tolerant JSON aliases |
| Identity/fingerprint code in dashboards | `UnifiedLicGen.Core` | Consolidate and extend existing tested stepper implementation |
| Dashboard `ModbusService` classes | `UnifiedLicGen.Modbus` + device modules | Share transport only; retain separate register maps and commands |
| Serial scanners/observers | `UnifiedLicGen.Modbus` | Consolidate, using raw COM enumeration as source of truth |
| JSON config services | Core/device modules | Share storage mechanics; retain device-specific profile schemas |
| ASM `MainPage` code-behind | ASM view models/services | Decompose into session, auth, snapshot, and command responsibilities |
| Stepper `MainWindow` code-behind | Stepper view models/services | Decompose while preserving motion guards and dispatcher safety |
| Existing stepper xUnit tests | `UnifiedLicGen.Tests` | Port first as regression protection |
| ASM manual-only verification | `UnifiedLicGen.Tests` | Add register decode, range, command-bit, and auth tests |

## 11. Acceptance Criteria for Version 1

- One installed application supports both existing card families and local Keygen.
- Operator can complete connect → identify → provision/load manifest → authorize → test → report from the dashboard.
- Device type is detected read-only or explicitly selected when ambiguous.
- No device write is possible before authorization except explicitly designed read-only discovery.
- All four mandatory identity checks are visible and enforced.
- Key output is byte-for-byte compatible with the existing firmware/dashboard contracts.
- ASM controls retain requested/applied readback semantics.
- Stepper controls retain fresh-status motion guards and busy/fault gating.
- Disconnect or transport failure immediately disables writes and cancels/ends active tests safely.
- Reports contain traceable identity and results but no secrets.
- Unit tests cover shared contracts and both register maps; hardware acceptance covers both physical cards.

- Stepper flashing is gated by preparation, clean build, target probe, acknowledgement, and exact serial entry.
- A flashed Stepper is accepted only after Modbus reconnect and live identity validation against the generated manifest.

## 12. Decisions Required Before Coding

1. Choose the common target framework (.NET 8 for current dashboard compatibility or .NET 10 for the new baseline).
2. Define a reliable read-only product discriminator for automatic ASM/stepper detection.
3. Decide whether Keygen is visible to all operators or restricted to a provisioning role.
4. Replace or formally redesign the temporary master-unlock mechanism for production.
5. Decide the test-report format and required operator/audit fields.
6. Define safe motion test distances/speeds and the required physical safety acknowledgement.
7. Decide whether backend authorization is part of version 1 or remains an optional module.

## 13. Recommended First Release Scope

For the lowest-risk useful release, include the unified shell, serial connection, identity, manifest authorization, embedded Keygen workflow, guarded Stepper firmware provisioning, manual ASM/stepper controls, recommended test suites, and JSON/Markdown reports. Defer arbitrary register writes, remote fleet management, unattended or batch firmware flashing, and generalized plugin loading until the core lifecycle is stable on real hardware.

## 14. Pending Blank-Card Customer ID Provisioning

The agreed SOP is recorded in [CUSTOMER_ID_PROVISIONING_SOP.md](CUSTOMER_ID_PROVISIONING_SOP.md). Both firmware repositories are available; implementation remains intentionally paused until their header contracts, build/flash procedures, and identity register maps are reviewed.
