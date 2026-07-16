# Unified Test & Keygen Dashboard

Minimal Windows workflow for reading blank ASM or Stepper cards, assigning identity, generating the complete P-256 package, staging firmware, and performing one guarded final flash with readback verification.

Physically blank cards without Modbus firmware can be bootstrapped in Phase 1 using the guarded ST-LINK-only default flash. Connect through Modbus after that flash to continue provisioning.

Provisioning phases, ASM controls, Stepper controls, and Test Center are organized in one compact sidebar. Every panel shares the same global Modbus connection and detected identity.

The exact 10-digit Customer ID and manifest-verification state remain visible globally. Use the dedicated **Verify board** tab before operating either control panel.

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
- Stepper firmware source of truth: [rikilshah/stepper_control_card_v2](https://github.com/rikilshah/stepper_control_card_v2)
- ASM firmware source of truth: [rikilshah/VCB240002](https://github.com/rikilshah/VCB240002)

## Local Verification

```powershell
dotnet build UnifiedLicGen.csproj
dotnet test UnifiedLicGen.Tests\UnifiedLicGen.Tests.csproj -p:Platform=x64
```

Firmware programming is interactive and requires physical-target acknowledgement plus exact card-serial confirmation.
