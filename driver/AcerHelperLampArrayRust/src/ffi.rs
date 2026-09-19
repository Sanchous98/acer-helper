//! The WDF, VHF, HID and NT surface this driver uses — and, just as deliberately, the part it does not.
//!
//! # Why bindgen and not `wdk-sys`
//!
//! `windows-drivers-rs`'s `wdk-sys` would have been the intended source of these declarations. It cannot be
//! used here: its build script calls `wdk-build`, and `wdk-build` contains
//!
//! ```text
//! compile_error!(
//!     "windows-drivers-rs is designed to be run on a Windows host machine in a WDK environment. \
//!      Please build using a Windows target."
//! );
//! ```
//!
//! in `utils.rs`, on the non-Windows side of a `#[cfg(target_os = "windows")]` fork, so the crate does not
//! compile on the Linux build host at all — no amount of environment setup reaches it. `cargo-wdk` depends on
//! the same crate and stops at the same line.
//!
//! What is kept is the *technique*: bindgen, run from `build.rs` against the same NuGet-unpacked WDK headers
//! the C driver compiles against, emitting a declaration set that carries `size_of`/`offset_of` assertions for
//! every record. The layouts below are therefore checked by the compiler on every build rather than
//! transcribed — which matters more here than usual, because a driver cannot be loaded or executed on this
//! machine to find out whether they were right.
//!
//! # The function table
//!
//! KMDF does not import `WdfDriverCreate`, `WdfDeviceCreate` and so on as ordinary symbols. A KMDF driver
//! reaches every `Wdf*` API through one imported table of function pointers — `WdfFunctions_01033` for
//! framework 1.33 — indexed by the `WdfFunctionTableIndex` values in `wdffuncenum.h`:
//!
//! ```c
//! #define WdfFunctions WdfFunctions_01033
//! #define WdfDriverCreate(...) \
//!     ((PFN_WDFDRIVERCREATE) WdfFunctions[WdfDriverCreateTableIndex])(WdfDriverGlobals, ...)
//! ```
//!
//! Both symbols are resolved out of `WDFLDR.SYS` through `wdfldr.lib`. The table's *name carries the
//! framework version*, and `build.rs` spells that name from the single pin in `src/kmdf_version.rs` — so a
//! build that resolved a different framework than the INF declares fails to link, instead of quietly binding
//! entry points from a newer KMDF than the target OS has.
//!
//! The slot type is `WDFFUNC` — an untyped function pointer — so every call casts the slot to the exact
//! signature the WDK's own inline wrapper declares for it. Those signatures are reproduced below, parameter
//! for parameter from the `PFN_WDF*` typedefs in the KMDF headers (each one takes `PWDF_DRIVER_GLOBALS` first,
//! which is what the `WdfDriverGlobals` field of the WDK's macros expands to).

#![allow(non_snake_case, non_camel_case_types, non_upper_case_globals, dead_code)]
// bindgen's bitfield accessors transmute to a width they could reach with `as`. It is generated code from a
// header, checked against the header by the layout assertions beside it, and not something to hand-edit.
#![allow(unnecessary_transmutes)]

use core::ffi::{c_void, c_long};

/// The declarations bindgen generated from `wrapper.h` at build time.
pub mod wdk {
    include!(concat!(env!("OUT_DIR"), "/bindings.rs"));
}

pub use wdk::*;

/// The framework-version-dependent pieces, written by `build.rs` from `src/kmdf_version.rs`. Nothing in this
/// crate's source names a framework version; these do.
pub mod kmdf {
    include!(concat!(env!("OUT_DIR"), "/kmdf_symbol.rs"));
}

// ---------------------------------------------------------------------------------------------------------
// The function table
// ---------------------------------------------------------------------------------------------------------

/// Read slot `index` of the KMDF function table as the function pointer it holds.
///
/// # Safety
///
/// `T` must be the exact `PFN_WDF*` type the WDK declares for that slot. The table is untyped by construction
/// (`WDFFUNC` is `Option<unsafe extern "C" fn()>`), so this cast is the one place where a wrong signature
/// could be written and the compiler could not see it; every call site below states which typedef it is
/// reproducing, and the slot index beside it comes from the same header that declares it.
#[inline(always)]
pub unsafe fn table_fn<T: Copy>(index: i32) -> T {
    let table = kmdf::wdf_functions();
    // The table is an array of WDFFUNC; read the slot out without assuming it is non-null (WDFLDR fills it
    // in before DriverEntry runs, but a slot the framework version does not have reads as null, which is a
    // call through a null pointer rather than a silent success).
    let slot: WDFFUNC = unsafe { core::ptr::read(table.offset(index as isize)) };
    unsafe { core::mem::transmute_copy::<WDFFUNC, T>(&slot) }
}

/// Fetch a KMDF entry point. `$fnty` is written as the exact signature (see the typedefs below); `$idx` is
/// the `WdfFunctionTableIndex` constant bindgen generated from `wdffuncenum.h`.
macro_rules! wdf {
    ($fnty:ty, $idx:expr) => {{
        unsafe { $crate::ffi::table_fn::<$fnty>($idx) }
    }};
}

/// The `WdfDriverGlobals` the WDK's own macros pass as every `Wdf*` call's first argument. It is imported
/// from WDFLDR, which fills it in before `DriverEntry` runs.
#[inline(always)]
pub fn driver_globals() -> PWDF_DRIVER_GLOBALS {
    unsafe { WdfDriverGlobals }
}

// ---------------------------------------------------------------------------------------------------------
// Table slot indices, aliased out of the generated `_WDFFUNCENUM` constants
// ---------------------------------------------------------------------------------------------------------

/// The `WdfFunctionTableIndex` values the calls below use. They come from `wdffuncenum.h` through bindgen, so
/// they belong to the framework version the headers declare; a slot that does not exist in a given version is
/// a value the header would not have defined, i.e. a compile error rather than a runtime mis-dispatch.
pub mod idx {
    use super::wdk::*;

    pub const DEVICE_CREATE: i32 = _WDFFUNCENUM_WdfDeviceCreateTableIndex;
    pub const DEVICE_CREATE_SYMBOLIC_LINK: i32 = _WDFFUNCENUM_WdfDeviceCreateSymbolicLinkTableIndex;
    pub const DEVICE_INIT_SET_FILEOBJECT_CONFIG: i32 =
        _WDFFUNCENUM_WdfDeviceInitSetFileObjectConfigTableIndex;
    pub const DEVICE_INIT_SET_IO_TYPE: i32 = _WDFFUNCENUM_WdfDeviceInitSetIoTypeTableIndex;
    pub const DEVICE_WDM_GET_DEVICE_OBJECT: i32 = _WDFFUNCENUM_WdfDeviceWdmGetDeviceObjectTableIndex;
    pub const DRIVER_CREATE: i32 = _WDFFUNCENUM_WdfDriverCreateTableIndex;
    pub const FILEOBJECT_GET_DEVICE: i32 = _WDFFUNCENUM_WdfFileObjectGetDeviceTableIndex;
    pub const IO_QUEUE_CREATE: i32 = _WDFFUNCENUM_WdfIoQueueCreateTableIndex;
    pub const IO_QUEUE_GET_DEVICE: i32 = _WDFFUNCENUM_WdfIoQueueGetDeviceTableIndex;
    pub const IO_QUEUE_RETRIEVE_NEXT_REQUEST: i32 =
        _WDFFUNCENUM_WdfIoQueueRetrieveNextRequestTableIndex;
    pub const IO_QUEUE_START: i32 = _WDFFUNCENUM_WdfIoQueueStartTableIndex;
    pub const REQUEST_COMPLETE: i32 = _WDFFUNCENUM_WdfRequestCompleteTableIndex;
    pub const REQUEST_COMPLETE_WITH_INFORMATION: i32 =
        _WDFFUNCENUM_WdfRequestCompleteWithInformationTableIndex;
    pub const REQUEST_FORWARD_TO_IO_QUEUE: i32 = _WDFFUNCENUM_WdfRequestForwardToIoQueueTableIndex;
    pub const REQUEST_RETRIEVE_INPUT_BUFFER: i32 =
        _WDFFUNCENUM_WdfRequestRetrieveInputBufferTableIndex;
    pub const REQUEST_RETRIEVE_OUTPUT_BUFFER: i32 =
        _WDFFUNCENUM_WdfRequestRetrieveOutputBufferTableIndex;
    pub const SPINLOCK_ACQUIRE: i32 = _WDFFUNCENUM_WdfSpinLockAcquireTableIndex;
    pub const SPINLOCK_CREATE: i32 = _WDFFUNCENUM_WdfSpinLockCreateTableIndex;
    pub const SPINLOCK_RELEASE: i32 = _WDFFUNCENUM_WdfSpinLockReleaseTableIndex;
    pub const WAITLOCK_ACQUIRE: i32 = _WDFFUNCENUM_WdfWaitLockAcquireTableIndex;
    pub const WAITLOCK_CREATE: i32 = _WDFFUNCENUM_WdfWaitLockCreateTableIndex;
    pub const WAITLOCK_RELEASE: i32 = _WDFFUNCENUM_WdfWaitLockReleaseTableIndex;
}

/// The enumerated values this driver names.
///
/// bindgen emits a C enumerator as `<enum typedef>_<enumerator>`, so the WDK's `WdfExecutionLevelPassive`
/// arrives as `_WDF_EXECUTION_LEVEL_WdfExecutionLevelPassive`. These aliases keep the driver's own code
/// reading like the WDK documentation; the mapping is one line per value and the compiler checks it.
pub mod val {
    pub use super::wdk::_WDF_DEVICE_IO_TYPE_WdfDeviceIoBuffered as DEVICE_IO_BUFFERED;
    pub use super::wdk::_WDF_EXECUTION_LEVEL_WdfExecutionLevelDispatch as EXECUTION_DISPATCH;
    pub use super::wdk::_WDF_EXECUTION_LEVEL_WdfExecutionLevelPassive as EXECUTION_PASSIVE;
    pub use super::wdk::_WDF_IO_QUEUE_DISPATCH_TYPE_WdfIoQueueDispatchManual as QUEUE_MANUAL;
    pub use super::wdk::_WDF_IO_QUEUE_DISPATCH_TYPE_WdfIoQueueDispatchParallel as QUEUE_PARALLEL;
}

// ---------------------------------------------------------------------------------------------------------
// The PFN_ typedefs the calls below cast table slots to
// ---------------------------------------------------------------------------------------------------------

/// `typedef NTSTATUS (NTAPI *PFN_WDFDRIVERCREATE)(PWDF_DRIVER_GLOBALS, PDRIVER_OBJECT,
/// PCUNICODE_STRING, PWDF_OBJECT_ATTRIBUTES, PWDF_DRIVER_CONFIG, WDFDRIVER*);`
pub type PfnWdfDriverCreate = unsafe extern "C" fn(
    PWDF_DRIVER_GLOBALS,
    PDRIVER_OBJECT,
    PCUNICODE_STRING,
    PWDF_OBJECT_ATTRIBUTES,
    PWDF_DRIVER_CONFIG,
    *mut WDFDRIVER,
) -> NTSTATUS;

/// `PFN_WDFDEVICEINITSETFILEOBJECTCONFIG(PWDF_DRIVER_GLOBALS, PWDFDEVICE_INIT, PWDF_FILEOBJECT_CONFIG,
/// PWDF_OBJECT_ATTRIBUTES);`
pub type PfnWdfDeviceInitSetFileObjectConfig = unsafe extern "C" fn(
    PWDF_DRIVER_GLOBALS,
    PWDFDEVICE_INIT,
    PWDF_FILEOBJECT_CONFIG,
    PWDF_OBJECT_ATTRIBUTES,
);

/// `PFN_WDFDEVICEINITSETIOTYPE(PWDF_DRIVER_GLOBALS, PWDFDEVICE_INIT, WDF_DEVICE_IO_TYPE);`
pub type PfnWdfDeviceInitSetIoType =
    unsafe extern "C" fn(PWDF_DRIVER_GLOBALS, PWDFDEVICE_INIT, WDF_DEVICE_IO_TYPE);

/// `PFN_WDFDEVICECREATE(PWDF_DRIVER_GLOBALS, PWDFDEVICE_INIT*, PWDF_OBJECT_ATTRIBUTES, WDFDEVICE*);`
pub type PfnWdfDeviceCreate = unsafe extern "C" fn(
    PWDF_DRIVER_GLOBALS,
    *mut PWDFDEVICE_INIT,
    PWDF_OBJECT_ATTRIBUTES,
    *mut WDFDEVICE,
) -> NTSTATUS;

/// `PFN_WDFDEVICECREATESYMBOLICLINK(PWDF_DRIVER_GLOBALS, WDFDEVICE, PCUNICODE_STRING);`
pub type PfnWdfDeviceCreateSymbolicLink =
    unsafe extern "C" fn(PWDF_DRIVER_GLOBALS, WDFDEVICE, PCUNICODE_STRING) -> NTSTATUS;

/// `PFN_WDFDEVICEWDMGETDEVICEOBJECT(PWDF_DRIVER_GLOBALS, WDFDEVICE);`
pub type PfnWdfDeviceWdmGetDeviceObject =
    unsafe extern "C" fn(PWDF_DRIVER_GLOBALS, WDFDEVICE) -> PDEVICE_OBJECT;

/// `PFN_WDFIOQUEUECREATE(PWDF_DRIVER_GLOBALS, WDFDEVICE, PWDF_IO_QUEUE_CONFIG, PWDF_OBJECT_ATTRIBUTES,
/// WDFQUEUE*);`
pub type PfnWdfIoQueueCreate = unsafe extern "C" fn(
    PWDF_DRIVER_GLOBALS,
    WDFDEVICE,
    PWDF_IO_QUEUE_CONFIG,
    PWDF_OBJECT_ATTRIBUTES,
    *mut WDFQUEUE,
) -> NTSTATUS;

/// `PFN_WDFIOQUEUEGETDEVICE(PWDF_DRIVER_GLOBALS, WDFQUEUE);`
pub type PfnWdfIoQueueGetDevice = unsafe extern "C" fn(PWDF_DRIVER_GLOBALS, WDFQUEUE) -> WDFDEVICE;

/// `PFN_WDFIOQUEUERETRIEVENEXTREQUEST(PWDF_DRIVER_GLOBALS, WDFQUEUE, WDFREQUEST*);`
pub type PfnWdfIoQueueRetrieveNextRequest =
    unsafe extern "C" fn(PWDF_DRIVER_GLOBALS, WDFQUEUE, *mut WDFREQUEST) -> NTSTATUS;

/// `PFN_WDFIOQUEUESTART(PWDF_DRIVER_GLOBALS, WDFQUEUE);`
pub type PfnWdfIoQueueStart = unsafe extern "C" fn(PWDF_DRIVER_GLOBALS, WDFQUEUE);

/// `PFN_WDFFILEOBJECTGETDEVICE(PWDF_DRIVER_GLOBALS, WDFFILEOBJECT);`
pub type PfnWdfFileObjectGetDevice =
    unsafe extern "C" fn(PWDF_DRIVER_GLOBALS, WDFFILEOBJECT) -> WDFDEVICE;

/// `PFN_WDFREQUESTCOMPLETE(PWDF_DRIVER_GLOBALS, WDFREQUEST, NTSTATUS);`
pub type PfnWdfRequestComplete = unsafe extern "C" fn(PWDF_DRIVER_GLOBALS, WDFREQUEST, NTSTATUS);

/// `PFN_WDFREQUESTCOMPLETEWITHINFORMATION(PWDF_DRIVER_GLOBALS, WDFREQUEST, NTSTATUS, ULONG_PTR);`
pub type PfnWdfRequestCompleteWithInformation =
    unsafe extern "C" fn(PWDF_DRIVER_GLOBALS, WDFREQUEST, NTSTATUS, usize);

/// `PFN_WDFREQUESTFORWARDTOIOQUEUE(PWDF_DRIVER_GLOBALS, WDFREQUEST, WDFQUEUE);`
pub type PfnWdfRequestForwardToIoQueue =
    unsafe extern "C" fn(PWDF_DRIVER_GLOBALS, WDFREQUEST, WDFQUEUE) -> NTSTATUS;

/// `PFN_WDFREQUESTRETRIEVEINPUTBUFFER(PWDF_DRIVER_GLOBALS, WDFREQUEST, size_t, PVOID*, size_t*);`
pub type PfnWdfRequestRetrieveInputBuffer = unsafe extern "C" fn(
    PWDF_DRIVER_GLOBALS,
    WDFREQUEST,
    usize,
    *mut PVOID,
    *mut usize,
) -> NTSTATUS;

/// `PFN_WDFREQUESTRETRIEVEOUTPUTBUFFER(PWDF_DRIVER_GLOBALS, WDFREQUEST, size_t, PVOID*, size_t*);`
pub type PfnWdfRequestRetrieveOutputBuffer = unsafe extern "C" fn(
    PWDF_DRIVER_GLOBALS,
    WDFREQUEST,
    usize,
    *mut PVOID,
    *mut usize,
) -> NTSTATUS;

/// `PFN_WDFSPINLOCKCREATE(PWDF_DRIVER_GLOBALS, PWDF_OBJECT_ATTRIBUTES, WDFSPINLOCK*);`
pub type PfnWdfSpinLockCreate =
    unsafe extern "C" fn(PWDF_DRIVER_GLOBALS, PWDF_OBJECT_ATTRIBUTES, *mut WDFSPINLOCK) -> NTSTATUS;

/// `PFN_WDFSPINLOCKACQUIRE(PWDF_DRIVER_GLOBALS, WDFSPINLOCK);`
pub type PfnWdfSpinLockAcquire = unsafe extern "C" fn(PWDF_DRIVER_GLOBALS, WDFSPINLOCK);

/// `PFN_WDFSPINLOCKRELEASE(PWDF_DRIVER_GLOBALS, WDFSPINLOCK);`
pub type PfnWdfSpinLockRelease = unsafe extern "C" fn(PWDF_DRIVER_GLOBALS, WDFSPINLOCK);

/// `PFN_WDFWAITLOCKCREATE(PWDF_DRIVER_GLOBALS, PWDF_OBJECT_ATTRIBUTES, WDFWAITLOCK*);`
pub type PfnWdfWaitLockCreate =
    unsafe extern "C" fn(PWDF_DRIVER_GLOBALS, PWDF_OBJECT_ATTRIBUTES, *mut WDFWAITLOCK) -> NTSTATUS;

/// `PFN_WDFWAITLOCKACQUIRE(PWDF_DRIVER_GLOBALS, WDFWAITLOCK, PLONGLONG);`
pub type PfnWdfWaitLockAcquire =
    unsafe extern "C" fn(PWDF_DRIVER_GLOBALS, WDFWAITLOCK, *mut i64) -> NTSTATUS;

/// `PFN_WDFWAITLOCKRELEASE(PWDF_DRIVER_GLOBALS, WDFWAITLOCK);`
pub type PfnWdfWaitLockRelease = unsafe extern "C" fn(PWDF_DRIVER_GLOBALS, WDFWAITLOCK);

// ---------------------------------------------------------------------------------------------------------
// Thin Rust spelling of the WDF macros
// ---------------------------------------------------------------------------------------------------------

#[inline(always)]
pub fn wdf_driver_create(
    driver_object: PDRIVER_OBJECT,
    registry_path: PCUNICODE_STRING,
    driver_attributes: PWDF_OBJECT_ATTRIBUTES,
    driver_config: PWDF_DRIVER_CONFIG,
    driver: *mut WDFDRIVER,
) -> NTSTATUS {
    let f = wdf!(PfnWdfDriverCreate, idx::DRIVER_CREATE);
    unsafe { f(driver_globals(), driver_object, registry_path, driver_attributes, driver_config, driver) }
}

#[inline(always)]
pub fn wdf_device_init_set_file_object_config(
    device_init: PWDFDEVICE_INIT,
    config: PWDF_FILEOBJECT_CONFIG,
    attributes: PWDF_OBJECT_ATTRIBUTES,
) {
    let f = wdf!(PfnWdfDeviceInitSetFileObjectConfig, idx::DEVICE_INIT_SET_FILEOBJECT_CONFIG);
    unsafe { f(driver_globals(), device_init, config, attributes) }
}

#[inline(always)]
pub fn wdf_device_init_set_io_type(device_init: PWDFDEVICE_INIT, io_type: WDF_DEVICE_IO_TYPE) {
    let f = wdf!(PfnWdfDeviceInitSetIoType, idx::DEVICE_INIT_SET_IO_TYPE);
    unsafe { f(driver_globals(), device_init, io_type) }
}

#[inline(always)]
pub fn wdf_device_create(
    device_init: *mut PWDFDEVICE_INIT,
    attributes: PWDF_OBJECT_ATTRIBUTES,
    device: *mut WDFDEVICE,
) -> NTSTATUS {
    let f = wdf!(PfnWdfDeviceCreate, idx::DEVICE_CREATE);
    unsafe { f(driver_globals(), device_init, attributes, device) }
}

#[inline(always)]
pub fn wdf_device_create_symbolic_link(device: WDFDEVICE, name: PCUNICODE_STRING) -> NTSTATUS {
    let f = wdf!(PfnWdfDeviceCreateSymbolicLink, idx::DEVICE_CREATE_SYMBOLIC_LINK);
    unsafe { f(driver_globals(), device, name) }
}

#[inline(always)]
pub fn wdf_device_wdm_get_device_object(device: WDFDEVICE) -> PDEVICE_OBJECT {
    let f = wdf!(PfnWdfDeviceWdmGetDeviceObject, idx::DEVICE_WDM_GET_DEVICE_OBJECT);
    unsafe { f(driver_globals(), device) }
}

#[inline(always)]
pub fn wdf_io_queue_create(
    device: WDFDEVICE,
    config: PWDF_IO_QUEUE_CONFIG,
    attributes: PWDF_OBJECT_ATTRIBUTES,
    queue: *mut WDFQUEUE,
) -> NTSTATUS {
    let f = wdf!(PfnWdfIoQueueCreate, idx::IO_QUEUE_CREATE);
    unsafe { f(driver_globals(), device, config, attributes, queue) }
}

#[inline(always)]
pub fn wdf_io_queue_get_device(queue: WDFQUEUE) -> WDFDEVICE {
    let f = wdf!(PfnWdfIoQueueGetDevice, idx::IO_QUEUE_GET_DEVICE);
    unsafe { f(driver_globals(), queue) }
}

#[inline(always)]
pub fn wdf_io_queue_retrieve_next_request(queue: WDFQUEUE) -> Option<WDFREQUEST> {
    let f = wdf!(PfnWdfIoQueueRetrieveNextRequest, idx::IO_QUEUE_RETRIEVE_NEXT_REQUEST);
    let mut request: WDFREQUEST = core::ptr::null_mut();
    let status = unsafe { f(driver_globals(), queue, &mut request) };
    if nt_success(status) {
        Some(request)
    } else {
        None
    }
}

#[inline(always)]
pub fn wdf_io_queue_start(queue: WDFQUEUE) {
    let f = wdf!(PfnWdfIoQueueStart, idx::IO_QUEUE_START);
    unsafe { f(driver_globals(), queue) }
}

#[inline(always)]
pub fn wdf_file_object_get_device(file_object: WDFFILEOBJECT) -> WDFDEVICE {
    let f = wdf!(PfnWdfFileObjectGetDevice, idx::FILEOBJECT_GET_DEVICE);
    unsafe { f(driver_globals(), file_object) }
}

#[inline(always)]
pub fn wdf_request_complete(request: WDFREQUEST, status: NTSTATUS) {
    let f = wdf!(PfnWdfRequestComplete, idx::REQUEST_COMPLETE);
    unsafe { f(driver_globals(), request, status) }
}

#[inline(always)]
pub fn wdf_request_complete_with_information(request: WDFREQUEST, status: NTSTATUS, information: usize) {
    let f = wdf!(PfnWdfRequestCompleteWithInformation, idx::REQUEST_COMPLETE_WITH_INFORMATION);
    unsafe { f(driver_globals(), request, status, information) }
}

#[inline(always)]
pub fn wdf_request_forward_to_io_queue(request: WDFREQUEST, queue: WDFQUEUE) -> NTSTATUS {
    let f = wdf!(PfnWdfRequestForwardToIoQueue, idx::REQUEST_FORWARD_TO_IO_QUEUE);
    unsafe { f(driver_globals(), request, queue) }
}

#[inline(always)]
pub fn wdf_request_retrieve_input_buffer(
    request: WDFREQUEST,
    minimum: usize,
) -> Option<(*mut c_void, usize)> {
    let f = wdf!(PfnWdfRequestRetrieveInputBuffer, idx::REQUEST_RETRIEVE_INPUT_BUFFER);
    let mut buffer: PVOID = core::ptr::null_mut();
    let mut length: usize = 0;
    let status = unsafe { f(driver_globals(), request, minimum, &mut buffer, &mut length) };
    if nt_success(status) {
        Some((buffer, length))
    } else {
        None
    }
}

#[inline(always)]
pub fn wdf_request_retrieve_output_buffer(
    request: WDFREQUEST,
    minimum: usize,
) -> Option<(*mut c_void, usize)> {
    let f = wdf!(PfnWdfRequestRetrieveOutputBuffer, idx::REQUEST_RETRIEVE_OUTPUT_BUFFER);
    let mut buffer: PVOID = core::ptr::null_mut();
    let mut length: usize = 0;
    let status = unsafe { f(driver_globals(), request, minimum, &mut buffer, &mut length) };
    if nt_success(status) {
        Some((buffer, length))
    } else {
        None
    }
}

#[inline(always)]
pub fn wdf_spin_lock_create(attributes: PWDF_OBJECT_ATTRIBUTES, lock: *mut WDFSPINLOCK) -> NTSTATUS {
    let f = wdf!(PfnWdfSpinLockCreate, idx::SPINLOCK_CREATE);
    unsafe { f(driver_globals(), attributes, lock) }
}

#[inline(always)]
pub fn wdf_wait_lock_create(attributes: PWDF_OBJECT_ATTRIBUTES, lock: *mut WDFWAITLOCK) -> NTSTATUS {
    let f = wdf!(PfnWdfWaitLockCreate, idx::WAITLOCK_CREATE);
    unsafe { f(driver_globals(), attributes, lock) }
}

#[inline(always)]
pub fn wdf_spin_lock_acquire(lock: WDFSPINLOCK) {
    let f = wdf!(PfnWdfSpinLockAcquire, idx::SPINLOCK_ACQUIRE);
    unsafe { f(driver_globals(), lock) }
}

#[inline(always)]
pub fn wdf_spin_lock_release(lock: WDFSPINLOCK) {
    let f = wdf!(PfnWdfSpinLockRelease, idx::SPINLOCK_RELEASE);
    unsafe { f(driver_globals(), lock) }
}

#[inline(always)]
pub fn wdf_wait_lock_acquire(lock: WDFWAITLOCK) {
    let f = wdf!(PfnWdfWaitLockAcquire, idx::WAITLOCK_ACQUIRE);
    unsafe { f(driver_globals(), lock, core::ptr::null_mut()) };
}

#[inline(always)]
pub fn wdf_wait_lock_release(lock: WDFWAITLOCK) {
    let f = wdf!(PfnWdfWaitLockRelease, idx::WAITLOCK_RELEASE);
    unsafe { f(driver_globals(), lock) }
}

// ---------------------------------------------------------------------------------------------------------
// Small NT helpers
// ---------------------------------------------------------------------------------------------------------

/// `NT_SUCCESS`, from `ntdef.h`: any non-negative status. Every status the WDK returns is an `NTSTATUS`
/// whose severity lives in the top two bits, so this is a sign test, not a comparison against `STATUS_SUCCESS`.
#[inline(always)]
pub const fn nt_success(status: NTSTATUS) -> bool {
    status >= 0
}

pub const STATUS_SUCCESS: NTSTATUS = 0x0000_0000u32 as c_long;
pub const STATUS_INVALID_PARAMETER: NTSTATUS = 0xC000_000Du32 as c_long;
pub const STATUS_INVALID_DEVICE_REQUEST: NTSTATUS = 0xC000_0010u32 as c_long;
pub const STATUS_BUFFER_TOO_SMALL: NTSTATUS = 0xC000_0023u32 as c_long;
pub const STATUS_DEVICE_NOT_READY: NTSTATUS = 0xC000_00AAu32 as c_long;
pub const STATUS_CANCELLED: NTSTATUS = 0xC000_0120u32 as c_long;
pub const STATUS_INSUFFICIENT_RESOURCES: NTSTATUS = 0xC000_009Au32 as c_long;

/// `RtlZeroMemory` and `RtlCopyMemory` are compiler intrinsics in the WDK's headers (they reach `memset`
/// and `memcpy` builtins), not imports. In Rust the equivalents are these — and they are the reason the
/// driver does not need the CRT: `core::ptr::copy_nonoverlapping` over an unknown length lowers to a
/// `memcpy` *call*, which `/NODEFAULTLIB` leaves unresolved. See the `mem*` definitions in `lib.rs`.
#[inline(always)]
pub unsafe fn rtl_zero_memory<T>(destination: *mut T) {
    unsafe { core::ptr::write_bytes(destination as *mut u8, 0, core::mem::size_of::<T>()) };
}

#[inline(always)]
pub unsafe fn rtl_copy_memory<T>(destination: *mut T, source: *const T) {
    unsafe {
        core::ptr::copy_nonoverlapping(source as *const u8, destination as *mut u8, core::mem::size_of::<T>())
    };
}

// ---------------------------------------------------------------------------------------------------------
// What is deliberately NOT here
// ---------------------------------------------------------------------------------------------------------
//
// `WdfIoQueuePurgeSynchronously` and `WdfIoQueuePurge` have no slot index in `idx`, no `Pfn*` typedef and no
// wrapper above. This is not an omission to be filled in later: the C driver calls
// `WdfIoQueuePurgeSynchronously` from inside an IO callback, which KMDF forbids (a queue's callback must not
// purge that queue synchronously — it deadlocks against the very callback it is running on) and which the
// kernel's own verifier flags. Rather than remember not to do it, this driver has no way to express it: the
// table slot is never read, so the entry point cannot be called. The behaviour the purge provided — unblocking
// a parked `WAIT_FRAME` when the app stops listening — is delivered instead by `FrameQueue::cancel_parked`,
// which retrieves the request and completes it with STATUS_CANCELLED.
