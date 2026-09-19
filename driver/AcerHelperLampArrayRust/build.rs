// Build script for the Rust AcerHelperLampArray driver.
//
// It does three jobs, and the third is the one worth explaining:
//
//   1. PARSE THE WDK HEADERS. bindgen runs here, on the build host, against the same NuGet-unpacked WDK that
//      `driver/build.sh` compiles the C driver against. bindgen is a cross-platform library — it dlopens
//      libclang and reads headers — and its generated code carries `size_of`/`offset_of` assertions against
//      every record it emits, so the struct layouts the driver uses are verified by the compiler on every
//      build rather than transcribed by hand. `windows-drivers-rs`'s `wdk-sys` does the same thing; what it
//      additionally does is refuse to build off Windows (see docs/), so this crate reaches for bindgen
//      directly instead.
//
//   2. NAME THE FUNCTION TABLE. KMDF does not link `WdfDriverCreate` and friends as ordinary imports: a KMDF
//      driver reaches every `Wdf*` API through a table of function pointers, `WdfFunctions_01033`, imported
//      from WDFLDR and indexed by the `WdfFunctionTableIndex` values in `wdffuncenum.h`. The symbol's NAME
//      carries the framework version. This script writes a tiny generated module naming it from the pinned
//      version, so the version appears in exactly one place in the crate (src/kmdf_version.rs) and the symbol
//      is spelled from it — a build resolved against a different framework is then an unresolved symbol at
//      link time, not a driver that silently binds to a newer KMDF than its INF declares.
//
//   3. EMIT THE KERNEL LINK. The flags are the ones `driver/build.sh` passes to lld-link for the C driver,
//      translated to what rustc forwards: /DRIVER, /SUBSYSTEM:NATIVE, the discardable INIT section, and
//      /NODEFAULTLIB (a kernel driver must not link the user-mode CRT — which is also why src/lib.rs defines
//      its own memcpy/memset/memmove/memcmp).

use std::env;
use std::path::{Path, PathBuf};

mod kmdf_version {
    include!("src/kmdf_version.rs");
}

/// Find a file by name anywhere under `root`, case-insensitively, and return the newest match.
///
/// Discovery rather than hard-coded paths, for the same reason `driver/build.sh` does it: the NuGet package
/// layout is not a stable interface, and a build that guesses reports "file not found" a dozen frames deep
/// instead of saying what it could not find.
fn find_one(root: &Path, name: &str, must_contain: &str, want_dir: bool) -> PathBuf {
    let want = name.to_ascii_lowercase();
    let mut hits: Vec<PathBuf> = Vec::new();
    let mut stack = vec![root.to_path_buf()];
    while let Some(dir) = stack.pop() {
        let Ok(entries) = std::fs::read_dir(&dir) else {
            continue;
        };
        for e in entries.flatten() {
            let p = e.path();
            if p.is_dir() {
                stack.push(p);
                continue;
            }
            let Some(file) = p.file_name().and_then(|s| s.to_str()) else {
                continue;
            };
            if file.to_ascii_lowercase() != want {
                continue;
            }
            let full = p.to_string_lossy().replace('\\', "/").to_ascii_lowercase();
            if must_contain.is_empty() || full.contains(&must_contain.to_ascii_lowercase()) {
                hits.push(p);
            }
        }
    }
    hits.sort();
    let hit = if want_dir { hits.pop() } else { hits.pop() };
    hit.unwrap_or_else(|| {
        panic!("cannot locate {name} (containing {must_contain:?}) under {}", root.display())
    })
}

fn main() {
    println!("cargo:rerun-if-changed=build.rs");
    println!("cargo:rerun-if-changed=src/kmdf_version.rs");
    println!("cargo:rerun-if-changed=wrapper.h");

    let out_dir = PathBuf::from(env::var("OUT_DIR").expect("OUT_DIR"));

    // The WDK root. The image unpacks the WDK/SDK NuGet packages to /opt/wdk; a developer's own checkout can
    // point elsewhere with WDKContentRoot. `wdk-build` in windows-drivers-rs looks for the same variable
    // first and falls back to the registry — the registry half is Windows-only and `#[cfg]`-gated, which is
    // one of the reasons that crate will not build here.
    let wdk_root = env::var("WDKContentRoot")
        .or_else(|_| env::var("WDK_ROOT"))
        .unwrap_or_else(|_| "/opt/wdk/c".to_string());
    let wdk_root = PathBuf::from(wdk_root);
    if !wdk_root.is_dir() {
        panic!("WDKContentRoot {} is not a directory", wdk_root.display());
    }

    let version = format!("{}.{}", kmdf_version::KMDF_MAJOR, kmdf_version::KMDF_MINOR);

    // Header directories. The image lower-cases the whole package tree and every #include spelling inside it,
    // because the SDK headers are not internally consistent about case and Linux is case-sensitive.
    let wdm_h = find_one(&wdk_root, "wdm.h", "/km/", false);
    let km_inc = wdm_h.parent().expect("km include dir").to_path_buf();
    let inc_root = km_inc.parent().expect("include root").to_path_buf();
    let wdf_h = find_one(&wdk_root, "wdf.h", &format!("kmdf/{version}/"), false);
    let kmdf_inc = wdf_h.parent().expect("kmdf include dir").to_path_buf();

    // Library directories, and the framework entry stub for the pinned version.
    let ntoskrnl = find_one(&wdk_root, "ntoskrnl.lib", "/x64/", false);
    let km_lib = ntoskrnl.parent().expect("km lib dir").to_path_buf();
    let wdfentry = find_one(&wdk_root, "wdfdriverentry.lib", &format!("/x64/{version}/"), false);
    let kmdf_lib = wdfentry.parent().expect("kmdf lib dir").to_path_buf();
    let vhfkm = find_one(&wdk_root, "vhfkm.lib", "/x64/", false);

    let lib_in = |dir: &Path, name: &str| -> PathBuf { find_one(dir, name, "", false) };

    // Printed rather than announced through `cargo:warning=` so that a driver build reports zero warnings:
    // the resolved framework is worth a log line (driver/build.sh prints the same one for the C driver) but
    // it is not a diagnostic. `driver/build-rust-driver.sh` prints it, reading the same value from here.
    println!(
        "cargo:rustc-env=AHLA_KMDF_ENTRY_STUB={}",
        wdfentry.display()
    );

    // ---- the function-table symbol, spelled from the pinned version ------------------------------------
    //
    // `WdfFunctions_01033` is `WdfFunctions_<major:02>0<minor:02>` — the spelling wdf.h uses to select the
    // table, and the same one windows-drivers-rs builds. Writing it from the constant means this crate's
    // source never names a framework version at all.
    let table_symbol = format!(
        "WdfFunctions_{:02}0{:02}",
        kmdf_version::KMDF_MAJOR,
        kmdf_version::KMDF_MINOR
    );
    std::fs::write(
        out_dir.join("kmdf_symbol.rs"),
        format!(
            "// @generated by build.rs from src/kmdf_version.rs — do not edit.\n\
             //\n\
             // The KMDF function table this driver binds to. Its name is the framework version, so a build\n\
             // that resolved a different framework than the INF declares fails here rather than at runtime.\n\
             pub const KMDF_VERSION_MAJOR: u32 = {};\n\
             pub const KMDF_VERSION_MINOR: u32 = {};\n\
             pub const KMDF_VERSION: &str = \"{}\";\n\
             \n\
             /// The imported table of `Wdf*` entry points, one slot per `WdfFunctionTableIndex`.\n\
             #[inline(always)]\n\
             pub fn wdf_functions() -> *const crate::ffi::WDFFUNC {{\n\
                 unsafe {{ crate::ffi::{table_symbol} }}\n\
             }}\n",
            kmdf_version::KMDF_MAJOR,
            kmdf_version::KMDF_MINOR,
            version,
        ),
    )
    .expect("write kmdf_symbol.rs");

    // ---- bindgen -----------------------------------------------------------------------------------------
    let builder = bindgen::Builder::default()
        .header("wrapper.h")
        .clang_arg("--target=x86_64-pc-windows-msvc")
        // The WDK headers are MSVC-flavoured C: anonymous tags, `__declspec`, `#pragma`, MS enums.
        .clang_arg("-fms-extensions")
        .clang_arg("-fms-compatibility")
        .clang_arg("-Wno-everything")
        // The defines `driver/build.sh` passes, minus the include-path ones.
        .clang_arg("-D_WIN64")
        .clang_arg("-D_AMD64_")
        .clang_arg("-DAMD64")
        .clang_arg("-D_KERNEL_MODE")
        .clang_arg("-DSTD_CALL")
        .clang_arg("-DDEPRECATE_DDK_FUNCTIONS=1")
        .clang_arg("-DPOOL_NX_OPTIN=1")
        .clang_arg("-DNTDDI_VERSION=0x0A000000")
        .clang_arg("-D_WIN32_WINNT=0x0A00")
        .clang_arg("-DWINVER=0x0A00")
        .clang_arg(&format!("-DKMDF_VERSION_MAJOR={}", kmdf_version::KMDF_MAJOR))
        .clang_arg(&format!("-DKMDF_VERSION_MINOR={}", kmdf_version::KMDF_MINOR))
        .clang_arg(format!("-isystem{}", km_inc.display()))
        .clang_arg(format!("-isystem{}", inc_root.join("shared").display()))
        .clang_arg(format!("-isystem{}", inc_root.join("km/crt").display()))
        .clang_arg(format!("-isystem{}", kmdf_inc.display()))
        // Only what the driver touches. Everything else in ntddk.h (PCI, ACPI, USB, storage) is parsed but
        // not emitted — which matters, because a full dump is ~680 KB of Rust.
        .allowlist_type("_?WDF.*")
        .allowlist_type("PFN_WDF.*")
        .allowlist_type("EVT_WDF.*")
        .allowlist_type("PEVT_WDF.*")
        .allowlist_type("WDF.*")
        .allowlist_type("VHF.*")
        .allowlist_type("_?VHF.*")
        .allowlist_type("_?HID_XFER_PACKET")
        .allowlist_type("_WDFFUNCENUM")
        .allowlist_type("_WDFFUNC")
        .allowlist_type("WDFFUNC")
        .allowlist_type("_WDF_DRIVER_GLOBALS")
        .allowlist_type("PWDF_DRIVER_GLOBALS")
        .allowlist_type("NTSTATUS")
        .allowlist_type("UNICODE_STRING")
        .allowlist_type("_UNICODE_STRING")
        .allowlist_type("GUID")
        .allowlist_type("_GUID")
        .allowlist_type("_DEVICE_OBJECT")
        .allowlist_type("_DRIVER_OBJECT")
        .allowlist_type("_EVENT_TYPE")
        .allowlist_var("WdfFunctions.*")
        .allowlist_var("WdfDriverGlobals")
        .allowlist_var("WdfStructureCount")
        .allowlist_var("_WDFFUNCENUM.*")
        // The enumerators. bindgen emits them as constants, and a constant is a *var* for allowlisting, so
        // without this the driver cannot name `WdfIoQueueDispatchManual` or `WdfExecutionLevelPassive` at all.
        .allowlist_var("Wdf[A-Z].*")
        .allowlist_function("Vhf.*")
        .allowlist_function("Rtl.*")
        .allowlist_function("Ke.*")
        .use_core()
        .derive_default(false)
        .impl_debug(false)
        .layout_tests(true);

    // The generated names are the C ones; renaming them would make the bindings harder to check against the
    // headers, which is the whole point of generating them. The lint allowances live on the `wdk` module in
    // src/ffi.rs, not in the generated file — an inner attribute inside an `include!`d file is not permitted
    // where bindgen places it.

    let bindings = builder.generate().expect("bindgen failed on the WDK headers");
    bindings.write_to_file(out_dir.join("bindings.rs")).expect("write bindings.rs");

    // ---- the kernel link ---------------------------------------------------------------------------------
    //
    // `cargo::rustc-cdylib-link-arg` lands on the linker command line for a cdylib — the same mechanism
    // windows-drivers-rs's `wdk-build` uses, so this is the intended shape for a Rust driver, just emitted
    // from here because `wdk-build` will not run off Windows.
    let arg = |s: String| println!("cargo::rustc-cdylib-link-arg={s}");

    // The framework. WdfDriverEntry.lib supplies FxDriverEntry, which KMDF's loader calls to get to the
    // driver's own DriverEntry; wdfldr.lib resolves the WdfFunctions/WdfDriverGlobals imports from WDFLDR.SYS.
    arg(wdfentry.display().to_string());
    arg(lib_in(&kmdf_lib, "wdfldr.lib").display().to_string());
    arg(ntoskrnl.display().to_string());
    arg(lib_in(&km_lib, "hal.lib").display().to_string());
    arg(lib_in(&km_lib, "wmilib.lib").display().to_string());
    arg(lib_in(&km_lib, "bufferoverflowfastfailk.lib").display().to_string());
    arg(vhfkm.display().to_string());

    // The flags a KMDF driver needs, matching driver/build.sh's lld-link line.
    for f in [
        "/DRIVER",
        "/SUBSYSTEM:NATIVE,10.00",
        "/ENTRY:FxDriverEntry",
        // A kernel driver must not pull in the user-mode CRT: libcmt brings WdfDriverEntry's stub a user-mode
        // heap and a user-mode SEH. src/lib.rs supplies memcpy/memset/memmove/memcmp itself for the same
        // reason — LLVM emits calls to those names for bulk copies.
        "/NODEFAULTLIB",
        "/OSVERSION:10.0",
        "/VERSION:10.0",
        "/RELEASE",
        "/OPT:REF",
        "/OPT:ICF",
        "/INCREMENTAL:NO",
        "/DEBUG",
        // INIT is the discardable section DriverEntry runs from; /MERGE folds the pageable section into
        // PAGE so `#[link_section = "PAGE"]` lands where the WDK expects pageable code.
        "/MERGE:_TEXT=.text",
        "/MERGE:_PAGE=PAGE",
        "/SECTION:INIT,d",
        // LNK4210 (no .CRT section) and LNK4216/LNK4257 are Rust's, not defects here: rustc emits no CRT
        // initialisers, and it has no way to name an entry point without exporting it.
        "/IGNORE:4210,4216,4257",
    ] {
        arg(f.to_string());
    }
}
