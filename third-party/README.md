# Third-party payloads

Everything here belongs to somebody else and is used verbatim. Nothing in this folder is built from this
repository.

## RyzenSMU.bin — vendored

PawnIO module bytecode: the program that runs *inside* the PawnIO driver and gives AcerHelper's CPU-undervolt
feature access to the AMD SMU mailbox. Loaded as an embedded resource (see `AcerHelper.csproj`) and handed to the
driver as bytes; it is never written to disk.

| | |
|---|---|
| Upstream | <https://github.com/namazso/PawnIO.Modules> |
| Copyright | © namazso `<admin@namazso.eu>` |
| Licence | `LGPL-2.1-or-later` (SPDX), per the `SPDX-License-Identifier` header in `RyzenSMU.p` and the repo's `COPYING` |
| Source | `RyzenSMU.p` in the upstream repository, at the release this blob came from |
| SHA-256 | `54DA61C2653ED0AFABC20D1349636023CB90E7582C4EE4AB93FA77D673E33F26` (38 996 bytes) |

**Why it is committed rather than fetched in CI.** The upstream project states outright that module APIs carry no
stability guarantee across releases — *"the minor version will be bumped to clarify the lack of API stability
guarantee across releases. Since the modules are bundled with the software using them, this shouldn't cause
issues"*. AcerHelper's call shapes (`ioctl_read_smu_register`, 1 in / 1 out; `ioctl_write_smu_register`, 2 in /
0 out) were verified against **these exact bytes** on real hardware. Pinning by content is therefore stronger than
pinning a URL plus a hash, and it keeps the feature buildable and testable locally. Bundling is also what the
author's own integration guide prescribes: *"Select the module binary you wish to use, and include its contents in
your software."*

**Updating it** is a deliberate act, not a routine bump: replace the file, re-record the SHA-256 above, and
re-verify that the two ioctl call shapes still match — a changed argument or result count fails with
`STATUS_INVALID_PARAMETER` rather than misbehaving quietly, but a changed *meaning* would not.

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
