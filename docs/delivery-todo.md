# Vendor-plugin delivery TODO (D1–D4)

Authoritative design: `docs/vendor-plugins.md` §5 (packaging, distribution, runtime update, signing, CI).
This file tracks the delivery channel: how a plugin reaches the user. One task = one subagent.
A task is `done` only after **both host TFMs build 0/0, the signer/tool builds, and the full suite is green**.

> Tracker lives in git because the OpenCode `todowrite` tool is not registered in this harness.

## Why delivery

Phase 1 moved Acer OUT of the host (`DeviceFactory` loads a cached plugin, `docs/phase-1-todo.md`). The
plugin is not bundled in the MSI/AppImage: it is published as a **GitHub Release asset** and downloaded **at
runtime**, verified, cached per-user, and enabled on the next restart. This is that channel.

## Tasks

| # | Task | Status | Depends | Notes |
|---|---|---|---|---|
| D1 | Local plugin cache (`PluginCache` + `installed.json`, atomic install, local hash verify) | **done** (`0c74ad4`) | — | layout `<root>/<id>/v<major>/`; 1947 tests |
| D2a | Plugin update service (`PluginManifestFetcher` + `PluginUpdateService`: fetch/plan/download/verify/install) | **done** (`e4a5b9b`) | D1 | injectable seams; 1968 tests |
| D2b | Wire into the existing `UpdateSchedule` tick + restart notification | **done** (`a4fce24`) | D2a | one GitHub call; same `_updating` guard; 1985 tests |
| D4 | Embed the production P-256 signing public key | **done** (`c05f3f8`) | — | `keyId 2026-09-release`; private half NOT in repo |
| D3 | CI builds/signs/publishes the plugin + `vendor-plugins.json` in `build.yml` | **done** (`48ba340`) | D1,D2 | aggregate job; name-normalisation; 1996 tests |

## How it works now

1. `build.yml` (a `v*` tag) publishes `acer-nitro` Native AOT per OS, **renames** it to the §5.2 canonical
   asset name (`AcerHelper.Vendor.acer-nitro-<os>-x64.<ext>`), signs it with `PLUGIN_SIGNING_KEY`, and the
   `plugins-manifest` aggregate job writes `vendor-plugins.json` and attaches everything to the release.
2. `AppController`'s one `UpdateSchedule` tick fetches the release (one GitHub call), and
   `PluginUpdateService` plans → downloads → verifies (signature → hash → bytes) → confirms with `ah_matches`
   → installs into `%AppData%\AcerHelper\plugins\<id>\v<major>\`.
3. On `Installed` it raises the `plugin.installed_restart` notification; the click restarts the app, and the
   next `DeviceFactory.Create` loads the plugin synchronously.

## OWNER ACTION REQUIRED (before delivery can actually ship)

**Set the repository secret `PLUGIN_SIGNING_KEY`** = the private half of the production key, PKCS#8 DER,
base64. It was generated locally and written to
`%USERPROFILE%\acer-helper-plugin-signing-key.pkcs8.b64` (outside the repo; never commit it). The embedded
public key is `PluginPublicKeys.ReleaseKeyId = "2026-09-release"`, and the pair was verified
(ExportSubjectPublicKeyInfo(private) == embedded SPKI; sign/verify round-trip OK).

Add it under GitHub → Settings → Secrets and variables → Actions → New repository secret, name
`PLUGIN_SIGNING_KEY`. A `v*` tag release FAILS LOUDLY if it is absent (an unsigned plugin is worse than a
failed build).

## Provable only by a tagged CI run

The real Native AOT publishes on both runners, the real secret producing a signature the shipped host's
embedded key accepts, the artifact upload/download/merge across jobs, the final release attachment, and the
end-user download+install+restart. Everything above is the contract that makes that run meaningful; it is
pinned in-suite but not executed locally (no MSVC/clang, no Actions locally).

## Still open (later)

- **Deprecated-major CI leg** (§5.5): absent on purpose — only API major 1 exists; add it with the first real
  major bump.
- **Phase 2** (Acer Linux half: hidraw, mainline acer-wmi, evdev hotkeys) and **Phase 3** (Dell/ASUS).
- **A tagged release** to exercise D3 end to end (needs the secret above).
