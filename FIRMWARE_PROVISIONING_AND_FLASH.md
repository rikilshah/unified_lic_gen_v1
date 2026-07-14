# Stepper Firmware Provisioning and Flash

## Status

The Unified Test & Keygen Dashboard now provides a guarded end-to-end firmware workflow for the Stepper Control Card. It prepares identity headers, builds firmware, detects the SWD target, flashes the image, reconnects to Modbus, and validates the identity readback.

Firmware source: `D:\stm32_vscode\stepper_control_card_v2`

This does not add firmware flashing for the ASM card. The two card families remain separate hardware modules even though they share the CDI, key-generation, manifest, and authorization workflow.

## Operator Workflow

1. Connect to the Stepper card and read its CDI identity.
2. Generate and export the manifest package from Keygen & Provisioning.
3. Open **Firmware provisioning**.
4. Select **Prepare identity headers**.
5. Select **Clean build firmware**. Flash remains unavailable if the build fails.
6. Connect the intended board through ST-LINK and select **Detect target**.
7. Confirm that the physical target is safe to program, then type the exact card serial shown by the dashboard.
8. Select **Flash firmware and verify**.
9. The dashboard disconnects Modbus, programs and starts the ELF, reconnects to the saved COM settings, loads the generated manifest, and compares the live card identity. Only a successful readback is reported as complete.

The operation log records every stage and the external tool output needed to diagnose a failure.

## Files and Tools

The preparation stage updates:

- `Core\Inc\card_public_key.h`
- `Core\Inc\customer_id_config.h`
- `Core\Inc\serial_number_config.h`

Existing headers are first copied to `D:\stm32_vscode\stepper_control_card_v2\.unified-backups\<serial>\<timestamp>\`.

Defaults:

- CMake: `C:\ST\STM32CubeCLT_1.15.0\CMake\bin\cmake.exe`
- STM32 Programmer: `C:\ST\STM32CubeCLT_1.15.0\STM32CubeProgrammer\bin\STM32_Programmer_CLI.exe`
- Build preset: `MinSizeRel`, always with `--clean-first`
- Flash image: `build\MinSizeRel\STEPPER_CONTROL_CARD_V2.elf`

## Safety Gates

- A generated CDI and manifest package must exist before preparation.
- Preparation, clean build, and ST-LINK probe must succeed in the current session.
- The operator must acknowledge the physical target warning.
- Confirmation text must exactly equal the intended card serial.
- RDP level 1 or 2 is rejected. The dashboard never changes option bytes or attempts an automatic unlock because that may erase the target.
- Programmer exit code zero is insufficient: the board must reconnect through Modbus and pass manifest identity validation.

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
- Automated tests: 7 passed, including header backup/staging and serial-confirmation rejection.
- Stepper clean MinSizeRel build: successful.
- Firmware: 24,696 bytes of 32 KB flash (75.37%).
- RAM: 3,440 bytes of 4 KB (83.98%).
- ST-LINK: STM32F03x detected at 3.27 V.

No flash command was executed during automated implementation verification.

## Hardware Validation Log

| Date | Result | Evidence |
| --- | --- | --- |
| 2026-07-13 | Operator accepted | The complete Unified Dashboard workflow was run on the intended hardware and reported working, including firmware provisioning and the guarded flash flow. |

This is the current validated baseline. Future behavior or UI changes should be recorded as new entries without replacing this result.
