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
- The default-public-key identity flash and generated-public-key flash are separate actions so operators can see exactly when cryptographic identity is introduced.
- The dashboard opens on the four-phase provisioning workspace rather than the generic overview, and the navigation labels it explicitly so the primary production task is immediately visible.
- Legacy dashboard destinations are removed from navigation; production operators see one minimal provisioning workspace and connection settings.
- Instructions are reduced to short action labels and persistent phase status. Detailed flash information appears only in a centered floating summary at the final decision point.
- Serial, Customer ID, CDI, keys, manifest, and firmware headers are assembled before one final flash; intermediate identity and key flashes are not part of the workflow.

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
