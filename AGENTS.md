# Repository Instructions

## Mandatory application versioning

Every committed change that affects application code, UI behavior, hardware communication, provisioning, dependencies, packaging, or operator workflow must include a version update. Do not defer the version bump to a later commit.

1. Select the Semantic Versioning increment:
   - PATCH for bug fixes, UI/UX corrections, internal refactoring, dependency updates, and build changes;
   - MINOR for backward-compatible features or workflows;
   - MAJOR for breaking workflow, file-format, manifest, protocol, or compatibility changes.
2. Update `Version`, `AssemblyVersion`, and `FileVersion` together in `UnifiedLicGen.csproj`. Use `X.Y.Z` for `Version` and `X.Y.Z.0` for the assembly/file versions.
3. Add a dated entry for the same version to `CHANGELOG.md`, including every user-visible change.
4. Run the full test suite and build the application before committing.
5. Verify the generated `UnifiedLicGen.dll` product and file versions match the project version.
6. Commit the implementation, tests, changelog, and version metadata together.

When an Admin change is mirrored into the Technician app, apply and verify an independent version increment in both repositories. Documentation-only or test-only changes that do not alter the shipped application may retain the current application version.
