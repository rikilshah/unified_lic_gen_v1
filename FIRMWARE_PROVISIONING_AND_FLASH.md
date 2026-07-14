# Unified Firmware Provisioning and Flash

## Status

The dashboard provides one minimal four-phase workflow for both cards: read or restore the default card, create and save identity, generate the complete key package and stage firmware, then review and flash once.

Authoritative firmware source: [rikilshah/stepper_control_card_v2](https://github.com/rikilshah/stepper_control_card_v2)

Expected local checkout for the currently implemented tooling: `D:\stm32_vscode\stepper_control_card_v2`. The GitHub repository is the source of truth; the local checkout must be synchronized before preparing or building firmware.

ASM firmware source: [rikilshah/VCB240002](https://github.com/rikilshah/VCB240002), expected at `D:\stm32_vscode\VCB240002`.

## Operator Workflow

1. Select the correct hardware profile, connect, and open **Firmware provisioning**. The target remains locked to the detected card family.
2. **Phase 1 — Default baseline:** accept a connected card already reporting Customer ID `0000000000`. Restore and build default firmware when programming is needed. Confirm with the exact live Modbus serial when available; when blank hardware cannot respond, use `000000000`. Connect after programming and require blank readback before identity creation.
3. **Phase 2 — Identity:** enter the assigned serial, generate or recover its Customer ID, and save CDI JSON under the serial target folder. Nothing is flashed.
4. **Phase 3 — Keys and staging:** generate the complete P-256 package from CDI, including the sensitive private-key PEM, save all outputs under `lic_files`, stage all final identity headers, and clean-build. Nothing is flashed.
5. **Phase 4 — Final flash:** review the floating summary, confirm the target, flash the one final image, reconnect, and verify the complete live identity against the persisted pair and generated manifest.

Every flash requires a successful build and ST-LINK probe, physical-target acknowledgement, and the exact currently detected card serial. Once Phase 2 flashing starts, the persisted serial/Customer ID pair is final truth and retries reuse it.

The status bar reports failures while the floating flash window shows the current destructive-operation stage.

The operator-facing UI shows one wizard step at a time. During final programming, the floating review window visually reports confirmation, ST-LINK probing, programming/reconnect, and verification instead of exposing a persistent main-page log.

The four phases and hardware control/test workspaces share one sidebar shell and one global Modbus session. Flash reconnect uses the same COM port, baud, and slave ID selected for control and testing.

Customer ID generation and recovery are specified in [CUSTOMER_ID_PROVISIONING_SOP.md](CUSTOMER_ID_PROVISIONING_SOP.md).

## Files and Tools

The preparation stage updates:

- `Core\Inc\card_public_key.h`
- `Core\Inc\customer_id_config.h`
- `Core\Inc\serial_number_config.h`

Existing headers are first copied to `D:\stm32_vscode\stepper_control_card_v2\.unified-backups\<serial>\<timestamp>\`.

Defaults:

- CMake: `C:\ST\STM32CubeCLT_1.15.0\CMake\bin\cmake.exe`
- STM32 Programmer: `C:\ST\STM32CubeCLT_1.15.0\STM32CubeProgrammer\bin\STM32_Programmer_CLI.exe`
- Stepper: configure/build preset `MinSizeRel`; `build\MinSizeRel\STEPPER_CONTROL_CARD_V2.elf`
- ASM: configure/build preset `Release`; `build\Release\VCB240002_2_0.elf`
- Every build configures the selected preset first, then runs `--clean-first`.

## Safety Gates

- Phase 1 preparation requires a synchronized local `origin/main`; it does not invent default header values.
- Phase 1 default flashing requires the selected hardware profile, built default ELF, successful ST-LINK/RDP probe, and physical-target acknowledgement. Confirmation is the exact valid serial read through Modbus, or `000000000` only when no Modbus identity is available.
- A generated CDI and manifest package must exist before Phase 3 public-key preparation.
- Preparation, clean build, and ST-LINK probe must succeed in the current session.
- The operator must acknowledge the physical target warning.
- Confirmation text must exactly equal the intended card serial.
- RDP level 1 or 2 is rejected. The dashboard never changes option bytes or attempts an automatic unlock because that may erase the target.
- Programmer exit code zero is insufficient: the board must reconnect through Modbus and pass manifest identity validation.
- The operator-assigned serial and generated Customer ID are persisted before header modification and cannot be silently replaced after flashing begins.
- A second serial assignment for the same MCU UID and hardware profile is rejected.
- The firmware target cannot be changed away from the connected hardware profile.

## Failure Recovery

- **Prepare fails:** generate/export the package and verify the firmware `Core\Inc` folder.
- **Build fails:** review the operation log; flash stays disabled.
- **Detection fails:** check ST-LINK USB, SWD wiring, target power, reset wiring, and programmer ownership.
- **Flash fails:** capture the full log and retry detection. Do not bypass RDP safeguards.
- **Reconnect fails:** verify the saved COM port, baud, and slave ID.
- **Identity fails:** do not authorize the board. Compare staged serial, customer ID, and public key with the manifest, then rebuild.

## Verification Record

Verification was performed without programming a physical board:

- Unified dashboard build: successful with zero warnings and errors.
- Automated tests: 25 passed.
- Stepper clean MinSizeRel build from `main`: 24,180 bytes flash (73.79%), 3,448 bytes RAM (84.18%).
- ASM clean Release build from `main`: 14,180 bytes flash (43.27%), 1,696 bytes RAM (41.41%).
- ST-LINK: STM32F03x detected at 3.27 V.

No flash command was executed during automated implementation verification.

## Hardware Validation Log

| Date | Result | Evidence |
| --- | --- | --- |
| 2026-07-13 | Operator accepted | The complete Unified Dashboard workflow was run on the intended hardware and reported working, including firmware provisioning and the guarded flash flow. |

This is the current validated baseline. Future behavior or UI changes should be recorded as new entries without replacing this result.
