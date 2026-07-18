# Unified Test & Keygen Dashboard

Minimal Windows workflow for reading blank ASM or Stepper cards, assigning identity, generating the complete P-256 package, staging firmware, and performing one guarded final flash with readback verification.

Physically blank cards without Modbus firmware can be bootstrapped in Phase 1 using the guarded ST-LINK-only default flash. Connect through Modbus after that flash to continue provisioning.

Already-finalized cards use **Reflash finalized firmware** in the Target panel. This maintenance path clean-builds and flashes the selected firmware source while preserving the existing serial, Customer ID, public key, CDI, key package, and manifest. It requires the live identity, source headers, and existing trusted manifest to agree before flashing, then verifies the same identity after restart.

Provisioning phases, ASM controls, Stepper controls, and Test Center are organized in one compact sidebar. Every panel shares the same global Modbus connection and detected identity.

The shared serial session can probe ASM and Stepper cards independently on one COM port (defaults: ASM slave `1`, Stepper slave `2`). Each detected card keeps its own manifest-authorization gate while the Admin provisioning wizard continues to operate on the active firmware target.

Each detected card's serial number and exact 10-digit Customer ID remain grouped and visible globally; detected serials also appear in the native window title. Use the dedicated **Verify board** tab before operating either control panel.

Current public version: **2.0.0**. This major release unifies provisioning, authorization, ASM control and Stepper control around one shared Modbus session. The version is shown in the window title, application header, status bar, and executable metadata.

## Standalone publishing

```powershell
dotnet publish UnifiedLicGen.csproj -p:PublishProfile=win-x64
```

Distribute the complete `artifacts\publish\win-x64` folder. See the [publishing guide](docs/guides/PUBLISHING.md) for ZIP creation, prerequisites, versioning, and release verification.

## Documentation

Start with the [documentation index](docs/README.md). It organizes the project material by purpose:

- [Architecture and project context](docs/architecture/ARCHITECTURE.md)
- [Design system and UX rules](docs/design/DESIGN.md)
- [Operator and release guides](docs/guides/)
- [Release history](CHANGELOG.md)

## Source Repositories

- Dashboard source of truth: [rikilshah/unified_lic_gen_v1](https://github.com/rikilshah/unified_lic_gen_v1)
- Technician application: [rikilshah/asm_dashboard](https://github.com/rikilshah/asm_dashboard)
- Stepper firmware source of truth: [rikilshah/stepper_control_card_v2](https://github.com/rikilshah/stepper_control_card_v2)
- ASM firmware source of truth: [rikilshah/VCB240002](https://github.com/rikilshah/VCB240002)

The Admin dashboard remains the upstream source of truth for shared device contracts, identity and authorization behavior, Modbus infrastructure, register maps, and control/test behavior. Technician releases are derived deliberately from a tagged or committed Admin baseline. Proven Technician improvements may be reconciled back here, after which the Admin implementation becomes the canonical shared behavior again.

## Local Verification

```powershell
dotnet build UnifiedLicGen.csproj
dotnet test UnifiedLicGen.Tests\UnifiedLicGen.Tests.csproj -p:Platform=x64
```

Firmware programming is interactive and requires physical-target acknowledgement plus exact card-serial confirmation.
