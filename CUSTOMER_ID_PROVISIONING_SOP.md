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

Each phase is gated. A later phase remains unavailable until the connected card passes the previous phase's readback.

### Phase 1 — Establish the Default Baseline

1. Connect and identify the card.
2. If the live Customer ID is `0000000000`, accept the detected card as the default blank baseline.
3. If it is not blank, restore `card_public_key.h`, `customer_id_config.h`, and `serial_number_config.h` from the local firmware checkout's `origin/main`, then configure and clean-build the selected hardware profile.
4. After explicit target acknowledgement and exact live-serial confirmation, flash the default firmware.
5. Reconnect and require the Customer ID to read back as `0000000000` before Phase 2 is enabled.

### Phase 2 — Assign Serial and Customer ID

1. Enter and validate the assigned serial for the selected hardware.
2. Generate one valid Customer ID and persist it together with the assigned serial, MCU UID, and hardware profile before modifying firmware.
3. Display the assigned serial and generated Customer ID for operator review.
4. Restore the repository-default public-key header and write the assigned pair into the identity headers:

   ```c
   #define APP_CUST_ID_TEXT "1234567890"
   #define APP_SERIAL_NUMBER_TEXT "S26050606"
   ```

5. Preserve timestamped backups, configure, clean-build, probe, and flash through the guarded process.
6. Reconnect and require both serial and Customer ID to exactly match the persisted values.

### Phase 3 — Generate and Flash Public Keys

1. Use the verified Phase 2 identity to generate the CDI, P-256 key pair, and provisioning manifest.
2. Stage the generated public-key header while retaining the assigned serial and Customer ID.
3. Configure and clean-build the same hardware profile, probe the target, and flash after the safety confirmations.
4. Reconnect and require the complete live identity to match the generated manifest.

### Phase 4 — Final Verification

1. Perform a fresh, read-only Modbus identity read.
2. Require exact serial and Customer ID equality with the persisted first-provisioning session.
3. Require device ID, Customer ID, raw public key, and fingerprint to match the manifest.
4. Declare the card complete only after every check passes. Use the same identity for authorization, logs, and reports.

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
- Automated tests: 24 passed, including phase-separated default/public-key header staging, ID generation, serial formatting, persistence/recovery, duplicate device assignment rejection, hardware mismatch rejection, profile artifacts, and header compatibility.
- Stepper firmware clean build from synchronized `main`: passed.
- ASM firmware configure plus clean build from synchronized `main`: passed.
- No physical flash was executed during automated verification.
