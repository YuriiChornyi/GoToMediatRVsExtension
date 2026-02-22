# GitHub Workflows for MediatR Navigation Extension

## Workflows

### `pr-validation.yml` — PR Gate
**Triggers:** Any PR opened or updated against `main`

- Restores NuGet packages and builds in Debug mode
- Reads the version from `source.extension.vsixmanifest` and compares it against the latest `v*` git tag
- **Fails the PR if the version was not bumped** — you must update the version in the manifest before merging

### `build-and-publish.yml` — Release on Push to Main
**Triggers:** Push to `main`

- Builds the VSIX in Release mode
- Reads the version from `source.extension.vsixmanifest`
- Collects all commit messages since the last release tag as release notes
- Creates a GitHub Release tagged `v{version}` with the VSIX attached

### `marketplace-publish.yml` — Publish to Visual Studio Marketplace
**Triggers:** GitHub release published, or manual dispatch

- Builds the VSIX and publishes it to the Visual Studio Marketplace using `VsixPublisher.exe`
- Requires the `MARKETPLACE_PAT` secret (Azure DevOps PAT with Marketplace publish scope)
- Runs in the `production` environment (add required reviewers for an approval gate)

---

## Development Flow

```
Feature branch
    → open PR → pr-validation.yml runs (build + version check)
    → merge to main → build-and-publish.yml runs (GitHub Release created automatically)
    → [optional] publish the release → marketplace-publish.yml runs
```

## Required Setup

### Secret
| Name | Description |
|------|-------------|
| `MARKETPLACE_PAT` | Azure DevOps PAT with `Marketplace (Publish)` scope — only needed for marketplace publishing |

### Environment
Create a `production` environment under `Settings → Environments` for the marketplace publish job (optional approval gate).

### Version Bumping
Update the `Version` attribute in `source.extension.vsixmanifest` manually before opening a PR:

```xml
<Identity ... Version="6.5" ... />
```

The PR validation workflow will fail if the version matches the latest release tag.
