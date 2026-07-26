# NOMNOM submission draft: Loyal Wingman 0.0.1

This is a first-release checklist and manifest draft. The intended manifest location is `modManifests/LoyalWingman.json` and its ID/file name is `LoyalWingman` / `modManifests/LoyalWingman.json`. It becomes submit-ready after the public prerelease exists and the all-zero SHA-256 placeholder is replaced with the release asset digest.

## Checklist

- [ ] Run `scripts/PackageRelease.ps1` and record the generated `LoyalWingman.dll` SHA-256.
- [ ] Create the parseable tag `v0.0.1` and a GitHub Release marked **prerelease/Preview**.
- [ ] Upload exactly `LoyalWingman.dll` as the first GitHub Release asset (no ZIP or PDB).
- [ ] Replace the all-zero release-asset digest after upload.
- [ ] Submit the completed manifest after the public prerelease exists.

`NOComponentsWIP` is neither a current runtime dependency nor registered in NOMNOM, so it must not be declared in `dependencies`.

## JSON manifest draft

```json
{
  "id": "LoyalWingman",
  "displayName": "Loyal Wingman",
  "description": "Preview release of the Loyal Wingman plugin.",
  "authors": [
    "Loyal Wingman contributors"
  ],
  "urls": [
    {
      "name": "info",
      "url": "https://github.com/NOLoyalWingman/LoyalWingman"
    }
  ],
  "githubOwner": "NOLoyalWingman",
  "githubRepoName": "LoyalWingman",
  "autoUpdateArtifacts": "True",
  "artifacts": [
    {
      "fileName": "LoyalWingman.dll",
      "version": "0.0.1",
      "category": "preRelease",
      "type": "plugin",
      "gameVersion": "0.33",
      "downloadUrl": "https://github.com/NOLoyalWingman/LoyalWingman/releases/download/v0.0.1/LoyalWingman.dll",
      "hash": "sha256:0000000000000000000000000000000000000000000000000000000000000000",
      "dependencies": [
        {
          "id": "com.nikkorap.blueprinter",
          "version": "1.8.21"
        },
        {
          "id": "blueprinter.kestrel",
          "version": "2.2.0"
        }
      ]
    }
  ]
}
```

The first GitHub Release must remain a prerelease/Preview, while its tag stays parseable as `v0.0.1`. Replace the all-zero `sha256:<64hex>` placeholder with the final GitHub Release asset digest before submission.
