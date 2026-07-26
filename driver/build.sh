#!/bin/sh
# Build AcerHelperLampArray.sys with clang-cl/lld-link against the NuGet WDK. Runs inside the container built
# by ./Dockerfile; expects the driver sources bind-mounted at /src (or $1).
#
# Everything about the toolchain is DISCOVERED rather than hard-coded: NuGet package layouts move between
# versions, and a build that guesses paths fails with "file not found" a dozen layers deep instead of saying
# what it could not find.
set -eu

SRC="${1:-/src}"
OUT="${OUT:-$SRC/out}"
WDK_ROOT="${WDK_ROOT:-/opt/wdk}"

# clang-cl / lld-link are shipped under versioned names on Debian; --driver-mode=cl always works.
CL="${CL:-clang --driver-mode=cl}"
LINK="$(command -v lld-link || command -v ld.lld || true)"
[ -n "$LINK" ] || LINK="$(ls /usr/lib/llvm-*/bin/lld-link 2>/dev/null | head -1)"
[ -n "$LINK" ] || { echo "lld-link not found" >&2; exit 1; }

find_one() {
    # find_one <description> <find args...>
    desc="$1"; shift
    hit="$(find "$WDK_ROOT" "$@" 2>/dev/null | sort | tail -1)"
    [ -n "$hit" ] || { echo "cannot locate $desc under $WDK_ROOT" >&2; exit 1; }
    echo "$hit"
}

# Resolve a library by name inside a known directory, case-insensitively: the image lower-cases the whole
# package tree (see Dockerfile), so WdfDriverEntry.lib is on disk as wdfdriverentry.lib.
lib_in() {
    hit="$(find "$1" -maxdepth 1 -iname "$2" 2>/dev/null | head -1)"
    [ -n "$hit" ] || { echo "cannot locate $2 in $1" >&2; exit 1; }
    echo "$hit"
}

# KMDF 1.33 by default, NOT the newest the WDK ships: the framework version a driver links against must be
# present on the target, and 1.33 is what Windows 11 22H2 (build 22621, the INF's floor) has in-box. Override
# with KMDF_VERSION=1.35 if you raise that floor.
KMDF_VERSION="${KMDF_VERSION:-1.33}"

WDM_H="$(find_one 'km/wdm.h'            -iname wdm.h            -ipath '*/km/*')"
WDF_H="$(find_one "kmdf $KMDF_VERSION wdf.h" -iname wdf.h       -ipath "*kmdf/$KMDF_VERSION/*")"
NTOSKRNL="$(find_one 'ntoskrnl.lib'     -iname ntoskrnl.lib     -ipath '*x64*')"
WDFENTRY="$(find_one "WdfDriverEntry.lib $KMDF_VERSION" -iname wdfdriverentry.lib -ipath "*x64/$KMDF_VERSION*")"
VHFKM="$(find_one 'vhfkm.lib'           -iname vhfkm.lib        -ipath '*x64*')"

KM_INC="$(dirname "$WDM_H")"
INC_ROOT="$(dirname "$KM_INC")"
KMDF_INC="$(dirname "$WDF_H")"
KMDF_VER="$(basename "$KMDF_INC")"          # e.g. 1.33
KMDF_MAJOR="${KMDF_VER%%.*}"
KMDF_MINOR="${KMDF_VER##*.}"
KM_LIB="$(dirname "$NTOSKRNL")"
KMDF_LIB="$(dirname "$WDFENTRY")"

echo "== toolchain =="
echo "  cl        : $CL ($(clang --version | head -1))"
echo "  link      : $LINK"
echo "  km inc    : $KM_INC"
echo "  shared inc: $INC_ROOT/shared"
echo "  kmdf      : $KMDF_INC (KMDF $KMDF_MAJOR.$KMDF_MINOR)"
echo "  km lib    : $KM_LIB"
echo "  kmdf lib  : $KMDF_LIB"
echo "  vhfkm     : $VHFKM"

mkdir -p "$OUT"

# Defines match what the WDK's own property sheets set for an x64 KMDF driver. NTDDI_VERSION 0x0A000000 is
# Windows 10/11 (the INF's floor is 22621, enforced there, not here).
DEFINES="
-D_WIN64 -D_AMD64_ -DAMD64 -D_KERNEL_MODE -DSTD_CALL -DDEPRECATE_DDK_FUNCTIONS=1 -DPOOL_NX_OPTIN=1
-DNTDDI_VERSION=0x0A000000 -D_WIN32_WINNT=0x0A00 -DWINVER=0x0A00
-DKMDF_VERSION_MAJOR=$KMDF_MAJOR -DKMDF_VERSION_MINOR=$KMDF_MINOR
"

# /imsvc marks the WDK/SDK headers as SYSTEM includes, so clang stops reporting their own warnings (deprecated
# ExAllocatePoolWithTag, MS-extension enums, unhandled enum values in wdfdevice.h inlines, ...). Only warnings
# from the driver's own sources — under plain -I — are left, which is the whole point of building with /W4.
INCLUDES="
-I$SRC
/imsvc$KM_INC
/imsvc$INC_ROOT/shared
/imsvc$INC_ROOT/km/crt
/imsvc$KMDF_INC
"

echo "== compile =="
# /GS is on (kernel drivers want the stack cookie; BufferOverflowFastFailK.lib supplies its runtime).
# The pragma/attribute warnings clang emits for MS-specific spellings in the WDK headers are noise, not signal.
# shellcheck disable=SC2086
$CL /c /nologo /W4 /WX- /O2 /Oy- /GS /Gy /Zc:wchar_t /Zl \
    -fms-compatibility -fms-extensions -Wno-unknown-pragmas -Wno-ignored-attributes \
    -Wno-microsoft-anon-tag -Wno-pragma-pack -Wno-nonportable-include-path \
    -Wno-ignored-pragma-intrinsic -Wno-microsoft-enum-forward-reference \
    -Wno-microsoft-static-assert -Wno-unused-const-variable \
    $DEFINES $INCLUDES \
    /Fo"$OUT/driver.obj" "$SRC/driver.c"

echo "== link =="
# The flags a KMDF driver needs: /DRIVER marks the PE as a kernel driver, FxDriverEntry is KMDF's stub entry
# point (it calls our DriverEntry), /NODEFAULTLIB keeps the user-mode CRT out, and the section merges +
# "INIT,d" (discardable) are what make PAGED_CODE/INIT sections behave.
# shellcheck disable=SC2086
"$LINK" /OUT:"$OUT/AcerHelperLampArray.sys" /NOLOGO \
    /DRIVER /SUBSYSTEM:NATIVE,10.00 /ENTRY:FxDriverEntry /NODEFAULTLIB \
    /OSVERSION:10.0 /VERSION:10.0 /RELEASE /OPT:REF /OPT:ICF /INCREMENTAL:NO /DEBUG \
    /PDB:"$OUT/AcerHelperLampArray.pdb" \
    /MERGE:_TEXT=.text /MERGE:_PAGE=PAGE /SECTION:INIT,d \
    /IGNORE:4210 \
    "$OUT/driver.obj" \
    "$WDFENTRY" "$(lib_in "$KMDF_LIB" wdfldr.lib)" \
    "$NTOSKRNL" "$(lib_in "$KM_LIB" hal.lib)" "$(lib_in "$KM_LIB" wmilib.lib)" \
    "$(lib_in "$KM_LIB" bufferoverflowfastfailk.lib)" \
    "$VHFKM"

# Stamp the INF. The checked-in file keeps stampinf's $TOKENS$ so an MSBuild/WDK build works unchanged; here we
# substitute them ourselves, because pnputil does not understand them.
# The tokens are spelled [$] rather than \$: in a sed BRE a trailing '$' is an end-of-line anchor, so the obvious
# s/\$KMDFVERSION\$/…/ silently matches nothing.
sed -e "s/[\$]KMDFVERSION[\$]/$KMDF_VER/g" \
    -e "s/NT[\$]ARCH[\$]/NTamd64/g" \
    "$SRC/AcerHelperLampArray.inf" > "$OUT/AcerHelperLampArray.inf"

grep -q 'KmdfLibraryVersion = [0-9]' "$OUT/AcerHelperLampArray.inf" \
    || { echo "INF token substitution failed" >&2; exit 1; }

echo "== done =="
ls -l "$OUT"
echo
echo "NOTE: the .sys is UNSIGNED and there is no .cat — signing needs signtool/inf2cat (native Windows"
echo "      binaries, shipped in the same NuGet packages). See README.md."
