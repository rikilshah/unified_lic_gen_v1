# Changelog

All notable changes to UnifiedLicGen are documented here.

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
