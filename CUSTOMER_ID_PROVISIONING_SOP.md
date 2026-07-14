# Blank-Card Customer ID Provisioning SOP

Status: **Implemented; physical first-flash validation pending**

Date agreed: 2026-07-14

## Purpose

Provision a blank card with an operator-assigned serial number and a new 10-digit Customer ID during its first firmware flash. Because the card is blank, this pair becomes the authoritative identity for the card and every downstream artifact.

## Authoritative Sources

- Dashboard: [rikilshah/unified_lic_gen_v1](https://github.com/rikilshah/unified_lic_gen_v1)
- Stepper firmware: [rikilshah/stepper_control_card_v2](https://github.com/rikilshah/stepper_control_card_v2)
- ASM firmware: [rikilshah/VCB240002](https://github.com/rikilshah/VCB240002)

Local source folders are working checkouts only and must be synchronized with their GitHub repositories before implementation, building, or flashing.

## Agreed Customer ID Rules

1. Generate exactly 10 decimal digits using a random generator.
2. Leading zeroes are significant because the Customer ID is a fixed-width string, not a numeric quantity.
3. Reject any candidate containing three identical consecutive digits. Examples that are invalid include `111`, `000`, and `777` anywhere in the 10-digit value.
4. Other repeated digits are allowed.
5. Use the same generation and validation rules for both hardware models. There is no test/production difference.

## Assigned Serial Rules

1. The operator supplies the serial before Customer ID generation.
2. It must use the nine-character `XYYMMDDSS` format: one letter followed by eight digits.
3. Stepper serials begin with `S`, for example `S26050606`.
4. ASM serials begin with `A`, for example `A26050605`.
5. The dashboard normalizes the prefix to uppercase and writes the value to `APP_SERIAL_NUMBER_TEXT` in `serial_number_config.h`.
6. If the MCU UID already has a persisted session, entering a different serial is rejected; the operator must recover the original assigned serial.

## Implemented Four-Phase Flow

The minimal dashboard exposes only this workflow. Each phase is gated, and the assigned identity plus generated keys are flashed together only once in the final phase.

### Phase 1 — Establish the Default Baseline

1. Attempt normal Modbus connection when the card already contains default firmware.
2. If the live Customer ID is `0000000000`, accept the detected card as the default blank baseline.
3. If a physically blank card has no Modbus firmware and cannot connect, select its hardware profile and restore `card_public_key.h`, `customer_id_config.h`, and `serial_number_config.h` from the local firmware checkout's `origin/main` without requiring Modbus identity.
4. Configure and clean-build the selected default firmware and acknowledge the physical ST-LINK target. If Modbus supplied a valid serial, type that exact serial; otherwise type the nine-digit blank-card fallback `000000000`.
5. Probe and flash entirely through SWD. No COM port, serial number, Customer ID, Modbus read, or post-flash Modbus reconnect is required for programmer success.
6. After SWD programming, connect and identify the newly responsive card through Modbus. Require Customer ID `0000000000` before Phase 2 is enabled.

### Phase 2 — Create and Save Identity

1. Enter and validate the assigned serial for the selected hardware.
2. Generate one valid Customer ID and persist it together with the assigned serial, MCU UID, and hardware profile before modifying firmware.
3. Display the assigned serial and generated Customer ID for operator review.
4. Show the generated value persistently in the shared status bar as `GENERATED - PENDING FLASH`; show a default `0000000000` card as `UNPROVISIONED`.
5. Save `<SERIAL>_CDI.json` immediately under `G:\My Drive\PCB_LIC_DB\<SERIAL>\`.
6. Retain the pair for firmware staging:

   ```c
   #define APP_CUST_ID_TEXT "1234567890"
   #define APP_SERIAL_NUMBER_TEXT "S26050606"
   ```

7. Do not flash in this phase.

### Phase 3 — Generate Package and Stage Firmware

1. Use the saved CDI to generate one P-256 key pair.
2. Write the complete known-format package to `G:\My Drive\PCB_LIC_DB\<SERIAL>\lic_files\`: manifest, raw and SEC1 public keys, fingerprint, register map, `card_public_key.h`, sensitive private-key PEM, and package README.
3. Stage `card_public_key.h`, `customer_id_config.h`, and `serial_number_config.h` into the selected firmware checkout.
4. Configure and clean-build the hardware-specific firmware once with all final identity values.
5. Do not flash in this phase.

### Phase 4 — Review, Flash Once, and Verify

1. Open the floating flash summary showing hardware, serial, Customer ID, fingerprint, package folder, and firmware artifact.
2. Require target acknowledgement and exact assigned-serial confirmation.
3. Probe ST-LINK and flash the single final firmware image.
4. Reconnect and require exact serial and Customer ID equality with the persisted session.
5. Require device ID, Customer ID, raw public key, and fingerprint to match the generated manifest before declaring success.
6. Replace the pending status-bar label with the live readback value marked `FLASHED`.

On application restart, the dashboard recovers the persisted session for the connected MCU UID and restores the highest phase that can be proven from live readback and the stored manifest.

## Final-Truth and Failure Rules

- The assigned serial and generated Customer ID become final truth once flashing begins.
- Do not generate a replacement automatically after build, flash, reconnect, or validation failure.
- A mismatch fails closed and must not authorize the card.
- A retry must reuse the same stored serial and Customer ID pair unless the operator explicitly abandons the uncommitted provisioning session before a successful flash.
- The session is persisted as `customer_id_provisioning_session.json` under the card folder in `PCB_LIC_DB`; an application restart recovers the same identity for the same serial, device ID, and hardware profile.

## Hardware Profiles

- Stepper: `MinSizeRel`, output `build/MinSizeRel/STEPPER_CONTROL_CARD_V2.elf`.
- ASM: `Release`, output `build/Release/VCB240002_2_0.elf`.
- Both publish the 10-digit Customer ID in input registers `10..15` and use `Core/Inc/customer_id_config.h`.
- The generated shared header also supplies `APP_CUST_ID_LEGACY32`, which the ASM firmware requires.

## Verification

- Dashboard build: passed with zero warnings and errors.
- Automated tests: 26 passed, including serial-or-`000000000` default-flash gating, complete private/public key package export, header staging, ID generation, serial formatting, persistence/recovery, hardware mismatch rejection, profile artifacts, and header compatibility.
- Stepper firmware clean build from synchronized `main`: passed.
- ASM firmware configure plus clean build from synchronized `main`: passed.
- No physical flash was executed during automated verification.
