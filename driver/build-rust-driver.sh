#!/bin/sh
# Build the Rust AcerHelperLampArray.sys with cargo, and package it exactly as driver/build.sh packages the C
# one: the same .sys name, the same .pdb, the same INF with the same tokens substituted.
#
# It is a separate script rather than a mode of build.sh because the two drivers are two builds that happen to
# produce the same package shape, and build.sh is not to be touched. The parts that must agree are stated as
# assertions at the bottom rather than as a shared file, so a drift is a failed build and not a silent one.
#
# Runs inside the container built by the repository Dockerfile, in the `driver-rust` stage; expects the crate
# at /src (or $1) and the INF at $INF (or beside the C driver).
set -eu

SRC="${1:-/src}"
OUT="${OUT:-$SRC/out}"

# The INF is the same file the C driver stamps, and this crate deliberately does not carry a second copy of it
# — two copies of a package's INF are two things to keep in step, and the INF is where the hardware id,
# Security, PnpLockdown, LowerFilters, KmdfService and the 22621 floor live. So it is looked for, in the
# places a caller can put it: beside the crate (a whole-tree mount), where the release stage COPYs it, or —
# the documented `docker run` — mounted on its own. INF= overrides.
if [ -z "${INF:-}" ]; then
    for candidate in "$SRC/AcerHelperLampArray.inf" \
                     "$SRC/../AcerHelperLampArray/AcerHelperLampArray.inf" \
                     /inf/AcerHelperLampArray.inf; do
        if [ -f "$candidate" ]; then INF="$candidate"; break; fi
    done
fi
[ -n "${INF:-}" ] && [ -f "$INF" ] \
    || { echo "cannot find AcerHelperLampArray.inf; mount it and set INF=" >&2; exit 1; }

# The WDK the headers are parsed from and the libraries linked against. The image unpacks the NuGet packages
# to /opt/wdk/c, which is where build.rs looks by default too.
WDK_CONTENT_ROOT="${WDKContentRoot:-${WDK_ROOT:-/opt/wdk/c}}"
export WDKContentRoot="$WDK_CONTENT_ROOT"

VERSION_FILE="$SRC/src/kmdf_version.rs"
[ -f "$VERSION_FILE" ] || { echo "cannot find $VERSION_FILE" >&2; exit 1; }

# THE PIN, read from the one place it is written down. src/kmdf_version.rs is also what build.rs parses into
# the bindgen defines and into the name of the KMDF function-table symbol, and what the Rust side exports as
# WdfMinimumVersionRequired. There is no second copy of this number anywhere in the Rust driver.
KMDF_MAJOR="$(sed -n 's/^pub const KMDF_MAJOR: u32 = \([0-9][0-9]*\);$/\1/p' "$VERSION_FILE")"
KMDF_MINOR="$(sed -n 's/^pub const KMDF_MINOR: u32 = \([0-9][0-9]*\);$/\1/p' "$VERSION_FILE")"
[ -n "$KMDF_MAJOR" ] && [ -n "$KMDF_MINOR" ] \
    || { echo "cannot read the KMDF version from $VERSION_FILE" >&2; exit 1; }
KMDF_VER="$KMDF_MAJOR.$KMDF_MINOR"

CARGO_HOME="${CARGO_HOME:-/usr/local/cargo}"
CARGO_TARGET_DIR="${CARGO_TARGET_DIR:-$SRC/target}"
export CARGO_HOME CARGO_TARGET_DIR

echo "== rust driver =="
echo "  cargo     : $(cargo --version)"
echo "  rustc     : $(rustc --version)"
echo "  target    : x86_64-pc-windows-msvc"
echo "  linker    : lld-link ($(lld-link --version 2>/dev/null | head -1 || echo 'lld'))"
echo "  WDK       : $WDK_CONTENT_ROOT"
echo "  KMDF      : $KMDF_VER (from src/kmdf_version.rs; INF stamped with the same value)"
echo "  clang/libclang: $(clang --version | head -1)"

echo "== cargo build =="
# --locked so the dependency graph is the one in Cargo.lock. The only build dependency is bindgen, and it is
# needed to parse the WDK headers — see build.rs for why windows-drivers-rs's wdk-sys is not used instead.
(cd "$SRC" && cargo build --release --locked)

DLL="$CARGO_TARGET_DIR/x86_64-pc-windows-msvc/release/AcerHelperLampArrayRust.dll"
PDB="$CARGO_TARGET_DIR/x86_64-pc-windows-msvc/release/AcerHelperLampArrayRust.pdb"
[ -f "$DLL" ] || { echo "cargo produced no $DLL" >&2; exit 1; }

echo "== package =="
mkdir -p "$OUT"
cp -f "$DLL" "$OUT/AcerHelperLampArray.sys"
[ -f "$PDB" ] && cp -f "$PDB" "$OUT/AcerHelperLampArray.pdb"

# Stamp the INF. Identical to driver/build.sh's substitution, because the INF is the same file and pnputil
# does not understand $TOKENS$. The tokens are spelled [$] rather than \$: in a sed BRE a trailing '$' is an
# end-of-line anchor, so the obvious s/\$KMDFVERSION\$/.../ silently matches nothing.
sed -e "s/[\$]KMDFVERSION[\$]/$KMDF_VER/g" \
    -e "s/NT[\$]ARCH[\$]/NTamd64/g" \
    "$INF" > "$OUT/AcerHelperLampArray.inf"

echo "== checks =="

# 1. The INF declares the framework the driver was built against. This is the whole content of defect 3, and
#    it is checkable precisely because both sides read src/kmdf_version.rs. The value is extracted and
#    compared rather than the line being grepped for a number, so a *wrong* value fails too — build.sh's
#    `grep -q 'KmdfLibraryVersion = [0-9]'` only proves the substitution did something.
#
#    `tr -d '\r'` is not defensive programming: the INF reaches the container with CRLF line endings (the
#    build context on a Windows checkout is the working tree, and .gitattributes normalises on commit, not on
#    checkout), so an end-anchored pattern would not match an otherwise correct stamp. The C driver's INF has
#    the same endings, for the same reason, and its own check simply has no anchor.
DECLARED_KMDF="$(sed -n 's/^KmdfLibraryVersion[[:space:]]*=[[:space:]]*\([0-9][0-9.]*\).*$/\1/p' \
    "$OUT/AcerHelperLampArray.inf" | tr -d '\r')"
[ "$DECLARED_KMDF" = "$KMDF_VER" ] \
    || { echo "INF declares KmdfLibraryVersion = '${DECLARED_KMDF:-<none>}', expected $KMDF_VER" >&2; exit 1; }
# The two things the INF's floor depends on: the architecture token was substituted, and the declared floor
# is still Windows 11 22H2 — the build that has KMDF 1.33 in-box, which is why 1.33 is the pin.
grep -q 'NTamd64' "$OUT/AcerHelperLampArray.inf" \
    || { echo "INF architecture token substitution failed" >&2; exit 1; }
grep -q 'NT\$ARCH\$' "$OUT/AcerHelperLampArray.inf" \
    && { echo "INF still contains an unsubstituted \$TOKEN\$" >&2; exit 1; }

# 2. What came out is a kernel driver. There is no way to load it here — it is unsigned, loading needs test
#    mode, and a driver fault is a blue screen on the owner's machine — so "compiles and packages" is the
#    whole of the verification and it is done by inspecting the image, not by running it.
objdump -p "$OUT/AcerHelperLampArray.sys" | grep -q 'Subsystem.*NT native' \
    || { echo "AcerHelperLampArray.sys is not a NATIVE subsystem PE" >&2; exit 1; }
objdump -p "$OUT/AcerHelperLampArray.sys" | grep -q 'DLL Name: WDFLDR.SYS' \
    || { echo "AcerHelperLampArray.sys does not import WDFLDR.SYS" >&2; exit 1; }

echo "  INF     : KmdfLibraryVersion = $KMDF_VER, NTamd64, no unsubstituted tokens"
echo "  image   : PE x64, Subsystem NATIVE, imports WDFLDR.SYS"

echo "== done =="
ls -l "$OUT"
echo
echo "NOTE: the .sys is UNSIGNED and there is no .cat — signing needs signtool/inf2cat (native Windows"
echo "      binaries, shipped in the same NuGet packages). See README.md."
echo "NOTE: this driver has never been loaded. Its verification is that it compiles and packages; see"
echo "      docs/rust-driver.md."
