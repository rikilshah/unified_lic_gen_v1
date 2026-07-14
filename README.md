# Unified Test & Keygen Dashboard

Windows dashboard for CDI capture, P-256 key generation, manifest authorization, ASM and Stepper diagnostics, and guarded Stepper firmware provisioning.

## Documentation

- [Design system and UX rules](DESIGN.md)
- [Project context and unified architecture](UNIFIED_TEST_KEYGEN_DASHBOARD_CONTEXT.md)
- [Stepper firmware provisioning and flash runbook](FIRMWARE_PROVISIONING_AND_FLASH.md)
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
