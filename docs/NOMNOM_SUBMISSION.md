# NOMNOM submission: Loyal Wingman 0.0.2

This is the 0.0.2 compatibility-prerelease update for `modManifests/LoyalWingman.json`. [NOMNOM PR #220](https://github.com/KopterBuzz/NOMNOM/pull/220) has been updated and remains a draft pending Nuclear Option 0.34 runtime acceptance.

## Checklist

- [x] Run `scripts/PackageRelease.ps1` and record the generated `LoyalWingman.dll` SHA-256.
- [x] Create the parseable tag `v0.0.2` and a public GitHub Release.
- [x] Upload exactly `LoyalWingman.dll` as the first GitHub Release asset (no ZIP or PDB).
- [x] Record the GitHub release-asset digest.
- [x] Update [PR #220](https://github.com/KopterBuzz/NOMNOM/pull/220); it remains a draft pending runtime acceptance.

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
      "hash": "sha256:d4ff9f70ee2c73433f094a0a8733fc48865dd5b94ba82d00bb795b35d3f56d54",
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

The local package, `v0.0.2` GitHub prerelease, release digest, and PR #220 update are complete. PR #220 remains a draft pending Nuclear Option 0.34 runtime acceptance.
