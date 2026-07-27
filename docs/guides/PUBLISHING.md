# Publishing UnifiedLicGen

## Standalone Windows x64 build

From the repository root:

```powershell
dotnet publish UnifiedLicGen.csproj -p:PublishProfile=win-x64
```

The distributable folder is:

```text
artifacts\publish\win-x64\
```

Run `UnifiedLicGen.exe` from that folder. Distribute the entire folder; WinUI runtime assets and native DLLs must remain beside the executable.

To create a release ZIP:

```powershell
Compress-Archive -Path .\artifacts\publish\win-x64\* -DestinationPath .\artifacts\UnifiedLicGen-vX.Y.Z-win-x64.zip -Force
```

## What self-contained means

The package includes the required .NET and Windows App SDK runtimes. The destination PC does not need a separate .NET installation.

Firmware operations still require:

- Windows x64 on Windows 10 version 1809 or later;
- STM32CubeCLT at the paths configured in `FirmwareProvisioningService`;
- synchronized local Stepper and ASM firmware repositories;
- ST-LINK drivers and physical hardware access;
- access to the configured `PCB_LIC_DB` target folder;
- an available Modbus COM port.

## Mandatory version updates

Every application update must carry its version in the same commit as the implementation. Do not commit application code, UI, hardware-control, provisioning, dependency, or packaging changes without completing all of these steps:

1. Choose a Semantic Versioning increment: PATCH for fixes and maintenance, MINOR for backward-compatible features, or MAJOR for breaking changes.
2. Update `Version`, `AssemblyVersion`, and `FileVersion` in `UnifiedLicGen.csproj`.
3. Add a dated entry for that exact version to `CHANGELOG.md`.
4. Run the full test suite and publish the standalone package.
5. Verify the generated binary metadata:

```powershell
(Get-Item .\artifacts\publish\win-x64\UnifiedLicGen.dll).VersionInfo |
    Select-Object ProductVersion, FileVersion
```

The native title bar, application header, status bar, ZIP name, changelog, and executable metadata must all identify the same release. When the same change is mirrored into the Technician app, bump and verify that repository independently. Documentation-only or test-only commits that do not change the shipped application may retain the existing version.

## Release verification

Before distribution:

```powershell
dotnet test .\UnifiedLicGen.Tests\UnifiedLicGen.Tests.csproj -p:Platform=x64
dotnet publish UnifiedLicGen.csproj -p:PublishProfile=win-x64
.\artifacts\publish\win-x64\UnifiedLicGen.exe
```

Confirm COM discovery, board verification, and the correct hardware tool paths on the target workstation before performing a production flash.
