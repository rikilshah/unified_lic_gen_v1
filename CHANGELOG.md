# Changelog

All notable changes to UnifiedLicGen are documented here.

## [Unreleased]

### Added

- One shared COM session can now detect and operate an ASM card and a Stepper card at separate slave addresses.
- ASM controls now include live input polling, immediate output toggles, visual input/fan feedback, a compact WS2812 color picker, per-pixel/all-pixel updates, brightness control, and a cancellable rainbow sweep.
- The last trusted manifest is restored at startup, with an optional packaged `manifests/default_manifest.json` fallback.
- WS2812 color selection has regression coverage to preserve exact RGB channel mapping.
- The native title bar lists the serial number of every detected card, while the application header groups each card's serial and Customer ID together.
- Finalized cards can be rebuilt and reflashed through a separate maintenance action without regenerating CDI, keys, manifests, or identity headers.

### Changed

- Each connected hardware family retains an independent authorization gate, so a manifest can authorize its matching card without unlocking another card on the same bus.
- Control workspaces use denser typography, spacing, grouping, and operator-focused labels while the Admin provisioning wizard remains available.
- The global header uses aligned, card-specific identity groups instead of one ambiguous shared serial/Customer ID pair.
- Firmware reconnect selects the configured slave address for the active ASM or Stepper firmware target.
- Maintenance reflash requires exact live-card, firmware-header, trusted-manifest, typed-serial, and post-flash identity agreement; it also rejects a changed ELF after operator review.

## [2.0.0] - 2026-07-16

### Added

- Four-phase blank-card provisioning wizard for ASM and Stepper hardware.
- Generated Customer ID, CDI JSON, P-256 key package, firmware staging, flashing, and exact readback verification.
- Manifest-based board authorization with persistent public verification status.
- Complete ASM PWM, digital I/O, WS2812, coil, status, and diagnostic register controls.
- Complete Stepper positioning, jog, homing, drive configuration, driver flags, discrete input, status, and fault controls.
- Standalone Windows x64 publishing profile and public application version display.

### Changed

- Shared one serialized global Modbus session across provisioning, verification, ASM controls, Stepper controls, and testing.
- Reorganized both hardware dashboards into compact task-based control groups.
- Established the GitHub dashboard and firmware repositories as sources of truth.

### Fixed

- Corrected ASM and Stepper hardware detection using the established serial prefixes.
- Corrected the default Modbus slave ID to firmware address 1.
- Embedded the operator-entered serial number in default firmware before programming.
- Added fail-closed motion busy guards, configuration readback, and ASM output readback.
