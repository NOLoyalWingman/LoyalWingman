# NOMNOM submission: Loyal Wingman 0.0.2 — historical prerelease

This document preserves the historical `v0.0.2` compatibility-prerelease record for `modManifests/LoyalWingman.json`. It is not a description of a recent AI-branch artifact or a current release candidate. The existing `v0.0.2` prerelease manifest below is intentionally unchanged.

## PR #220 current status

[NOMNOM PR #220](https://github.com/KopterBuzz/NOMNOM/pull/220) is **CLOSED** and is still marked as a draft (`isDraft: true`). It was closed at `2026-10-04T10:48:26Z` and was not merged (`mergedAt: null`). A maintainer previously commented on `2026-08-05`: “lmk when you are ready to merge this”.

No reopening, ready-for-review message, publication, deployment, or version selection is authorized by this status note.

## Historical v0.0.2 release record

The following entries record the historical `v0.0.2` prerelease work; they are not evidence that a newly tested candidate is ready:

- [x] `scripts/PackageRelease.ps1` was run and a `LoyalWingman.dll` SHA-256 was recorded.
- [x] The parseable `v0.0.2` tag and public GitHub prerelease were created.
- [x] `LoyalWingman.dll` was uploaded as the first GitHub Release asset (no ZIP or PDB).
- [x] A GitHub release-asset digest was recorded.
- [x] PR #220 was historically updated; its current state is closed, as recorded above.

`NOComponentsWIP` is neither a current runtime dependency nor registered in NOMNOM, so it must not be declared in `dependencies`.

## Readiness before asking to reopen or mark ready

It is appropriate to ask for reopening or send a ready-for-review message only after all of the following are satisfied:

- [ ] Deploy the exact new candidate that was tested and complete runtime acceptance for launch and control: Follow, A2A, Strike, RTB cancellation, takeover/recovery, lost leader/player handling, and scene cleanup.
- [ ] Publish a new version and verify that its `LoyalWingman.dll` digest and download match the manifest validators.
- [ ] Obtain explicit user approval before reopening PR #220 or sending a ready-for-review/merge message.

CLX completion and new native recruitment are not gates for the original feature release. This checklist makes no claim that the required runtime tests or release validation have been completed, and it does not select a new version.

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
