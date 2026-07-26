# AcerHelperLampArray — build, sign, install

`AcerHelperLampArray.sys` is the virtual HID **LampArray** device that makes this laptop's keyboard visible to
**Windows Dynamic Lighting**. It is a KMDF *HID source driver* sitting on top of the in-box Virtual HID
Framework (`vhf.sys`), driven over three IOCTLs by Acer Helper itself. Design and rationale:
[../docs/lamparray.md](../docs/lamparray.md).

It exists as a separate driver package because Windows enumerates lighting devices **only** as HID LampArray
collections — there is no user-mode API to register one. This is the same construction Logitech ships
(`logi_lamparray.sys` + `LowerFilters = vhf` + a user-mode translation service).

## Build A — container cross-build (no Visual Studio, no WDK install)

`clang-cl`/`lld-link` speak the MSVC command line and read MSVC objects and libraries, and since WDK
10.0.26100.1 the WDK and SDK are published as [NuGet packages](https://learn.microsoft.com/en-us/windows-hardware/drivers/install-the-wdk-using-nuget).
Put those two facts in a Linux container and the driver builds without installing anything on Windows:

```bash
docker build -t acerhelper-wdk driver
```

```bash
docker run --rm -v "$PWD/driver/AcerHelperLampArray:/src" acerhelper-wdk
```

Output in `driver/AcerHelperLampArray/out/`: `AcerHelperLampArray.sys` (~23 KB), its `.pdb`, and the INF with
stampinf's `$TOKENS$` substituted. Verified against the produced binary: format `pei-x86-64`, entry point
`FxDriverEntry`, sections `.text`/`INIT`(discardable)/`PAGE`, imports `WDFLDR.SYS` + `ntoskrnl.exe`.

Details worth knowing about [`build.sh`](build.sh):

- Links **KMDF 1.33**, not the newest the WDK ships: the framework version must be present on the target, and
  1.33 is what Windows 11 22H2 (the INF's floor) has in-box. `KMDF_VERSION=1.35 docker run …` to change it.
- Toolchain paths are discovered, not hard-coded, because NuGet package layouts move between versions.
- The image lower-cases the whole package tree and the `#include` spellings inside it — the SDK headers are not
  internally consistent about case (`kernelspecs.h` asks for `DriverSpecs.h`; the file is `driverspecs.h`),
  which only matters on a case-sensitive filesystem.
- WDK/SDK headers are included with `/imsvc` (system includes), so the only warnings you see are the driver's
  own. It currently builds clean at `/W4`.

Visual Studio supports a ClangCL toolset for driver projects, so this is the same compiler the IDE would use —
just driven by hand instead of by MSBuild.

## Build B — MSBuild + WDK (the classic path)

Needs Visual Studio 2022 (**Desktop development with C++**) and the **WDK** for Windows 11, matching versions:

```bash
msbuild driver/AcerHelperLampArray/AcerHelperLampArray.vcxproj /p:Configuration=Release /p:Platform=x64
```

Output in `x64/Release/AcerHelperLampArray/`. This path also runs stampinf, inf2cat and (with a certificate
configured) signtool for you, and gives you Static Driver Verifier / CodeQL, which the container build does not.

## Signing

A driver package must carry a signature Windows will load. There are exactly two realistic options.

### Development: test signing

`signtool.exe`, `inf2cat.exe` and `makecert.exe` are inside the same NuGet packages, so they can be pulled out
of the container image and run natively — no WDK install required. [`sign-test.ps1`](sign-test.ps1) does that,
creates a self-signed code-signing certificate, builds the `.cat` and signs both it and the `.sys`:

```powershell
pwsh -File driver/sign-test.ps1
```

Making Windows *accept* it then needs two machine-level changes the script deliberately leaves to you:

```powershell
pwsh -File driver/sign-test.ps1 -Trust   # elevated: installs the cert into LocalMachine Root + TrustedPublisher
```

```bash
bcdedit /set testsigning on
```

Test signing **requires Secure Boot to be off** and leaves a desktop watermark, so this is for a dev box only.

### Release: attestation signing

For users, the package has to be signed by Microsoft through the Windows Hardware Developer Program:

1. An **EV code-signing certificate** (~€250–400/year from a supported CA). There is no substitute — Azure
   Trusted Signing is not accepted for driver attestation.
2. A free **Partner Center** hardware account, validated with that certificate.
3. Sign a `.cab` containing the package with the EV certificate, submit it for **attestation signing**, and
   ship the returned `.cat`.

The result looks exactly like Logitech's on an installed machine:
`Signer Name: Microsoft Windows Hardware Compatibility Publisher — Attested`. Secure Boot stays on and no
warning is shown.

## Install / uninstall

```bash
pnputil /add-driver AcerHelperLampArray.inf /install
```

That stages the package; **no device node is created here**. Acer Helper creates the node itself
(`SwDeviceCreate`) when you turn *Windows Dynamic Lighting* on in its Options, and removes it again when you
turn it off or the app exits — so Windows never lists a lighting device that nothing is backing.

To create the node by hand for debugging (WDK tool, needs the driver already staged):

```bash
devgen /add /hardwareid "Root\AcerHelperLampArray"
```

Uninstall — find the published name (`oemNN.inf`) first:

```bash
pnputil /enum-drivers
```

```bash
pnputil /delete-driver oemNN.inf /uninstall /force
```

## Verify

1. Device Manager → *Human Interface Devices* → **Acer Helper keyboard lighting (LampArray)**, plus a child
   HID device created by VHF underneath it.
2. `Get-PnpDevice -PresentOnly | Where-Object FriendlyName -match 'Acer Helper'`
3. Settings → Personalisation → **Dynamic Lighting** — the device appears; effects there should light the
   keyboard within ~100 ms (the rate the app advertises and enforces; see
   [../docs/lamparray.md](../docs/lamparray.md)).
4. Acer Helper's status line reports when a host takes or releases the backlight.

## Debugging

The driver carries no WPP tracing — it is small enough that a kernel debugger is the better tool:

```bash
windbg -k net:port=50000,key=1.2.3.4
```

Useful once attached: `!wdfkd.wdfldr`, `!wdfkd.wdfdevice <handle> ff`, and a breakpoint on
`AcerHelperLampArray!AhlaEvtSetFeature` to watch the host's frames arrive.

Common failures:

| Symptom | Cause |
|---|---|
| Device node with code 52 | package not signed acceptably (see *Signing*) |
| Device node with code 28 | package not staged — run `pnputil /add-driver` |
| Node starts, nothing in Dynamic Lighting | `LowerFilters = vhf` missing, or `VhfStart` failed — check `!wdfkd.wdfdevice` |
| App reports "device node created but the driver did not start" | as above; look at the node in Device Manager |
