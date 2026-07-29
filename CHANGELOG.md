# Changelog

All notable changes to UnifiedLicGen are documented here.

## [2.0.3] - 2026-07-29

### Fixed

- Hardened the portable Windows package so incomplete extraction is detected before .NET or WinUI starts instead of failing with a missing `System.Private.CoreLib.dll` or Windows App Runtime error.
- Moved Windows App SDK self-contained initialization into the application project so every supported publish path includes the private WinUI runtime.

### Changed

- Updated Windows App SDK to the latest 1.8 servicing release.
- Portable releases now keep the runtime payload under `app`, provide a checked root launcher, and include per-file plus archive SHA-256 manifests.

## [2.0.2] - 2026-07-27

### Changed

- Matched the Technician dashboard by aligning the ASM blower, communication heartbeat, IP1, and IP2 elements in one compact two-column I/O grid.

## [2.0.1] - 2026-07-27

### Added

- One shared COM session can now detect and operate an ASM card and a Stepper card at separate slave addresses.
- ASM controls now include live input polling, immediate output toggles, visual input/fan feedback, a compact WS2812 color picker, per-pixel/all-pixel updates, brightness control, and a cancellable rainbow sweep.
- The last trusted manifest is restored at startup, with an optional packaged `manifests/default_manifest.json` fallback.
- WS2812 color selection has regression coverage to preserve exact RGB channel mapping.
- The native title bar lists the serial number of every detected card, while the application header groups each card's serial and Customer ID together.
- Finalized cards can be rebuilt and reflashed through a separate maintenance action without regenerating CDI, keys, manifests, or identity headers.
- Technician v1.0.2's vertical-axis Stepper workspace, aligned field actions, live limit indicators, and footer authorization state are now shared by the Admin dashboard.
- Critical failures can surface as temporary in-app notifications without turning routine control feedback into persistent page clutter.

### Changed

- Each connected hardware family retains an independent authorization gate, so a manifest can authorize its matching card without unlocking another card on the same bus.
- Control workspaces use denser typography, spacing, grouping, and operator-focused labels while the Admin provisioning wizard remains available.
- The global header uses aligned, card-specific identity groups instead of one ambiguous shared serial/Customer ID pair.
- Firmware reconnect selects the configured slave address for the active ASM or Stepper firmware target.
- Maintenance reflash requires exact live-card, firmware-header, trusted-manifest, typed-serial, and post-flash identity agreement; it also rejects a changed ELF after operator review.
- Primary and danger actions use the compact technician sizing system, while routine status and control-access state remain in the persistent footer.

### Fixed

- Stepper live position, status, fault, command, IP1, IP2, and encoder state now continue polling when no ASM card is present.
- Repeated communication or identity failures revoke the affected card's authorization, and every hardware write revalidates the live card identity first.
- Jog distance and jog direction are written as one serialized operation; WS2812 rainbow frames use one shared frame write path.
- Manifest preference files are persisted atomically so a partial write cannot replace a previously trusted manifest path.
- Replaced the invalid onboard-LED output toggle with read-only communication heartbeat status and restricted persistent ASM writes to the blower bit.
- Made brightness changes redraw the current WS2812 frame immediately.

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
