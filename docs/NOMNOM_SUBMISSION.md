# NOMNOM submission: Loyal Wingman 0.0.2

This is the 0.0.2 compatibility-prerelease update for `modManifests/LoyalWingman.json`. [NOMNOM PR #220](https://github.com/KopterBuzz/NOMNOM/pull/220) remains a draft and awaits this update and upstream review.

## Checklist

- [x] Run `scripts/PackageRelease.ps1` and record the generated `LoyalWingman.dll` SHA-256.
- [ ] Create the parseable tag `v0.0.2` and a public GitHub Release.
- [ ] Upload exactly `LoyalWingman.dll` as the first GitHub Release asset (no ZIP or PDB).
- [ ] Record the GitHub release-asset digest.
- [ ] Update [PR #220](https://github.com/KopterBuzz/NOMNOM/pull/220); it remains a draft.

`NOComponentsWIP` is neither a current runtime dependency nor registered in NOMNOM, so it must not be declared in `dependencies`.

## JSON manifest draft

```json
{
  "id": "LoyalWingman",
  "displayName": "Loyal Wingman",
  "description": "Nuclear Option 0.34 compatibility prerelease of the Loyal Wingman plugin.",
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
      "version": "0.0.2",
      "category": "preRelease",
      "type": "plugin",
      "gameVersion": "0.34",
      "downloadUrl": "https://github.com/NOLoyalWingman/LoyalWingman/releases/download/v0.0.2/LoyalWingman.dll",
      "hash": "sha256:91c9eefa32fe784a9adc852c27ba8c525f3dcd9d611d669aaa0070d88c4c5c6f",
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

The local package is complete. The `v0.0.2` tag, GitHub Release, asset upload, and PR #220 update are pending; PR #220 remains a draft.
