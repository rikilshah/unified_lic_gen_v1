# Design System - Unified Test & Keygen Dashboard

## Product Context

- **What this is:** A Windows industrial service dashboard that provisions, authenticates, tests, and controls two distinct Modbus card families.
- **Who it is for:** Production technicians, firmware engineers, QA operators, and authorized service staff.
- **Project type:** Dense desktop control dashboard.

## Aesthetic Direction

- **Direction:** Material Industrial Dark.
- **Decoration:** Intentional and restrained. Elevation communicates hierarchy; color communicates state.
- **Mood:** Precise, calm, trustworthy, and fast to scan under production-floor conditions.

## Typography

- **Display/UI:** Segoe UI Variable, native to Windows and highly legible in WinUI.
- **Data:** Cascadia Mono for identifiers, register values, keys, and measurements.
- **Scale:** 12 caption, 13 label, 14 body, 16 section, 20 page title, 28 primary metric.

## Color

- Canvas: `#0B0F14`
- Navigation: `#0F141B`
- Surface: `#151B23`
- Raised surface: `#1B2430`
- Border: `#2B3746`
- Primary text: `#F2F5F8`
- Secondary text: `#9EABB9`
- Accent: `#39C6D4`
- Accent hover: `#62D8E2`
- Success: `#48C78E`
- Warning: `#F4B860`
- Error: `#FF6B6B`
- Info: `#6EA8FE`

Color is semantic. Cyan means an operator action or selected context; green means verified; amber means attention; red means fault, denial, or destructive action.

## Spacing and Shape

- Base unit: 4 px.
- Density: Compact-comfortable.
- Scale: 4, 8, 12, 16, 24, 32.
- Radius: 4 px controls, 8 px cards, 12 px floating drawer.
- Minimum interactive target: 36 px; critical actions: 44 px.

## Layout

- Persistent 252 px left navigation rail.
- Persistent 64 px session/status header.
- Scrollable page canvas with a maximum comfortable reading width.
- Primary controls remain visible; configuration is placed in a right-side floating settings drawer.
- Requested and applied hardware values are always visually distinct.

## Motion

- Minimal-functional.
- Drawer enter/exit: 180 ms ease-out/ease-in.
- State changes use color and text together, never color alone.
- Avoid decorative animation during live hardware operation.

## UX Rules

- Hardware writes are disabled until connection and authorization succeed.
- ASM and Stepper controls never share a register editor or command surface.
- Settings drawer contains infrequent connection, polling, configuration, and service options.
- Faults and deny reasons remain visible until acknowledged or resolved.
- Private-key export is hidden under an advanced disclosure and requires explicit confirmation.
- Firmware provisioning has a dedicated page; it is never placed in the floating settings drawer or combined with package export.
- Flash remains disabled until identity headers are prepared, a clean build succeeds, and the connected ST-LINK target is probed.
- A destructive-action warning, explicit acknowledgement, and an exact card-serial confirmation are required before flash.
- Firmware success means the board restarted, Modbus reconnected, and the live identity matched the generated manifest; process exit alone is not success.
- A blank card reporting `0000000000` receives one persisted random Customer ID; retries and restarts recover it rather than generating a replacement.
- Before generation, the operator must enter the assigned serial; Stepper uses `SYYMMDDSS` and ASM uses `AYYMMDDSS`.
- Firmware target selection is locked to the connected hardware profile, with distinct ASM and Stepper build artifacts.
- Firmware provisioning is displayed as four vertically ordered phase cards: Default Baseline, Serial & Customer ID, Public Keys, and Final Verification.
- Each phase exposes only its relevant actions, displays a persistent status, and gates the next phase on verified device readback.
- Identity and generated public-key headers are staged together and programmed in one final flash.
- The dashboard opens on the four-phase provisioning workspace rather than the generic overview, and the navigation labels it explicitly so the primary production task is immediately visible.
- Legacy dashboard destinations are removed from navigation; production operators see one minimal provisioning workspace and connection settings.
- Instructions are reduced to short action labels and persistent phase status. Detailed flash information appears only in a centered floating summary at the final decision point.
- Serial, Customer ID, CDI, keys, manifest, and firmware headers are assembled before one final flash; intermediate identity and key flashes are not part of the workflow.
- Provisioning uses a one-step-at-a-time wizard: Read Card, Create Identity, Generate + Stage, and Review + Flash. Later steps remain hidden until the prior gate passes; completed steps remain reachable with Back.
- The flash summary includes visual progress for confirmation, ST-LINK probe, programming/reconnect, and identity verification.
- A persistent compact sidebar contains the four provisioning phases, ASM controls, Stepper controls, and Test Center.
- Modbus is global application state: every sidebar panel uses the same selected COM port, baud, slave ID, serialized session, connection status, and detected card identity.
- The top session bar always displays the exact 10-digit Customer ID. The bottom status bar adds lifecycle context: default `0000000000` is `UNPROVISIONED`, a generated value is `PENDING FLASH`, and live confirmed readback is `FLASHED`.
- A dedicated `Verify board` sidebar tab sits above the control/testing workspaces. Its manifest result persists while navigating, appears in the top authorization badge, is revalidated after reconnect, and gates every control action.
- The public semantic version appears in the native window title, product header, and bottom status bar. A consistent cyan key mark identifies the application in the header/status areas.
- Phase 1 supports an explicit SWD-only recovery state because a physically blank MCU may not expose Modbus. The operator may confirm with any correctly formatted serial for the selected hardware; Modbus becomes mandatory after firmware is present.
- Automatic hardware detection uses the live serial prefix because both current firmware repositories report product code `1`: `A` selects ASM and `S` selects Stepper. Unknown prefixes require explicit operator selection.
- The serial entered for a blank/default flash is not merely confirmation: the dashboard writes it into `serial_number_config.h`, rebuilds, and only then programs the ELF.
- The provisioning canvas uses an aligned 290 px target column and flexible action column; control pages use consistent equal-width grids and compact 12/16 px spacing.

## Decisions Log

| Date | Decision | Rationale |
| --- | --- | --- |
| 2026-07-13 | Material Industrial Dark | Matches the requested Material dark direction and provides clear state hierarchy for industrial workflows. |
| 2026-07-13 | Floating settings drawer | Keeps daily control surfaces focused while retaining all engineering configuration. |
| 2026-07-13 | Separate ASM and Stepper workspaces | The hardware and register maps are different even though provisioning and authorization are shared. |
| 2026-07-13 | Guarded Stepper firmware provisioning page | Header staging, clean build, target detection, typed confirmation, flash, and identity readback form one auditable workflow. |
| 2026-07-13 | Firmware provisioning workflow accepted | The operator confirmed the implemented workflow works on the intended hardware; preserve this behavior as the baseline for later changes. |
| 2026-07-14 | GitHub repositories are authoritative | Dashboard and firmware changes must start from synchronized GitHub sources; local checkouts are working copies only. |
| 2026-07-14 | Blank-card Customer ID becomes final truth | Generate once during first provisioning, flash it, and validate readback without silently replacing it. Implementation remains pending both firmware repositories. |
| 2026-07-14 | ASM firmware source established | `rikilshah/VCB240002` is the authoritative ASM firmware repository; both firmware sources must be reviewed before Customer ID implementation. |
| 2026-07-14 | Unified blank-card provisioning implemented | ASM and Stepper share Customer ID generation, persistence, header staging, flash confirmation, and exact readback while retaining hardware-specific builds. |
| 2026-07-14 | Operator assigns card serial | Serial and generated Customer ID are persisted as one final-truth pair, flashed together, and both require exact readback. |
| 2026-07-14 | Four-phase provisioning workflow | A visible, gated sequence separates default recovery, identity assignment, public-key programming, and final read-only verification to reduce operator ambiguity and prevent skipped checks. |
| 2026-07-14 | Provisioning is the startup workspace | Opening directly on the numbered four-phase flow removes an extra navigation step and prevents the redesigned workflow from being mistaken for the unchanged overview. |
| 2026-07-14 | Generate and stage before one final flash | Saving the complete package and building all final headers before programming reduces repeated card restarts and makes the final destructive action reviewable as one transaction. |
| 2026-07-14 | Floating flash summary | Flash details and confirmation move out of the primary workspace so the normal phase UI remains minimal. |
| 2026-07-14 | Wizard and control workspaces share one sidebar shell | One visible provisioning step reduces scanning load while ASM, Stepper, and testing remain immediately reachable without creating a second Modbus owner. |
| 2026-07-14 | One global Modbus session | Provisioning and all control/test panels share transport settings, connection lifecycle, serialization, and identity to prevent COM-port contention and inconsistent state. |
| 2026-07-14 | Customer ID is persistently visible | Operators can copy the generated public Customer ID into an external database and distinguish pending host data from confirmed card readback. |
| 2026-07-14 | Board verification is global session state | Manifest verification is performed in one dedicated tab, displayed in the top bar, and consumed by both hardware control panels. |
| 2026-07-14 | Public version and app mark | Operators and support logs can identify the running build without opening file properties; the same restrained key mark anchors title/status identity. |
| 2026-07-14 | Blank-card default flash bypasses Modbus | A card without application firmware cannot provide COM identity; ST-LINK plus explicit default-flash confirmation is the correct bootstrap trust boundary. |
| 2026-07-14 | Blank default flash accepts a formatted operator serial | Phase 1 validates `AYYMMDDSS`/`SYYMMDDSS` format and hardware prefix without pretending the blank target already owns that identity. |
| 2026-07-14 | Auto-detection uses serial prefix | ASM and Stepper both report product code `1`; their established `A` and `S` serial namespaces provide the available stable discriminator. |
| 2026-07-14 | Default-flash serial is embedded before programming | Rebuilding after operator entry guarantees Modbus readback reflects the selected serial instead of a stale repository default. |
| 2026-07-16 | ASM controls wired to the shared Modbus session | PWM, blower, onboard LED, WS2812 address/color/brightness/commands, status inputs, applied values, and timer diagnostics now use the VCB240002 register map with write/readback feedback. |
| 2026-07-16 | Hardware dashboards regrouped by task | ASM is organized into PWM, digital I/O, diagnostics, and lighting; Stepper is organized into positioning, jog, reference recovery, and drive setup with advanced settings collapsed. |
| 2026-07-16 | Default Modbus slave ID corrected to 1 | Both authoritative firmware register maps use slave address 1; operators may still override it in global connection settings. |
| 2026-07-16 | Stepper controls wired to the shared Modbus session | Relative/absolute moves, jog, zero reset, homing, drive parameters, advanced homing values, driver flags, live position/command/status/fault, and all four discrete inputs now use the stepper_control_card_v2 register map with busy guards and configuration readback. |
| 2026-07-16 | Major release 2.0.0 | The unified provisioning, verification, ASM control, and Stepper control workflow is now treated as the stable second-generation application baseline. |
