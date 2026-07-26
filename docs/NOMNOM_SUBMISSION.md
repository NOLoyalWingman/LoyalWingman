# NOMNOM submission: Loyal Wingman 0.0.1

This is the first public-release checklist and manifest for `modManifests/LoyalWingman.json`. The manifest was submitted in [NOMNOM PR #220](https://github.com/KopterBuzz/NOMNOM/pull/220) and is awaiting upstream review.

## Checklist

- [x] Run `scripts/PackageRelease.ps1` and record the generated `LoyalWingman.dll` SHA-256.
- [x] Create the parseable tag `v0.0.1` and a public GitHub Release.
- [x] Upload exactly `LoyalWingman.dll` as the first GitHub Release asset (no ZIP or PDB).
- [x] Record the GitHub release-asset digest.
- [x] Submit the completed manifest through [PR #220](https://github.com/KopterBuzz/NOMNOM/pull/220).

`NOComponentsWIP` is neither a current runtime dependency nor registered in NOMNOM, so it must not be declared in `dependencies`.

## JSON manifest draft

```json
{
  "id": "LoyalWingman",
  "displayName": "Loyal Wingman",
  "description": "Initial stable release of the Loyal Wingman plugin.",
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
      "category": "release",
      "type": "plugin",
      "gameVersion": "0.33",
      "downloadUrl": "https://github.com/NOLoyalWingman/LoyalWingman/releases/download/v0.0.1/LoyalWingman.dll",
      "hash": "sha256:30046e29496df9d37b6d396c908bdd862c107dbab34c4aeb5f6c6c619af47363",
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

The GitHub Release is public and its tag is parseable as `v0.0.1`. Registry submission is tracked in [PR #220](https://github.com/KopterBuzz/NOMNOM/pull/220).
