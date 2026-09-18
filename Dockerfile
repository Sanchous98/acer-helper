# syntax=docker/dockerfile:1.7.1
#
# Release image for Acer Helper: ONE multi-stage file that builds the artefacts from committed sources, with
# every toolchain input pinned. The stages, and nothing else:
#
#   dotnet-base     the .NET SDK plus the Native AOT prerequisites (clang, ld.bfd/objcopy, zlib)
#   app-linux       builder -> the portable app, net10.0, Native AOT, linux-x64        -> /out/linux
#   wdk-toolchain   Debian + clang-cl/lld-link + the WDK/SDK unpacked from NuGet (driver/Dockerfile, lifted)
#   driver-image    wdk-toolchain + the entrypoint, i.e. the driver-only image driver/README.md documents
#   driver          builder -> the kernel driver, built by the unchanged driver/build.sh -> /out
#   artefacts       the export: an empty filesystem holding /linux, /driver, /PROVENANCE.txt
#
# BuildKit is required — `--mount=type=cache` for the NuGet cache and `--output=type=local` for the export are
# both BuildKit features. `docker build` on Docker Desktop and `docker buildx build` elsewhere both give it.
# The frontend is pinned to a patch version because the `:1` and `:1.7` tags move; everything used here has
# been supported since 1.3, so a mirror that cannot resolve 1.7.1 can be served by any newer frontend.
#
# How a caller gets the artefacts out — this is the documented entry point, and it is the whole of it:
#
#   docker buildx build --target artefacts --output type=local,dest=dist .
#
# -> dist/linux/… (the publish folder, the AppImage input), dist/driver/… (the .sys package) and
# dist/PROVENANCE.txt. docs/build-image.md has the `docker create`+`docker cp` fallback for a daemon without
# BuildKit, and the driver-only image the driver's own documentation builds.

# Global arguments: the pinned inputs, in one block, so a reader sees the whole toolchain before any stage.
# An ARG declared here is not visible inside a stage — each stage that uses one re-declares it without a
# value, which inherits the default below rather than restating it.
#
# The WDK, pinned on purpose: 26100 is the Windows 11 24H2 WDK (the 28000.x line is the Insider SDK). The
# driver targets Windows 11 22H2+, which this builds for fine. This ARG is the only place the version appears.
ARG WDK_VERSION=10.0.26100.6584

# Deliberately without a default: driver/build.sh owns the KMDF pin, and its default is 1.33 — the framework
# version a driver links against must be present on the target machine, and 1.33 is what Windows 11 22H2
# (build 22621, the floor AcerHelperLampArray.inf declares) has in-box. Leaving this empty hands the decision
# to build.sh, so there is one pin rather than two that can disagree; passing --build-arg KMDF_VERSION=1.35
# overrides it for a driver whose INF floor has been raised.
ARG KMDF_VERSION

# Recorded in PROVENANCE.txt only; nothing is fetched by it. A release folder that cannot name the commit it
# came from is not auditable, and .git is deliberately not in the build context (.dockerignore), so the value
# has to be handed in: --build-arg GIT_SHA="$(git rev-parse HEAD)". The default is plain text on purpose — a
# default containing a command substitution would be pasted into a RUN and executed by the shell there.
ARG GIT_SHA=not recorded

# ----------------------------------------------------------------------------------------------------------
# Stage: dotnet-base — the shared .NET toolchain.
# ----------------------------------------------------------------------------------------------------------
# The .NET side needs a C toolchain of its own before any .NET artefact can be built, and it is the expensive
# part of the image to install, so it lives in one stage instead of being repeated per builder: a second .NET
# builder (or a change to one) inherits the toolchain rather than reinstalling it, and a change to the app
# sources cannot invalidate this layer.
FROM mcr.microsoft.com/dotnet/sdk:10.0.301-noble AS dotnet-base

# The SDK tag pins the SDK, and the SDK pins the AOT compiler: Microsoft.DotNet.ILCompiler is referenced
# implicitly (the SDK's KnownILCompilerPack), so nothing in AcerHelper.csproj names a compiler version and
# nothing here can — 10.0.301 is the pin, and it is the version this repository is built with on the owner's
# machine (dotnet --list-sdks).
#
# The apt packages are the AOT prerequisites for this distribution, each pinned to the major version rather
# than left to the distribution's default, because a rebuild must not silently pick up a newer compiler:
#   clang-18  — the AOT compiler/linker driver. Microsoft.NETCore.Native.Unix.targets defaults
#               CppCompilerAndLinker to the unversioned "clang" and probes it with `command -v`; installing
#               only clang-18 leaves that name absent, so the publish below names the pinned binary instead.
#   binutils  — supplies ld.bfd, which the same targets ask for with -fuse-ld=bfd (LinkerFlavor defaults to
#               bfd on linux), and objcopy, the fallback symbol stripper: those targets probe llvm-objcopy
#               first and accept objcopy when it is missing, and StripSymbols is true off Windows.
#   zlib1g-dev — the runtime's own native libraries link against zlib; the documented Ubuntu prerequisite
#               line for AOT publishing is exactly "apt-get install clang zlib1g-dev".
# libicu is deliberately absent: the runtime loads ICU with dlopen, and the AOT link does not resolve ICU
# symbols at link time. If a future runtime changes that, the link fails naming ICU symbols and libicu-dev is
# the fix — it is not installed speculatively.
RUN apt-get update \
 && apt-get install -y --no-install-recommends clang-18 binutils zlib1g-dev \
 && rm -rf /var/lib/apt/lists/*

# ----------------------------------------------------------------------------------------------------------
# Stage: app-linux — the portable app, Native AOT, linux-x64.
# ----------------------------------------------------------------------------------------------------------
FROM dotnet-base AS app-linux

ARG WDK_VERSION
ARG GIT_SHA

WORKDIR /src
COPY . .

# The verbatim CI publish — .github/workflows/build.yml, the linux job — with exactly one addition: the clang
# pin explained above. The output path is CI's too (-o publish-linux), so the command and the artefact layout
# stay comparable with a release built on a runner. The NuGet cache is a BuildKit cache mount rather than an
# image layer: without it every source change invalidates the layer and re-downloads the AOT compiler and
# runtime packs (hundreds of MB).
#
# sharing=locked, from the first real build of this file (2026-09-18, measured the next day). That build died
# inside this RUN with
#
#   NuGet.targets(198,5): error : Directory not empty : '/root/.nuget/packages/skiasharp/3.119.4/lib'
#
# NuGet.targets:198 is the <RestoreTask> of the target `Restore`, so the failure is restore's package install
# refusing to lay a package into a directory that already had content. What that install left behind is the
# evidence: skiasharp/3.119.4 was a half-deleted tree — .signature.p7s, interactive-extensions/ and ref/
# intact, lib/ down to 17 of its 22 target-framework directories, and every loose file gone (the .nuspec,
# icon.png, LICENSE.txt, README.md, the .nupkg, its .sha512, and the .nupkg.metadata that marks an install
# complete). A delete that runs out of a directory it is emptying is what leaves a tree shaped like that, and
# it can only run out because something is writing into it at the same time — two writers in one directory.
#
# The default here is sharing=shared, which lets every exec that mounts this path write into it at once, and
# `docker buildx build` may be run more than once over a working tree (the build cache holds a second "local
# source for dockerfile" record created while the failing build was still running, so a second invocation was
# indeed submitted). sharing=locked serialises the writers: a second exec waits for the first instead of
# restoring into the same directories. It costs that wait and nothing else — the packages are not re-fetched,
# because the mount is still the same persistent cache.
#
# The trigger is NOT proven, and this is not a demonstrated repair. Every attempt to reproduce the error here
# succeeded instead: the exact command on a never-used cache mount, cold and then warm; the same command
# against a deliberately recreated half-deleted skiasharp; two concurrent restores in one build; six
# concurrent restores into one fresh mount; and a restore alone against the mount the failing build had left
# behind — which repaired it. NuGet repairs a half-deleted package directory whenever it is the only writer.
# What is measured is the two-writer hazard and the half-deleted tree; sharing=locked is the one change among
# the candidates that makes a second writer impossible.
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet publish AcerHelper.csproj \
      -c Release \
      -f net10.0 \
      -r linux-x64 \
      --self-contained true \
      -p:PublishAot=true \
      -p:CppCompilerAndLinker=clang-18 \
      -o publish-linux

# Everything the export stage needs, under one path. PROVENANCE.txt is written from values measured in this
# build (the SDK and clang versions are queried, not asserted) so the exported folder states its own
# toolchain; the pins that are ARGs are recorded here too, and the source revision only if it was handed in.
RUN mkdir -p /out/linux \
 && cp -a /src/publish-linux/. /out/linux/ \
 && printf '%s\n' \
      'Acer Helper — Linux build, from the release image (Dockerfile at the repository root)' \
      "SDK:            $(dotnet --version)" \
      "clang:          $(clang-18 --version | head -1)" \
      "publish:        dotnet publish AcerHelper.csproj -c Release -f net10.0 -r linux-x64 --self-contained true -p:PublishAot=true -p:CppCompilerAndLinker=clang-18" \
      "driver:         built by driver/build.sh as this image ships it; its KMDF resolution is printed in the build log (1.33, the Windows 11 22H2 floor AcerHelperLampArray.inf declares)" \
      "WDK:            ${WDK_VERSION}" \
      "source:         ${GIT_SHA}" \
      > /out/PROVENANCE.txt

# ----------------------------------------------------------------------------------------------------------
# Stage: wdk-toolchain — the driver's toolchain.
# ----------------------------------------------------------------------------------------------------------
# Lifted without change from driver/Dockerfile, which this stage supersedes: MSVC and the WDK installer are
# Windows-only, so a real WDK build would need a Windows container (Server Core + VS Build Tools, ~10 GB, and
# it means switching the Docker daemon out of Linux mode). This stage instead uses clang-cl/lld-link — which
# speak the MSVC command line and read MSVC object/library formats — against the WDK and SDK shipped as NuGet
# packages (the officially supported acquisition path since WDK 10.0.26100.1:
# learn.microsoft.com/windows-hardware/drivers/install-the-wdk-using-nuget). Visual Studio itself supports a
# ClangCL toolset for driver projects, so this is not an exotic combination; it is the same compiler, driven
# by hand instead of by MSBuild.
#
# Kernel-mode headers in the WDK are all lower-case, so none of the case-sensitivity fixups that plague
# cross-compiling *user-mode* Windows code are needed here.
#
# The base stays debian:trixie-slim rather than joining the .NET side on the SDK image. The two toolchains are
# different distributions, and this one is the measured one: the driver's build has been verified against this
# Debian clang, and moving it to another LLVM major to save an apt-get layer would be changing the solved part
# to tidy the file.
FROM debian:trixie-slim AS wdk-toolchain

ARG WDK_VERSION

# clang/lld provide clang-cl and lld-link; the rest is just to fetch and unpack nupkgs.
RUN apt-get update \
 && apt-get install -y --no-install-recommends clang lld curl ca-certificates unzip \
 && rm -rf /var/lib/apt/lists/*

# The SDK package carries the shared/um headers the kernel headers pull in; the .x64 packages carry the libs.
RUN set -eux; \
    mkdir -p /opt/wdk; \
    for pkg in microsoft.windows.sdk.cpp microsoft.windows.sdk.cpp.x64 microsoft.windows.wdk.x64; do \
        curl -fsSL -o "/tmp/${pkg}.nupkg" \
            "https://api.nuget.org/v3-flatcontainer/${pkg}/${WDK_VERSION}/${pkg}.${WDK_VERSION}.nupkg"; \
        unzip -q -o "/tmp/${pkg}.nupkg" -d /opt/wdk; \
        rm "/tmp/${pkg}.nupkg"; \
    done

# Linux is case-sensitive; the SDK/WDK headers are not internally consistent about case (kernelspecs.h asks
# for "DriverSpecs.h", the file on disk is driverspecs.h — 689 headers have mixed-case names). Normalise once,
# at image build time: lower-case every path, then lower-case every #include spelling inside the headers. This
# is the same fix the msvc-wine project applies, and it is why no per-header symlink list is needed.
RUN set -eu; \
    cd /opt/wdk; \
    find . -depth -name '*[A-Z]*' | while IFS= read -r p; do \
        d="$(dirname "$p")"; b="$(basename "$p")"; l="$(printf '%s' "$b" | tr 'A-Z' 'a-z')"; \
        [ "$b" = "$l" ] || [ -e "$d/$l" ] || mv -T "$d/$b" "$d/$l"; \
    done; \
    find /opt/wdk -type f \( -name '*.h' -o -name '*.inl' \) -print0 \
      | xargs -0 -r -P4 sed -i -E 's@^([[:space:]]*#[[:space:]]*include[[:space:]]*[<"])([^>"]+)@\1\L\2@'

# The driver's build script, called and not reimplemented: the WDK/SDK discovery, the KMDF library selection,
# the /imsvc include handling and the INF token substitution are all solved there, and a second copy would be
# a second thing to keep correct. It is shipped at this path in both images below.
COPY driver/build.sh /usr/local/bin/build-driver
RUN chmod +x /usr/local/bin/build-driver

# ----------------------------------------------------------------------------------------------------------
# Stage: driver-image — the driver-only image, exactly what driver/Dockerfile used to be.
# ----------------------------------------------------------------------------------------------------------
# It exists so that the flow driver/README.md documents keeps working verbatim: build this target, then mount
# the driver sources over /src and let the entrypoint build them. Nothing else depends on this stage — the
# release image builds the driver in the `driver` stage below, from the sources it COPYs — so targeting
# `artefacts` does not pay for it.
FROM wdk-toolchain AS driver-image

WORKDIR /src
ENTRYPOINT ["/usr/local/bin/build-driver"]

# ----------------------------------------------------------------------------------------------------------
# Stage: driver — the kernel driver as a release artefact.
# ----------------------------------------------------------------------------------------------------------
FROM wdk-toolchain AS driver

ARG KMDF_VERSION

# Only the five files driver/build.sh reads: the source, the three headers it includes, and the INF it stamps.
# COPYing the directory instead would drag in the locally built .sys/.pdb, which is precisely what the
# provenance of a release artefact must not depend on.
COPY driver/AcerHelperLampArray/driver.c /src/driver.c
COPY driver/AcerHelperLampArray/driver.h /src/driver.h
COPY driver/AcerHelperLampArray/lamparray.h /src/lamparray.h
COPY driver/AcerHelperLampArray/public.h /src/public.h
COPY driver/AcerHelperLampArray/AcerHelperLampArray.inf /src/AcerHelperLampArray.inf

# KMDF_VERSION is empty unless the caller set it, and build.sh reads an empty value as unset — so the pin
# stays where it is written down (build.sh) and the build log still prints the version that was resolved.
# OUT moves the output off /src, so what is exported is the new package and not a mixture with any leftovers.
RUN OUT=/out KMDF_VERSION=${KMDF_VERSION} /usr/local/bin/build-driver /src

# ----------------------------------------------------------------------------------------------------------
# Stage: artefacts — the export.
# ----------------------------------------------------------------------------------------------------------
# An empty filesystem holding only the artefacts, so `--output type=local` never tries to write an SDK image
# or a WDK tree to the caller's disk.
FROM scratch AS artefacts
COPY --from=app-linux /out/linux /linux
COPY --from=driver /out /driver
COPY --from=app-linux /out/PROVENANCE.txt /PROVENANCE.txt

# ----------------------------------------------------------------------------------------------------------
# Why there is no app-windows stage here
# ----------------------------------------------------------------------------------------------------------
# The Windows artefact is Native AOT for win-x64, and the SDK refuses to cross-compile it from Linux. The
# refusal is not policy in prose, it is an error in the toolchain this file installs:
#
#   * Microsoft.NETCore.Native.Publish.targets fails the publish with "Cross-OS native compilation is not
#     supported." — the guard covers both directions — unless DisableUnsupportedError is set.
#   * The same file fails it again with "Add a PackageReference for
#     'runtime.linux-x64.Microsoft.DotNet.ILCompiler' to allow cross-compilation": the compiler that runs on
#     the host and the target's native libraries are two different packages, and naming them means a
#     PackageReference, which means editing AcerHelper.csproj. This image is not allowed to, and should not.
#   * Microsoft.NETCore.Native.Windows.targets hard-wires CppLinker to link.exe, runs findvcvarsall.bat (a
#     batch file, so cmd.exe) to locate the toolchain, and says so when it cannot: "Platform linker not found.
#     … Desktop Development for C++ workload in Visual Studio."
#   * The libraries the link needs are not all in the NuGet packages this file fetches. The Windows SDK ones
#     are: the fifteen SdkNativeLibrary entries (kernel32.lib, ole32.lib, …), the /DEFAULTLIB:ucrt.lib that
#     same file forces, and uuid.lib are all in microsoft.windows.sdk.cpp(.x64). But the runtime pack's own
#     native libraries carry /DEFAULTLIB:LIBCMT, /DEFAULTLIB:libcpmt and /DEFAULTLIB:OLDNAMES (measured by
#     scanning the native/ folder of Microsoft.NETCore.App.Runtime.NativeAOT.win-x64), and those are the MSVC
#     static CRT — Visual Studio's, not the Windows SDK's, and absent from every WDK/SDK NuGet package.
#
# Getting past this means msvc-wine or xwin for a VC toolchain, DisableUnsupportedError, a CppCompilerAndLinker
# shim to feed the MSVC-style link response file to lld-link, and a PackageReference in the csproj: an
# unsupported combination held together by a third-party MSBuild package, and a second toolchain to pin. It is
# not done here. The Windows artefact is built where the supported toolchain exists — a Windows host, as the
# `windows` job of .github/workflows/build.yml already does — and docs/build-image.md records what a Windows
# container would add for anyone who wants the build in a container on that side too.
