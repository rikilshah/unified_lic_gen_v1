# Unified Test & Keygen Dashboard

Minimal Windows workflow for reading blank ASM or Stepper cards, assigning identity, generating the complete P-256 package, staging firmware, and performing one guarded final flash with readback verification.

## Documentation

- [Design system and UX rules](DESIGN.md)
- [Project context and unified architecture](UNIFIED_TEST_KEYGEN_DASHBOARD_CONTEXT.md)
- [Unified firmware provisioning and flash runbook](FIRMWARE_PROVISIONING_AND_FLASH.md)
- [Customer ID provisioning SOP](CUSTOMER_ID_PROVISIONING_SOP.md)

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
