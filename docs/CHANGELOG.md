# Changelog

Notable changes since **v1.0.0**, reconstructed from Git history and published
release notes. Dates below are GitHub publication dates.

## Unreleased

No unreleased functional changes as of 2026-10-24. The latest implementation
commit, [`17e19cb`](https://github.com/maschine34675/Killcam/commit/17e19cb),
is tagged and published as v1.0.1.

## [1.0.1] - 2026-10-21

### Configuration

- Made the **debug replay shortcut unbound by default** and visible only under
  **Advanced settings** in F12. Ordinary death replays still work automatically;
  Space remains the default skip key.
- Replaced the stored `Debug / ReplayKey` setting with `Debug / DebugReplayHotkey`.
  Previous F9 **and custom old bindings** are intentionally ignored on upgrade.
  A shortcut assigned to the new setting is retained on later launches. The old
  config line may remain, but cannot trigger playback.
- Added readable F12 labels, clearer descriptions and consistent ordering.
  Numeric settings display their actual units instead of percentage formatting.
- Preserved all other stored keys, preferences, defaults and ranges. Replay
  behavior and the ArenaMode/Redeploy integrations are otherwise unchanged.

### Verification

- Added 114 configuration checks using the installed BepInEx and
  ConfigurationManager libraries, covering old-key reset, preserved preferences,
  new bindings and menu metadata. Extended release checks to verify synchronized
  plugin and assembly versions.
- Migration, respawn-handoff and visual-model regression checks passed for the
  release. This update added **no new in-game acceptance test**.

See the [1.0.1 release notes](https://github.com/maschine34675/Killcam/releases/tag/v1.0.1)
for installation and verification details.

## [1.0.0] - 2026-10-19

Baseline for this changelog: the currently published `v1.0.0` tag at
[`4e29ff8`](https://github.com/maschine34675/Killcam/commit/4e29ff8).
Its archive was refreshed on 2026-10-19 without changing the version; earlier
same-version archives are not reconstructed here.

Recorded death replays, slow motion/live view, visual-only character models,
the KillcamReplay rename/config migration and ArenaMode/Redeploy integration
already belong to this baseline. See its
[README](https://github.com/maschine34675/Killcam/blob/v1.0.0/README.md)
for the features and limitations at release.

[1.0.1]: https://github.com/maschine34675/Killcam/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/maschine34675/Killcam/releases/tag/v1.0.0
