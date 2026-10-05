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

**Current AI fix baseline reviewed:** `358509b99c2e5c1d4343beb9732c4d00292d2ec6`; implementation may follow. All runtime checks are pending. For every run, record the exact candidate commit and deployed `LoyalWingman.dll` SHA-256 (not the historical manifest hash), and retain both `BepInEx\LogOutput.log` and `%USERPROFILE%\AppData\LocalLow\Shockfront\NuclearOption\Player.log`. This is SP-first only; multiplayer remains unaccepted.

| Pending runtime check | User action | Expected visible result to record |
| --- | --- | --- |
| [ ] First glide bomb and repeat interval | Release a wingman with a glide bomb, assign an eligible target, and watch the first attack; leave the target available for its next attack opportunity. | The first glide bomb is visibly released, and a later release occurs only after the normal repeat interval rather than immediately. |
| [ ] STRIKE after defensive interruption | Give STRIKE multiple ground targets, let the wingman interrupt to defend itself, then keep the remaining targets valid. | The wingman resumes STRIKE and visibly works the remaining targets instead of ending the mission after the interruption. |
| [ ] A2A exhaustion, reload/salvo wait, and CAP | Assign A2A and observe an ordinary salvo/reload wait before weapons are exhausted; then exhaust its A2A weapons. Separately assign/keep CAP at a point, including after no A2A ammunition remains. | With the target still valid, A2A remains assigned through salvo/reload waits and ends when compatible ammunition is exhausted. CAP remains assigned and may patrol even without A2A ammunition. |
| [ ] FOLLOW fallback and leader recovery | Command FOLLOW, make the leader temporarily unavailable, then restore the leader. | The wingman falls back safely while the leader is unavailable and visibly returns to following after recovery. |
| [ ] RTB entry, cancellation, and player takeover | Send a selected wingman RTB; use TAKE CONTROL while it is still RTB. Separately cancel RTB while cancellation is available. | RTB visibly starts; TAKE CONTROL during RTB gives the player the selected aircraft; cancellation returns the aircraft to controllable activity. |
| [ ] Recovery-pending takeover, cancel/detach, and player ownership | Start recovery, use TAKE CONTROL while recovery is pending, then cancel or detach recovery. | The aircraft is not destroyed; player ownership remains with the player after the cancel/detach. |
| [ ] SP scene-exit cleanup | In single-player, leave/end a scene or session with active wingman records and inspect the retained logs. | Cleanup completes without stale active wingman-session records or cleanup errors; normal despawn is permissible. |
| [ ] Authority-loss diagnostic — outside SP acceptance | In a diagnostic authority-loss/session-loss scenario, inspect both retained logs and session state. | No mod AI switch takes over after authority loss, and no lingering reservations remain. Dedicated multiplayer is still unaccepted; this is diagnostic evidence, not multiplayer acceptance. |

It is appropriate to ask for reopening or send a ready-for-review message only after every applicable pending runtime check above is accepted, a **new version** is published, and its manifest download URL and SHA-256 match the new published DLL. Explicit user approval is also required. Do not infer a runtime pass, release, version selection, publication, deployment authorization, or PR reopening from this checklist.

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
