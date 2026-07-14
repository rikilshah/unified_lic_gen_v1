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
Compress-Archive -Path .\artifacts\publish\win-x64\* -DestinationPath .\artifacts\UnifiedLicGen-v1.0.0-win-x64.zip -Force
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

## Version updates

Update `Version`, `AssemblyVersion`, and `FileVersion` in `UnifiedLicGen.csproj`, then update the ZIP filename above. The assembly version automatically appears in the native window title, application header, status bar, and executable metadata.

## Release verification

Before distribution:

```powershell
dotnet test .\UnifiedLicGen.Tests\UnifiedLicGen.Tests.csproj -p:Platform=x64
dotnet publish UnifiedLicGen.csproj -p:PublishProfile=win-x64
.\artifacts\publish\win-x64\UnifiedLicGen.exe
```

Confirm COM discovery, board verification, and the correct hardware tool paths on the target workstation before performing a production flash.
