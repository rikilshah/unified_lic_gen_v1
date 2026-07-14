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

## Implemented Provisioning Flow

1. Start a first-provisioning session for a blank card.
2. Enter and validate the assigned serial for the selected hardware.
3. Generate one valid Customer ID and persist it together with the assigned serial, MCU UID, and hardware profile before modifying firmware.
4. Display the assigned serial and generated Customer ID for operator review.
5. Write the pair into the firmware headers:

   ```c
   #define APP_CUST_ID_TEXT "1234567890"
   #define APP_SERIAL_NUMBER_TEXT "S26050606"
   ```

6. Preserve the existing timestamped-header backup behavior.
7. Build and flash the selected hardware firmware using the existing guarded process.
8. Reconnect and compare both serial and Customer ID with the persisted values.
9. Mark provisioning successful only when both exact values and the manifest identity match.
10. Use that same identity pair for CDI JSON, key generation, manifest generation, authorization, logs, and reports.

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
- Automated tests: 23 passed, including ID generation, serial formatting, persistence/recovery, duplicate device assignment rejection, hardware mismatch rejection, profile artifacts, and header compatibility.
- Stepper firmware clean build from synchronized `main`: passed.
- ASM firmware configure plus clean build from synchronized `main`: passed.
- No physical flash was executed during automated verification.
