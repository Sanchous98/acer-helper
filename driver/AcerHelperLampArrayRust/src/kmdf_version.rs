// The KMDF framework version, pinned exactly once for the whole package.
//
// This file is the only place in the Rust driver where the version appears. Three consumers read it, and they
// are the three places a mismatch could otherwise hide:
//
//   * `build.rs` — `include!`s it, and uses it to define KMDF_VERSION_MAJOR/MINOR for the header parse, to pick
//     the `x64/<major>.<minor>` directory the link searches, and to name the function-table symbol
//     (`WdfFunctions_01033` for 1.33, spelled by the same format string the WDF headers use).
//   * `driver/build-rust-driver.sh` — reads it with `sed` and stamps it into the INF as
//     `KmdfLibraryVersion`, which is the value PnP compares against the framework actually installed.
//   * `src/lib.rs` — re-exports it as `WdfMinimumVersionRequired`, the symbol KMDF's own entry stub reads to
//     decide whether the driver was built against a framework it can satisfy. In the C build that symbol comes
//     out of `wdffuncenum.h` as `__declspec(selectany) ULONG WdfMinimumVersionRequired = KMDF_VERSION_MINOR;`
//     — i.e. from a `-D`, from a *different* place than the INF's token, which is the whole hazard.
//
// So "the framework the driver links against", "the framework the INF declares" and "the framework the driver
// reports it needs" are one number read three times, not three numbers that can drift. 1.33 is what Windows 11
// 22H2 (build 22621, the floor `AcerHelperLampArray.inf` declares) ships in-box; raising it here without
// raising that floor in the INF produces a driver the target OS cannot start.
pub const KMDF_MAJOR: u32 = 1;
pub const KMDF_MINOR: u32 = 33;
