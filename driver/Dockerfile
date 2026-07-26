# Cross-compile AcerHelperLampArray.sys in a Linux container.
#
# Why not the "normal" toolchain: MSVC + the WDK installer are Windows-only, so a real WDK build needs a
# Windows container (Server Core + VS Build Tools, ~10 GB, and it means switching the Docker daemon out of
# Linux mode). This image instead uses clang-cl/lld-link — which speak the MSVC command line and read MSVC
# object/library formats — against the WDK and SDK shipped as NuGet packages (the officially supported
# acquisition path since WDK 10.0.26100.1: learn.microsoft.com/windows-hardware/drivers/install-the-wdk-using-nuget).
# Visual Studio itself supports a ClangCL toolset for driver projects, so this is not an exotic combination;
# it is the same compiler, driven by hand instead of by MSBuild.
#
# Kernel-mode headers in the WDK are all lower-case, so none of the case-sensitivity fixups that plague
# cross-compiling *user-mode* Windows code are needed here.
#
# What comes out: an unsigned AcerHelperLampArray.sys. Signing and .cat generation need signtool/inf2cat
# (native Windows executables — they ship inside the same NuGet packages and run on the host without Visual
# Studio); see README.md.
#
#   docker build -t acerhelper-wdk driver
#   docker run --rm -v "<repo>/driver:/src" acerhelper-wdk

FROM debian:trixie-slim

# clang/lld provide clang-cl and lld-link; the rest is just to fetch and unpack nupkgs.
RUN apt-get update \
 && apt-get install -y --no-install-recommends clang lld curl ca-certificates unzip \
 && rm -rf /var/lib/apt/lists/*

# Pinned on purpose: 26100 is the Windows 11 24H2 WDK (the 28000.x line is the Insider SDK). The driver
# targets Windows 11 22H2+, which this builds for fine.
ARG WDK_VERSION=10.0.26100.6584

# The SDK package carries the shared/um headers the kernel headers pull in; the .x64 packages carry the libs.
RUN set -eux; \
    mkdir -p /opt/wdk; \
    for pkg in microsoft.windows.sdk.cpp microsoft.windows.sdk.cpp.x64 microsoft.windows.wdk.x64; do \
        curl -fsSL -o "/tmp/${pkg}.nupkg" \
            "https://api.nuget.org/v3-flatcontainer/${pkg}/${WDK_VERSION}/${pkg}.${WDK_VERSION}.nupkg"; \
        unzip -q -o "/tmp/${pkg}.nupkg" -d /opt/wdk; \
        rm "/tmp/${pkg}.nupkg"; \
    done

# Linux is case-sensitive; the SDK/WDK headers are not internally consistent about case (kernelspecs.h asks for
# "DriverSpecs.h", the file on disk is driverspecs.h — 689 headers have mixed-case names). Normalise once, at
# image build time: lower-case every path, then lower-case every #include spelling inside the headers. This is
# the same fix the msvc-wine project applies, and it is why no per-header symlink list is needed.
RUN set -eu; \
    cd /opt/wdk; \
    find . -depth -name '*[A-Z]*' | while IFS= read -r p; do \
        d="$(dirname "$p")"; b="$(basename "$p")"; l="$(printf '%s' "$b" | tr 'A-Z' 'a-z')"; \
        [ "$b" = "$l" ] || [ -e "$d/$l" ] || mv -T "$d/$b" "$d/$l"; \
    done; \
    find /opt/wdk -type f \( -name '*.h' -o -name '*.inl' \) -print0 \
      | xargs -0 -r -P4 sed -i -E 's@^([[:space:]]*#[[:space:]]*include[[:space:]]*[<"])([^>"]+)@\1\L\2@'

COPY build.sh /usr/local/bin/build-driver
RUN chmod +x /usr/local/bin/build-driver

WORKDIR /src
ENTRYPOINT ["/usr/local/bin/build-driver"]
