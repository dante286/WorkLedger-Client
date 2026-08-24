# Changelog

<!-- Release Please inserts each new version directly below this line. -->

---

## Superseded pre-release tags

WorkLedger's first real release is **v0.1.0**, versioned in step with WorkLedger-API's own
first release. The `v1.0.0`-`v1.0.4` tags below predate that reset: they were CI and packaging
experiments cut while the release pipeline was being set up, before any of the sync work
existed, and their version numbers never described a shipped feature set. They are kept here
for provenance - the tags and their release assets still exist on GitHub - but nothing after
this point continues their numbering.

## [0.2.0](https://github.com/dante286/WorkLedger-Client/compare/v0.1.0...v0.2.0) (2026-08-24)


### Features

* **sync:** adopt the server's authoritative payload on a server-wins push (WL-101) ([#22](https://github.com/dante286/WorkLedger-Client/issues/22)) ([eabec70](https://github.com/dante286/WorkLedger-Client/commit/eabec709ef29f8fe80b45abc20e129888859567b))

## 0.1.0 (2026-08-21)


### Features

* **reports:** replace the Reports tab stub with weekly summaries and CSV export (WL-50) ([c6ccad2](https://github.com/dante286/WorkLedger-Client/commit/c6ccad27ce4c93de1bfe434ef3384ae68a8a66a7))
* sync + reports UI, and the v0.1.0 release (plan 7) ([598321c](https://github.com/dante286/WorkLedger-Client/commit/598321c9b1a540c7de73f05e4ca968550e2ccbd8))
* **sync:** add ApiSyncProvider against the WorkLedger-API sync contract (WL-96) ([76b95fe](https://github.com/dante286/WorkLedger-Client/commit/76b95fe279138ce01d0ec95703ce63604c1e4581))
* **sync:** add ApiSyncProvider against the WorkLedger-API sync contract (WL-96) ([401f87d](https://github.com/dante286/WorkLedger-Client/commit/401f87d89ec5a02e32c71857df399d76c9bc9a18))
* **sync:** add configurable retention window to FileSyncProvider (WL-61) ([c020666](https://github.com/dante286/WorkLedger-Client/commit/c020666f97b0b366b913a3e6b0954a8cef05972e))
* **sync:** add ISyncProvider abstraction and SyncOrchestrator with last-write-wins merge ([f4b6027](https://github.com/dante286/WorkLedger-Client/commit/f4b60278f18f4615178765b9befce8547fcaad07))
* **sync:** add ISyncProvider abstraction and SyncOrchestrator with last-write-wins merge ([215ce7b](https://github.com/dante286/WorkLedger-Client/commit/215ce7b7d1767968e051b9a9a95287f846171a9b))
* **sync:** add SyncMode setting and wire SyncOrchestrator into app startup and Settings UI ([20c350a](https://github.com/dante286/WorkLedger-Client/commit/20c350a635d742bfde6b1ff836a679b75230d675))
* **sync:** add Version and OriginDeviceId to core models with migration ([55adc7e](https://github.com/dante286/WorkLedger-Client/commit/55adc7ebcb0f943fbc03f282a22074ddcad10f12))
* **sync:** add Version and OriginDeviceId to core models with migration ([844dd12](https://github.com/dante286/WorkLedger-Client/commit/844dd12ce76338903b8f6c70f00dd2cb8a8e71fb))
* **sync:** enqueue SyncQueue entries on every entity mutation ([4d00424](https://github.com/dante286/WorkLedger-Client/commit/4d004249aa25aacd9c24492c966de65b0bf578fe))
* **sync:** enqueue SyncQueue entries on every entity mutation ([7b6d506](https://github.com/dante286/WorkLedger-Client/commit/7b6d506639579838b187797d016634e559c99136))
* **sync:** implement FileSyncProvider for folder-based sync ([3ab4a63](https://github.com/dante286/WorkLedger-Client/commit/3ab4a63d76ba07ce85efecbc14bc843e4731305a))
* **sync:** implement FileSyncProvider for folder-based sync ([5df5d6f](https://github.com/dante286/WorkLedger-Client/commit/5df5d6fc4e756a2d8a80814b907afe0fbf739730))
* **sync:** take a safety backup before the first remote-wins merge of a run (WL-51) ([4645d1f](https://github.com/dante286/WorkLedger-Client/commit/4645d1fe1eaf9e765b3cd9ae5d36e57efe8b7c6b))
* **sync:** vendor the WorkLedger.Contracts DTOs with drift guards (WL-96) ([67bebdc](https://github.com/dante286/WorkLedger-Client/commit/67bebdc99785703dd784cb2b5662054190df553c))


### Bug Fixes

* guard against null Avalonia.Application.Current in BooleanToArchiveColorConverter\n\nAlso override SQLite native library to a non-vulnerable version to remove NU1903 warning.\n\nCo-authored-by: Copilot &lt;223556219+Copilot@users.noreply.github.com&gt; ([3149b5a](https://github.com/dante286/WorkLedger-Client/commit/3149b5a2179513e0aae0d3f945803319399eb5d1))
* move show archive to right side ([f9172c6](https://github.com/dante286/WorkLedger-Client/commit/f9172c6fadb14db186173d4aee6fa333221c5b04))
* Only build on versioned tags ([630a770](https://github.com/dante286/WorkLedger-Client/commit/630a7709d90b24db513a53cdc70f7dc3dc731e33))
* releasing v1.0.2 ([b93e813](https://github.com/dante286/WorkLedger-Client/commit/b93e81353d2d466ddb43a6a8f589a66c8a9d3a0d))
* **settings:** migrate data directory changes in both directions (WL-74) ([7ae7565](https://github.com/dante286/WorkLedger-Client/commit/7ae7565580f92a16bf475b64733dbff8cef2ff83))
* **settings:** migrate data directory changes in both directions (WL-74) ([342c7b1](https://github.com/dante286/WorkLedger-Client/commit/342c7b1ffd50a14dcfc7636c04f97f803ed300b1))
* Split into build and package workflows. ([9b58d03](https://github.com/dante286/WorkLedger-Client/commit/9b58d0355ed16ff557644f0293df2d2a2d97215c))
* **sync:** bump the entity version when queuing a delete (WL-93) ([2e3e379](https://github.com/dante286/WorkLedger-Client/commit/2e3e37927b38af6b0a209259ba32a0c7407f5008))
* **sync:** carry parent SyncIds on TaskItem and TimeEntry (WL-91) ([e5cbc3e](https://github.com/dante286/WorkLedger-Client/commit/e5cbc3ec836423c7f8d96f9da4720bbc6d204033))
* **sync:** queue timer-created time entries for sync (WL-97) ([a8f94c6](https://github.com/dante286/WorkLedger-Client/commit/a8f94c69395c9c66f98b88a7d1dd0e5e922655c1))
* **sync:** queue timer-created time entries for sync (WL-97) ([ca8fa95](https://github.com/dante286/WorkLedger-Client/commit/ca8fa9583c18f418321d2a6fd9744dedd78923b8))
* **sync:** stop the merge path re-broadcasting remote changes (WL-99) ([f654745](https://github.com/dante286/WorkLedger-Client/commit/f654745036acb9507bf63ef2ceeda46144ea1215))
* **sync:** stop the merge path re-broadcasting remote changes, and re-vendor the contracts (WL-99, WL-100) ([9f49231](https://github.com/dante286/WorkLedger-Client/commit/9f49231af8a5d38fdc9a3b2d9ffd52ee893e4e0d))
* testing a release please trigger ([eb4d61d](https://github.com/dante286/WorkLedger-Client/commit/eb4d61d81cc5198f6061c981f5e8b4c5a6354ab3))
* testing github builds ([e4ea07e](https://github.com/dante286/WorkLedger-Client/commit/e4ea07e693d4c9b665fb5e464df9a0034038c5bb))
* testing github builds ([baabb97](https://github.com/dante286/WorkLedger-Client/commit/baabb97a33fbd6bd4430ab8076368166736cd677))
* ubuntu builds failing ([dbb1b51](https://github.com/dante286/WorkLedger-Client/commit/dbb1b5104297d7f734aec71f16ab7d2ccd0f37f8))


### Miscellaneous Chores

* **release:** commit the missing Release Please workflow and reset to the v0.1.0 line ([9398a2f](https://github.com/dante286/WorkLedger-Client/commit/9398a2f5e13b0ac33cdf31c9a34713b1f09fed98))

## [1.0.4](https://github.com/dante286/WorkLedger-Client/compare/v1.0.3...v1.0.4) (2026-06-23)


### Bug Fixes

* ubuntu builds failing ([dbb1b51](https://github.com/dante286/WorkLedger-Client/commit/dbb1b5104297d7f734aec71f16ab7d2ccd0f37f8))

## [1.0.3](https://github.com/dante286/WorkLedger-Client/compare/v1.0.2...v1.0.3) (2026-06-23)


### Bug Fixes

* releasing v1.0.2 ([b93e813](https://github.com/dante286/WorkLedger-Client/commit/b93e81353d2d466ddb43a6a8f589a66c8a9d3a0d))

## [1.0.2](https://github.com/dante286/WorkLedger-Client/compare/v1.0.1...v1.0.2) (2026-06-22)


### Bug Fixes

* testing github builds ([e4ea07e](https://github.com/dante286/WorkLedger-Client/commit/e4ea07e693d4c9b665fb5e464df9a0034038c5bb))

## [1.0.1](https://github.com/dante286/WorkLedger-Client/compare/v1.0.0...v1.0.1) (2026-06-22)


### Bug Fixes

* testing github builds ([baabb97](https://github.com/dante286/WorkLedger-Client/commit/baabb97a33fbd6bd4430ab8076368166736cd677))

## 1.0.0 (2026-06-22)


### Bug Fixes

* guard against null Avalonia.Application.Current in BooleanToArchiveColorConverter\n\nAlso override SQLite native library to a non-vulnerable version to remove NU1903 warning.\n\nCo-authored-by: Copilot &lt;223556219+Copilot@users.noreply.github.com&gt; ([3149b5a](https://github.com/dante286/WorkLedger-Client/commit/3149b5a2179513e0aae0d3f945803319399eb5d1))
* move show archive to right side ([f9172c6](https://github.com/dante286/WorkLedger-Client/commit/f9172c6fadb14db186173d4aee6fa333221c5b04))
* Only build on versioned tags ([630a770](https://github.com/dante286/WorkLedger-Client/commit/630a7709d90b24db513a53cdc70f7dc3dc731e33))
* Split into build and package workflows. ([9b58d03](https://github.com/dante286/WorkLedger-Client/commit/9b58d0355ed16ff557644f0293df2d2a2d97215c))
* testing a release please trigger ([eb4d61d](https://github.com/dante286/WorkLedger-Client/commit/eb4d61d81cc5198f6061c981f5e8b4c5a6354ab3))
