# UnifiedLicGen Documentation

This directory is the structured entry point for project documentation. Use the shortest document that answers the task at hand; detailed operating procedures live in `guides`, while long-lived technical decisions live in `architecture` and `design`.

## Architecture

- [Architecture and project context](architecture/ARCHITECTURE.md) — system boundaries, shared contracts, register-map strategy, safety model, migration history, and acceptance criteria.

## Design

- [Design system](design/DESIGN.md) — Material Industrial Dark tokens, layout rules, interaction principles, and decision log.

## Guides

- [Customer ID provisioning SOP](guides/CUSTOMER_ID_PROVISIONING_SOP.md) — serial assignment, 10-digit Customer ID generation, CDI persistence, staging, flashing, and verification.
- [Firmware provisioning and flash](guides/FIRMWARE_PROVISIONING_AND_FLASH.md) — operator flow, firmware targets, ST-LINK gates, build commands, and readback requirements.
- [Publishing](guides/PUBLISHING.md) — standalone Windows x64 build, packaging, prerequisites, versioning, and release checks.

## Release Information

- [Project overview](../README.md) — product entry point, setup commands, and authoritative repositories.
- [Changelog](../CHANGELOG.md) — shipped capabilities and fixes by public version.

## Related Application

- [Technician application](https://github.com/rikilshah/asm_dashboard) — restricted operational application derived from the Admin dashboard. The [architecture document](architecture/ARCHITECTURE.md#11-admin-and-technician-product-relationship) defines ownership and synchronization rules.

## Documentation Rules

- Keep `README.md` and `CHANGELOG.md` at the repository root.
- Put architectural context in `docs/architecture/`.
- Put UI/UX rules and decisions in `docs/design/`.
- Put executable procedures in `docs/guides/`.
- Link every new document from this index.
- Treat GitHub repositories as authoritative; local paths are working checkouts only.
- Define shared device and workflow behavior in the Admin repository first, then port approved changes to the Technician repository from an explicit Admin commit or tag.
