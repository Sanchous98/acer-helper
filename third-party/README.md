# Third-party payloads

Everything here belongs to somebody else and is used verbatim. Nothing in this folder is built from this
repository.

## RyzenSMU.bin — fetched at build time

PawnIO module bytecode: the program that runs *inside* the PawnIO driver and gives AcerHelper's CPU-undervolt
feature access to the AMD SMU mailbox. Loaded as an embedded resource (see `AcerHelper.csproj`) and handed to the
driver as bytes; it is never written to disk.

| | |
|---|---|
| Upstream | <https://github.com/namazso/PawnIO.Modules> |
| Copyright | © namazso `<admin@namazso.eu>` |
| Licence | `LGPL-2.1-or-later` (SPDX), per the `SPDX-License-Identifier` header in `RyzenSMU.p` and the repo's `COPYING` |
| Source | the `RyzenSMU.bin` inside the release zip, e.g. `release_0_2_11.zip` for tag `0.2.11` |
| Last resolved here | `0.2.11`, SHA-256 `301D9CA397108E09F31BFBD5AC4C9BB4F352A5DE68532C32DB3BA7DDCDE93450` (50 764 bytes) — informational only: the build resolves the latest, it is not pinned by this table |

**Fetched, not committed.** `AcerHelper.csproj`'s `FetchRyzenSMU` target runs `build/fetch-ryzensmu.ps1` on
Windows builds. The script asks GitHub for the **latest** release of `namazso/PawnIO.Modules`, picks the release
zip, verifies the download against the **SHA-256 digest GitHub records for that asset** (the `digest` field on the
release asset), extracts `RyzenSMU.bin`, checks that the module still names `ioctl_read_smu_register` and
`ioctl_write_smu_register` (the two entries the code drives — a release that drops one is refused), records the
resolved tag and module SHA-256 in `obj/ryzensmu/<tag>/resolved.json` and in the build log, and copies it to
`third-party/RyzenSMU.bin` where the csproj embeds it. Caching lives under `obj/ryzensmu/<tag>/`: the release API
is queried on each build so "latest" really is latest, but the zip is downloaded only when the resolved tag has no
verified cached copy, so repeat builds and warm CI runs do not re-download. `third-party/RyzenSMU.bin` is in
`.gitignore`.

**Why latest is allowed here, and how integrity still holds.** The earlier arrangement committed one blob because
the upstream project states outright that module APIs carry no stability guarantee — *"the minor version will be
bumped to clarify the lack of API stability guarantee across releases. Since the modules are bundled with the
software using them, this shouldn't cause issues"* — and because AcerHelper's call shapes
(`ioctl_read_smu_register`, 1 in / 1 out; `ioctl_write_smu_register`, 2 in / 0 out) were verified against one
exact set of bytes. Fetching latest trades that exact-bytes pin for freshness; the reconciliation is that

* the release asset is verified against the digest GitHub publishes for it, so a corrupted or substituted
  download never reaches the embed;
* the two exported function names are checked as bytes, so a release that drops or renames either call shape is
  refused with an integrity error rather than embedded;
* the resolved tag and module SHA-256 are recorded in the build log and in `obj/ryzensmu/<tag>/resolved.json`;
* a build can pin hard with `-p:RyzenSmuPinnedVersion=0.2.11` and/or `-p:RyzenSmuPinnedSha256=<hex>`, and
  `-p:RequireRyzenSmu=true` turns an unavailable upstream into a build error instead of a hidden feature.

The call shapes are still checked by a human on the way past, not only by the build: a changed argument or result
*count* fails at runtime with `STATUS_INVALID_PARAMETER`, but a changed *meaning* would not, so a release bump is
still worth a look at the `RYZENSMU: staged <tag> sha256 <hex>` line.

**Offline.** If the API or the download is unreachable, the script falls back to the last verified cached module;
with no cache it exits `1`, the target emits a warning, staging is skipped and the undervolt section hides
itself — the same posture a checkout without the blob always had, and the whole app build does not fail.
`-p:FetchRyzenSmu=false` skips the fetch entirely. The Linux TFM and the Docker image never run it: the module is
Windows-only.

**The licence text travels with the build, not with the repo.** The LGPL requires it to accompany the work, so
`.github/workflows/build.yml` fetches the upstream `COPYING` verbatim into `RyzenSMU.COPYING` and the MSI ships it
next to the app. It is fetched rather than committed for one reason: a licence must never be a hand-copied
approximation, and the build guards that with a grep before packaging.

## PawnIO_setup.exe — not committed, fetched by CI

The PawnIO driver installer, downloaded pinned and checksum-verified by `.github/workflows/build.yml` and copied
into the install folder so the app can offer a one-click install.

| | |
|---|---|
| Upstream | <https://pawnio.eu/> — releases at <https://github.com/namazso/PawnIO.Setup> |
| Copyright | © namazso `<admin@namazso.eu>` |
| Licence | Proprietary freeware. The binary itself grants: *"This installer can be redistributed unmodified."* The author's module documentation says the same — *"Official and unrestricted binary editions: Proprietary, however redistribution of installer is allowed."* |
| Version | 2.2.0 |
| SHA-256 | `1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032` |

**The permission covers the installer, unmodified — and nothing else.** Unpacking `PawnIO.sys` or `PawnIOLib.dll`
out of it and shipping them as our own files is *not* granted, so AcerHelper only ever *invokes* the installer.
CI additionally verifies its Authenticode signature before it is packaged.

Note the author's stated *preference* differs from what he permits: *"It is recommended that users are simply
redirected to pawnio.eu for obtaining a copy."* AcerHelper bundles it so the offer works offline and cannot be
pointed at a substituted download, but the prompt names the driver, its purpose and its origin, asks once, and
takes no as an answer — and the app never upgrades or removes PawnIO, because other tuning tools share it.
