//! AcerHelperLampArray, in Rust — a KMDF HID *source* driver publishing a virtual HID LampArray device
//! through the in-box Virtual HID Framework (`vhf.sys`, declared as a lower filter in the INF).
//!
//! This is the same driver as `driver/AcerHelperLampArray/driver.c`, written against the same wire contract
//! (`src/contract.rs`, the Rust half of `public.h` and `lamparray.h`) and built into the same kind of package
//! (`AcerHelperLampArray.sys` + INF). What differs is the language and the four things the Rust type system
//! is asked to hold that C could not — each of them a defect found in the C driver's review:
//!
//! 1. **No synchronous purge, ever.** `WdfIoQueuePurgeSynchronously` is not declared in `src/ffi.rs`, so it
//!    cannot be called. The C driver calls it from an IO callback, which KMDF forbids and the verifier
//!    enforces; here the parked `WAIT_FRAME` is ended by `cancel_parked`, which retrieves the request and
//!    completes it with `STATUS_CANCELLED` — the same thing the app observes, without the forbidden call.
//!
//! 2. **A queue touched at DISPATCH_LEVEL is a DISPATCH-level queue.** `ctx.frame_queue` is a
//!    `Queue<Dispatch>`, and `retrieve_next` exists only on that type. The C driver's `FrameQueue` inherited
//!    `WdfExecutionLevelPassive` from the device (it was created with `WDF_NO_OBJECT_ATTRIBUTES`) and was then
//!    read from `EVT_VHF_ASYNC_OPERATION`, which VHF documents as IRQL <= DISPATCH_LEVEL — a wrong-IRQL
//!    access that is a verifier break, or `IRQL_NOT_LESS_OR_EQUAL` without one. See `src/irql.rs`.
//!
//! 3. **One framework version.** `src/kmdf_version.rs` is the only place 1.33 appears; `build.rs` derives the
//!    function-table symbol `WdfFunctions_01033` from it, and the build script stamps the INF from it. A build
//!    resolved against a different framework is an unresolved symbol. In the C driver the header parse gets the
//!    version from `-DKMDF_VERSION_MINOR` in `driver/build.sh` while the INF gets it from a separate `sed`
//!    substitution of `$KMDFVERSION$` — two places, which is how a driver ends up linking a framework its INF
//!    does not declare, and a driver whose INF declares a framework the target does not have will not start.
//!
//! 4. **`PAGED_CODE()` cannot be written outside the pageable section.** `paged_fn!` places the function in
//!    `PAGE` and hands it the `PagedGuard` that the assertion needs, in one expansion. The C driver's
//!    `PAGED_CODE()` is a hand-paired statement about where a function was placed.
//!
//! # What is NOT verified
//!
//! This driver has never been loaded or executed. It cannot be: it is unsigned, loading it needs test mode,
//! and a driver fault is a blue screen on the owner's working machine. What is verified is that it **compiles
//! and packages** — that the object links into a PE x64 NATIVE image with the same imports as the C driver,
//! and that the wire contract's sizes and offsets hold. Nothing about its behaviour in the kernel is claimed.

#![no_std]
#![allow(non_snake_case, non_camel_case_types, non_upper_case_globals)]
// `paged_fn!` captures the doc comment written inside its invocation and applies it to the function it
// expands to, which is where the documentation has to be written for the macro to see it. rustc's
// `unused_doc_comments` fires on the token in argument position regardless.
#![allow(unused_doc_comments)]

pub mod contract;
pub mod ffi;
pub mod irql;

use core::ffi::{c_ulong, c_ushort, c_void};
use core::sync::atomic::{AtomicBool, AtomicI32, Ordering};

use contract::*;
use ffi::*;
use irql::{Attributes, Dispatch, Passive, Queue};

// ---------------------------------------------------------------------------------------------------------
// The kernel runtime Rust expects and a driver must supply
// ---------------------------------------------------------------------------------------------------------
//
// With `/NODEFAULTLIB` (build.rs) there is no CRT, so the symbols LLVM emits for bulk memory operations have
// to come from somewhere. LLVM lowers `copy_nonoverlapping` / `write_bytes` over an unknown length to
// `memcpy` / `memset` *calls*, so a driver that copies a layout blob without these four definitions links
// with three unresolved externals.
//
// They are written with volatile accesses on purpose. A plain byte loop is recognised by LLVM as the very
// pattern it turns into a `memcpy` call, and the result is a function that calls itself until the stack runs
// out; a volatile access is opaque to that transformation.
//
// SAFETY: these have the C ABI and semantics of the standard functions of the same names. They are linked
// into a kernel image where every caller is kernel code operating on kernel buffers.

// The signatures are the ones rustc expects for these symbols (`*mut c_void`, not `*mut u8`): it warns
// otherwise, because a definition that does not match the compiler's own idea of `memcpy` is a function the
// optimiser will call with a different parameter convention than it was written for.

/// # Safety
/// `destination` and `source` must be valid for `count` bytes and must not overlap.
#[no_mangle]
pub unsafe extern "C" fn memcpy(destination: *mut c_void, source: *const c_void, count: usize) -> *mut c_void {
    let destination = destination as *mut u8;
    let source = source as *const u8;
    let mut i = 0;
    while i < count {
        unsafe { core::ptr::write_volatile(destination.add(i), core::ptr::read_volatile(source.add(i))) };
        i += 1;
    }
    destination as *mut c_void
}

/// # Safety
/// `destination` and `source` must each be valid for `count` bytes; they may overlap.
#[no_mangle]
pub unsafe extern "C" fn memmove(destination: *mut c_void, source: *const c_void, count: usize) -> *mut c_void {
    let destination = destination as *mut u8;
    let source = source as *const u8;
    if (destination as usize) < (source as usize) {
        let mut i = 0;
        while i < count {
            unsafe { core::ptr::write_volatile(destination.add(i), core::ptr::read_volatile(source.add(i))) };
            i += 1;
        }
    } else {
        let mut i = count;
        while i > 0 {
            i -= 1;
            unsafe { core::ptr::write_volatile(destination.add(i), core::ptr::read_volatile(source.add(i))) };
        }
    }
    destination as *mut c_void
}

/// # Safety
/// `destination` must be valid for `count` bytes.
#[no_mangle]
pub unsafe extern "C" fn memset(destination: *mut c_void, value: i32, count: usize) -> *mut c_void {
    let destination = destination as *mut u8;
    let mut i = 0;
    while i < count {
        unsafe { core::ptr::write_volatile(destination.add(i), value as u8) };
        i += 1;
    }
    destination as *mut c_void
}

/// # Safety
/// `left` and `right` must each be valid for `count` bytes.
#[no_mangle]
pub unsafe extern "C" fn memcmp(left: *const c_void, right: *const c_void, count: usize) -> i32 {
    let left = left as *const u8;
    let right = right as *const u8;
    let mut i = 0;
    while i < count {
        let l = unsafe { core::ptr::read_volatile(left.add(i)) };
        let r = unsafe { core::ptr::read_volatile(right.add(i)) };
        if l != r {
            return l as i32 - r as i32;
        }
        i += 1;
    }
    0
}

/// The panic handler. A kernel driver that panics has nowhere to unwind to and nothing to log to, so this
/// stops the machine the way the WDK's own `wdk-panic` crate does. `panic = "abort"` in Cargo.toml means this
/// is the whole of the panic machinery — no unwinding tables, no `__CxxFrameHandler`.
#[panic_handler]
fn panic(_info: &core::panic::PanicInfo) -> ! {
    // KMODE_EXCEPTION_NOT_HANDLED. Chosen over looping because a driver that silently spins is far harder to
    // diagnose than one that bugchecks and leaves the panic site in its parameters.
    const KMODE_EXCEPTION_NOT_HANDLED: u32 = 0x0000_001E;
    const STATUS_UNSUCCESSFUL: u64 = 0xC000_0001;
    let message = _info.message().as_str().map_or(0, |text| text.as_ptr() as u64);
    // SAFETY: KeBugCheckEx does not return, and its arguments are four integer-sized values.
    unsafe { KeBugCheckEx(KMODE_EXCEPTION_NOT_HANDLED, STATUS_UNSUCCESSFUL, message, 0, 0) }
}

// ---------------------------------------------------------------------------------------------------------
// The framework version this driver reports it needs
// ---------------------------------------------------------------------------------------------------------

/// `wdffuncenum.h` emits this symbol as `__declspec(selectany) ULONG WdfMinimumVersionRequired =
/// KMDF_VERSION_MINOR;` — a definition the C driver gets from a header, parameterised by a `-D`, which is one
/// more place the version could disagree with the INF's `KmdfLibraryVersion` token. KMDF's own entry stub
/// reads it, so a driver that built against a newer framework than the target has says so here and refuses to
/// start rather than calling entry points that are not there.
///
/// Here it is the same constant `build.rs` spells the function-table symbol from and the build script stamps
/// the INF with, so the three cannot differ.
#[no_mangle]
pub static WdfMinimumVersionRequired: c_ulong = ffi::kmdf::KMDF_VERSION_MINOR;

/// KMDF's `WdfStructureCount` and `WdfStructures` are declared `extern` by the WDF headers and, in a C build,
/// defined by the `WDF_STUB`-less inclusion of `wdffuncenum.h`. The Rust driver parses the same header through
/// bindgen with the macros expanded the same way, so bindgen emits them as imports; WDFLDR supplies them.
///
/// (`WdfDriverGlobals` and `WdfFunctions_01033` come from the same place — `wdfldr.lib` — and are read
/// through `ffi::driver_globals()` and `ffi::kmdf::wdf_functions()`.)

// ---------------------------------------------------------------------------------------------------------
// Driver state
// ---------------------------------------------------------------------------------------------------------
//
// A single static, not a WDF object context. The C driver hangs its `AHLA_CONTEXT` off the device with
// `WDF_DECLARE_CONTEXT_TYPE_WITH_NAME`, which costs a registered `WDF_OBJECT_CONTEXT_TYPE_INFO` in a
// section WDFLDR walks at load time; none of that machinery buys anything for a device the app creates
// exactly once per process. The invariant is stated and enforced instead: `DEVICE_TAKEN` refuses a second
// device, and every WDF object this driver keeps (the device, both queues, both locks, the VHF handle) is
// parented to the single device, so teardown releases all of them.

struct Context {
    device: WDFDEVICE,

    /// The manual queue holding the app's parked `WAIT_FRAME` request.
    ///
    /// `Queue<Dispatch>`, not `Queue<Passive>`: this handle is read from `EVT_VHF_ASYNC_OPERATION`, and the
    /// type is what makes that legal. See `src/irql.rs`.
    frame_queue: Queue<Dispatch>,

    /// Guards everything below except the VHF lifecycle. A spin lock, because the VHF Get/SetFeature
    /// callbacks can run at DISPATCH_LEVEL.
    lock: WDFSPINLOCK,

    /// Serialises `VhfCreate`/`VhfStart`/`VhfDelete`, which are PASSIVE_LEVEL only, against a concurrent
    /// SET_LAYOUT/STOP/last-handle-close on another thread — the IOCTL queue is parallel.
    lifecycle: WDFWAITLOCK,

    vhf: VHFHANDLE,
    vhf_started: BOOLEAN,

    /// Open user-mode handles. The app opens two (control + frames); the virtual device is torn down when the
    /// count reaches zero, so a crashed app cannot leave a lighting device listed that nothing backs.
    ///
    /// `AtomicI32` where C uses `InterlockedIncrement` on a bare `LONG`: the same operation, with the
    /// atomicity stated in the type rather than in the choice of intrinsic.
    open_count: AtomicI32,

    layout: AhlaLayout,

    /// Which lamp the next `LampAttributesResponse` GET describes. The host enumerates by GETting repeatedly,
    /// and the spec has the device auto-advance after each response (a SET of the request report overrides it).
    next_lamp_id: c_ushort,

    /// TRUE = no host owns the surface (the device is free to paint whatever it likes; for us that means the
    /// app keeps its own lighting). A host clears it to take control and sets it again to hand the surface back.
    autonomous: BOOLEAN,

    sequence: c_ulong,

    /// Host writes land in `staging` and are only promoted to `published` when the host marks a batch complete
    /// — so the app never paints a half-updated keyboard. A 4-zone frame arrives as one multi-update report,
    /// but a bigger array would take several.
    staging: [AhlaColor; AHLA_MAX_LAMPS],
    published: [AhlaColor; AHLA_MAX_LAMPS],

    /// A complete frame the app has not collected yet. Last-one-wins: a newer frame overwrites `published`, so
    /// a slow app never builds a backlog — it just skips intermediate frames.
    frame_pending: BOOLEAN,
}

/// Holds the one driver context. The `UnsafeCell` is the whole point: every field is either guarded by
/// `Context::lock` or by the single-instance invariant, and the accessor below is the one door.
struct Global(core::cell::UnsafeCell<core::mem::MaybeUninit<Context>>);

// SAFETY: the only accessor is `context()`, which hands out a `&'static mut Context`. The driver is
// single-instance (`DEVICE_TAKEN`), every callback that runs concurrently reaches the fields behind
// `Context::lock`, and the VHF lifecycle is behind `Context::lifecycle`.
unsafe impl Sync for Global {}

static CONTEXT: Global = Global(core::cell::UnsafeCell::new(core::mem::MaybeUninit::uninit()));

/// Refuses a second `EvtDeviceAdd`. The INF describes a root-enumerated device the app creates once; a second
/// add would mean two devices sharing one context, which is precisely the class of bug the C driver's
/// per-device context made impossible and this static would make possible if it were unguarded.
static DEVICE_TAKEN: AtomicBool = AtomicBool::new(false);

/// # Safety
///
/// Every caller must hold either `DEVICE_TAKEN` (i.e. be a callback of the one device) or be
/// `AhlaEvtDeviceAdd`, which is what sets it.
#[inline(always)]
unsafe fn context() -> &'static mut Context {
    unsafe { (*CONTEXT.0.get()).assume_init_mut() }
}

// ---------------------------------------------------------------------------------------------------------
// DriverEntry
// ---------------------------------------------------------------------------------------------------------

/// # Safety
/// Called by KMDF's `FxDriverEntry` stub, which `WdfDriverEntry.lib` supplies and `/ENTRY:FxDriverEntry`
/// names. `driver_object` and `registry_path` are the ones the loader passed.
///
/// In `INIT` rather than `PAGE`, matching the C driver — the section is discardable once `DriverEntry` has
/// run, and there is no second call.
#[no_mangle]
#[inline(never)]
#[link_section = "INIT"]
pub unsafe extern "system" fn DriverEntry(
    driver_object: PDRIVER_OBJECT,
    registry_path: PCUNICODE_STRING,
) -> NTSTATUS {
    let mut config: WDF_DRIVER_CONFIG = unsafe { core::mem::zeroed() };
    config.Size = core::mem::size_of::<WDF_DRIVER_CONFIG>() as c_ulong;
    config.EvtDriverDeviceAdd = Some(AhlaEvtDeviceAdd);

    // KMDF's DriverEntry contract: DriverObject and RegistryPath are the loader's, and KMDF takes ownership
    // of the driver object from here.
    wdf_driver_create(
        driver_object,
        registry_path,
        core::ptr::null_mut(), // WDF_NO_OBJECT_ATTRIBUTES
        &mut config,
        core::ptr::null_mut(), // WDF_NO_HANDLE: the driver object is not needed after creation
    )
}

// ---------------------------------------------------------------------------------------------------------
// Device add and teardown
// ---------------------------------------------------------------------------------------------------------

crate::paged_fn! {
    /// `EVT_WDF_DRIVER_DEVICE_ADD`. Builds the control device: file-object callbacks, buffered IO, the
    /// symbolic link the app opens, and the two queues.
    ///
    /// Pageable (see `paged_fn!`), because everything it does is PASSIVE_LEVEL work done once per device.
    unsafe fn AhlaEvtDeviceAdd(driver: WDFDRIVER, device_init: PWDFDEVICE_INIT) -> NTSTATUS {
        let _ = driver;

        if DEVICE_TAKEN.swap(true, Ordering::AcqRel) {
            // One device, one context — see `CONTEXT`. Refusing is better than silently sharing state.
            return STATUS_INSUFFICIENT_RESOURCES;
        }

        // File-object callbacks exist because the virtual device must exist only while the app holds a
        // handle: an app crash or a plain exit then removes it from the OS's lighting device list instead of
        // leaving a dead entry the user can select in Settings.
        let mut file_config: WDF_FILEOBJECT_CONFIG = unsafe { core::mem::zeroed() };
        file_config.Size = core::mem::size_of::<WDF_FILEOBJECT_CONFIG>() as c_ulong;
        file_config.EvtDeviceFileCreate = Some(AhlaEvtFileCreate);
        file_config.EvtFileClose = Some(AhlaEvtFileClose);
        // EvtFileCleanup stays NULL, as in the C driver: an app killed outright still closes its handles.
        wdf_device_init_set_file_object_config(
            device_init,
            &mut file_config,
            core::ptr::null_mut(), // WDF_NO_OBJECT_ATTRIBUTES for the file objects
        );
        wdf_device_init_set_io_type(device_init, val::DEVICE_IO_BUFFERED);

        // Passive execution level for the device and everything that inherits from it: the cleanup callback
        // calls VhfDelete and the IOCTL path calls VhfCreate/VhfStart, all of which are PASSIVE_LEVEL only.
        // Nothing here is on a performance path, so none of it needs to run at DISPATCH.
        let attributes = Attributes::at_with_cleanup::<Passive>(Some(AhlaEvtDeviceCleanup));

        let mut device: WDFDEVICE = core::ptr::null_mut();
        let status = wdf_device_create(&mut (device_init as *mut _), attributes.raw(), &mut device);
        if !nt_success(status) {
            DEVICE_TAKEN.store(false, Ordering::Release);
            return status;
        }

        let ctx = unsafe { context() };
        // SAFETY: the context is uninitialised storage owned by this driver, and the device has just been
        // created — nothing else can have a reference to it yet.
        unsafe {
            core::ptr::write_bytes(ctx as *mut Context as *mut u8, 0, core::mem::size_of::<Context>());
        }
        ctx.device = device;
        ctx.autonomous = 1; // nothing owns the surface until a host says otherwise

        let mut status = wdf_spin_lock_create(core::ptr::null_mut(), &mut ctx.lock);
        if !nt_success(status) {
            return status;
        }
        status = wdf_wait_lock_create(core::ptr::null_mut(), &mut ctx.lifecycle);
        if !nt_success(status) {
            return status;
        }

        let symbolic_link = unicode_string(AHLA_SYMBOLIC_LINK);
        status = wdf_device_create_symbolic_link(device, &symbolic_link);
        if !nt_success(status) {
            return status;
        }

        // The default queue: PARALLEL, and at PASSIVE_LEVEL because SET_LAYOUT calls VhfCreate/VhfStart,
        // which are PASSIVE-only. Parallel matters because WAIT_FRAME pends for as long as the host is idle;
        // a sequential queue would make the app's STOP wait behind it.
        //
        // `Queue::<Passive>::create` is what writes `WdfExecutionLevelPassive` into the attributes. The level
        // is not inherited from the device here — it is chosen, and the type records the choice.
        let mut queue_config: WDF_IO_QUEUE_CONFIG = unsafe { core::mem::zeroed() };
        queue_config.Size = core::mem::size_of::<WDF_IO_QUEUE_CONFIG>() as c_ulong;
        queue_config.DispatchType = val::QUEUE_PARALLEL;
        queue_config.DefaultQueue = 1;
        queue_config.EvtIoDeviceControl = Some(AhlaEvtIoDeviceControl);
        if let Err(status) = Queue::<Passive>::create(device, &mut queue_config) {
            return status;
        }

        // The frame queue: MANUAL (so the app's request parks until there is a frame to hand it) and at
        // DISPATCH_LEVEL, because it is read from EVT_VHF_ASYNC_OPERATION.
        //
        // This is the second of the four defects. In C, `WDF_IO_QUEUE_CONFIG_INIT(&queueConfig,
        // val::QUEUE_MANUAL)` is followed by `WdfIoQueueCreate(..., WDF_NO_OBJECT_ATTRIBUTES, ...)`,
        // which leaves `ExecutionLevel` at `WdfExecutionLevelInheritFromParent` and so inherits the device's
        // Passive; `AhlaFlushFrame` then calls `WdfIoQueueRetrieveNextRequest` on it from `AhlaEvtSetFeature`.
        // Here the level is an argument to the type, and the retrieval method only exists on the level that
        // makes it legal.
        let mut frame_config: WDF_IO_QUEUE_CONFIG = unsafe { core::mem::zeroed() };
        frame_config.Size = core::mem::size_of::<WDF_IO_QUEUE_CONFIG>() as c_ulong;
        frame_config.DispatchType = val::QUEUE_MANUAL;
        match Queue::<Dispatch>::create(device, &mut frame_config) {
            Ok(queue) => ctx.frame_queue = queue,
            Err(status) => return status,
        }

        // The virtual HID device is deliberately NOT created here. It appears on the first SET_LAYOUT,
        // because its attributes (how many lamps, where they are) come from the app — publishing a LampArray
        // with no lamps would show the user a lighting device that cannot be painted.
        STATUS_SUCCESS
    }
}

crate::paged_fn! {
    /// `EVT_WDF_DEVICE_CONTEXT_CLEANUP`. Runs when the device is being torn down; the only thing that has to
    /// happen is that the virtual HID device stops existing before the device object does.
    unsafe fn AhlaEvtDeviceCleanup(object: WDFOBJECT) -> () {
        let _ = object;
        let ctx = unsafe { context() };
        unsafe { ahla_stop_vhf(ctx) };
        DEVICE_TAKEN.store(false, Ordering::Release);
    }
}

crate::paged_fn! {
    /// `EVT_WDF_DEVICE_FILE_CREATE`. Counts the open handles; nothing else. The device node is created by the
    /// app, so a handle arriving here means the app is already talking to us.
    unsafe fn AhlaEvtFileCreate(device: WDFDEVICE, request: WDFREQUEST, file_object: WDFFILEOBJECT) -> () {
        let _ = (device, file_object);
        let ctx = unsafe { context() };
        let _ = ctx.open_count.fetch_add(1, Ordering::AcqRel);
        wdf_request_complete(request, STATUS_SUCCESS);
    }
}

crate::paged_fn! {
    /// `EVT_WDF_FILE_CLOSE`. The last handle closing is the app going away — a clean exit, a crash, or the
    /// user turning the feature off — and it un-publishes the virtual device so Windows never lists a
    /// lighting device nothing is backing.
    unsafe fn AhlaEvtFileClose(file_object: WDFFILEOBJECT) -> () {
        let _ = file_object;
        let ctx = unsafe { context() };

        if ctx.open_count.fetch_sub(1, Ordering::AcqRel) > 1 {
            return;
        }

        // Finish any parked WAIT_FRAME first, then un-publish. This is where the C driver calls
        // `WdfIoQueuePurgeSynchronously` followed by `WdfIoQueueStart`; see `cancel_parked` for why that call
        // is not available here and why the replacement has the same observable behaviour.
        cancel_parked(ctx);
        unsafe { ahla_stop_vhf(ctx) };
    }
}

// ---------------------------------------------------------------------------------------------------------
// The IOCTL path
// ---------------------------------------------------------------------------------------------------------

/// `EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL`, on the default queue.
///
/// The queue handle KMDF passes is deliberately not turned into a `Queue<L>`: this handler has no way to know
/// its own execution level, and it does not need to — the only queue it touches is `ctx.frame_queue`, which
/// is a `Queue<Dispatch>` and therefore says what it is.
///
/// This function is at PASSIVE_LEVEL (the default queue was created at that level) and is pageable.
crate::paged_fn! {
    unsafe fn AhlaEvtIoDeviceControl(
        queue: WDFQUEUE,
        request: WDFREQUEST,
        output_buffer_length: usize,
        input_buffer_length: usize,
        io_control_code: c_ulong,
    ) -> () {
        let _ = (queue, input_buffer_length);
        let ctx = unsafe { context() };

        let status = match io_control_code {
            IOCTL_AHLA_SET_LAYOUT => unsafe { ahla_set_layout(ctx, request) },
            IOCTL_AHLA_WAIT_FRAME => {
                unsafe { ahla_wait_frame(ctx, request, output_buffer_length) }
            }
            IOCTL_AHLA_STOP => {
                cancel_parked(ctx);
                unsafe { ahla_stop_vhf(ctx) };
                STATUS_SUCCESS
            }
            _ => STATUS_INVALID_DEVICE_REQUEST,
        };

        wdf_request_complete(request, status);
    }
}

/// `IOCTL_AHLA_SET_LAYOUT`: publish the device, or re-publish it with a new layout.
///
/// # Safety
/// `ctx` is the driver context and `request` owns an input buffer of at least `sizeof(AHLA_LAYOUT)`.
unsafe fn ahla_set_layout(ctx: &mut Context, request: WDFREQUEST) -> NTSTATUS {
    let Some((buffer, length)) =
        wdf_request_retrieve_input_buffer(request, core::mem::size_of::<AhlaLayout>())
    else {
        return STATUS_INVALID_PARAMETER;
    };
    if length < core::mem::size_of::<AhlaLayout>() {
        return STATUS_INVALID_PARAMETER;
    }

    // SAFETY: WdfRequestRetrieveInputBuffer returned a buffer of at least the requested size.
    let layout = unsafe { core::ptr::read_unaligned(buffer as *const AhlaLayout) };
    if layout.version != AHLA_LAYOUT_VERSION
        || layout.lamp_count == 0
        || layout.lamp_count > AHLA_MAX_LAMPS as c_ulong
    {
        return STATUS_INVALID_PARAMETER;
    }

    // Re-publishing with a different layout means re-enumerating: the host caches the attributes it
    // interrogated (lamp count, positions), so swapping the table underneath it would leave Windows painting
    // a device shape that no longer exists. Stop first — a no-op on the usual call, which is the first one.
    unsafe { ahla_stop_vhf(ctx) };

    wdf_spin_lock_acquire(ctx.lock);
    ctx.layout = layout;
    ctx.staging = [ZERO_COLOR; AHLA_MAX_LAMPS];
    ctx.published = [ZERO_COLOR; AHLA_MAX_LAMPS];
    ctx.next_lamp_id = 0;
    ctx.autonomous = 1;
    ctx.frame_pending = 0;
    ctx.sequence = 0;
    wdf_spin_lock_release(ctx.lock);

    unsafe { ahla_start_vhf(ctx) }
}

/// `IOCTL_AHLA_WAIT_FRAME`: park the app's request until there is a frame.
///
/// # Safety
/// `ctx` is the driver context and `request` is one the app issued with an output buffer.
unsafe fn ahla_wait_frame(ctx: &mut Context, request: WDFREQUEST, output_buffer_length: usize) -> NTSTATUS {
    if output_buffer_length < core::mem::size_of::<AhlaFrame>() {
        return STATUS_BUFFER_TOO_SMALL;
    }

    // Park the request in the manual queue, then immediately try to service it. Doing it in that order is
    // what closes the lost-wakeup race: a frame published between "we saw no frame" and "the request is
    // queued" would otherwise sit unnoticed until the host happened to send another one.
    let status = wdf_request_forward_to_io_queue(request, ctx.frame_queue.raw());
    if !nt_success(status) {
        return status;
    }

    flush_frame(ctx);

    // The request now belongs to the frame queue; the caller must not complete it. Signalled with a status
    // that cannot be mistaken for one to return, since `WdfRequestComplete` after a successful forward is
    // itself a defect.
    WAIT_FRAME_PARKED
}

/// Returned by [`ahla_wait_frame`] to say "do not complete this request". `STATUS_PENDING` would be a lie to
/// the app if it ever escaped; this value is caught by the caller before it can.
const WAIT_FRAME_PARKED: NTSTATUS = 0x4000_0000u32 as core::ffi::c_long;

// ---------------------------------------------------------------------------------------------------------
// Frames
// ---------------------------------------------------------------------------------------------------------

/// Snapshot the published state into an `AHLA_FRAME`. Caller holds `ctx.lock`.
fn build_frame(ctx: &Context) -> AhlaFrame {
    let mut frame: AhlaFrame = unsafe { core::mem::zeroed() };
    frame.sequence = ctx.sequence;
    frame.autonomous_mode = if ctx.autonomous != 0 { 1 } else { 0 };
    frame.lamp_count = ctx.layout.lamp_count;
    frame.colors = ctx.published;
    frame
}

/// Promote the staged colours to a complete frame. Caller holds `ctx.lock`. Overwrites any frame the app has
/// not collected yet — deliberately: for lighting, the newest state is the only interesting one.
fn publish(ctx: &mut Context) {
    ctx.published = ctx.staging;
    ctx.sequence += 1;
    ctx.frame_pending = 1;
}

/// Hand the pending frame (if any) to a waiting app request (if any). Must be called WITHOUT `ctx.lock` held.
///
/// Reachable from `EVT_VHF_ASYNC_OPERATION`, i.e. at up to DISPATCH_LEVEL, and it reads `ctx.frame_queue` —
/// which is why that queue is a `Queue<Dispatch>` and not the Passive queue the C driver inherited.
fn flush_frame(ctx: &mut Context) {
    let mut frame: AhlaFrame = unsafe { core::mem::zeroed() };
    let mut request: Option<WDFREQUEST> = None;

    wdf_spin_lock_acquire(ctx.lock);
    if ctx.frame_pending != 0 {
        if let Some(parked) = ctx.frame_queue.retrieve_next() {
            frame = build_frame(ctx);
            ctx.frame_pending = 0;
            request = Some(parked);
        }
    }
    wdf_spin_lock_release(ctx.lock);

    let Some(request) = request else {
        return; // nothing to deliver, or nobody waiting (the frame stays pending)
    };

    match wdf_request_retrieve_output_buffer(request, core::mem::size_of::<AhlaFrame>()) {
        Some((buffer, _)) => {
            // SAFETY: the output buffer is at least sizeof(AHLA_FRAME); `frame` is a local of that size.
            unsafe { core::ptr::write_unaligned(buffer as *mut AhlaFrame, frame) };
            wdf_request_complete_with_information(request, STATUS_SUCCESS, core::mem::size_of::<AhlaFrame>());
        }
        None => wdf_request_complete(request, STATUS_INVALID_PARAMETER),
    }
}

/// End every parked `WAIT_FRAME` with `STATUS_CANCELLED`.
///
/// This is the replacement for the C driver's `WdfIoQueuePurgeSynchronously` + `WdfIoQueueStart` pair, and it
/// is the first of the four defects. KMDF forbids purging a queue synchronously from that queue's own event
/// callback — the purge waits for the callback that is running it — and a driver verifier turns that into a
/// break. Rather than rely on not doing it in the wrong place, `WdfIoQueuePurgeSynchronously` is simply absent
/// from `src/ffi.rs`: there is no slot index for it, no `Pfn*` type, no wrapper, so the call has no spelling.
///
/// What the purge was for is unblocking the app's request when it stops listening, and that is what this
/// does. Draining a *manual* queue with `WdfIoQueueRetrieveNextRequest` is available at any level the queue
/// is usable at, and completing each request with `STATUS_CANCELLED` is exactly what the framework's purge
/// does to a pending request — so the app sees the same `ERROR_OPERATION_ABORTED` it saw before, and
/// `LampArrayTransport.Stop` still treats that as its own cancellation rather than a failure.
fn cancel_parked(ctx: &mut Context) {
    while let Some(request) = ctx.frame_queue.retrieve_next() {
        wdf_request_complete(request, STATUS_CANCELLED);
    }
}

// ---------------------------------------------------------------------------------------------------------
// The virtual HID device lifecycle
// ---------------------------------------------------------------------------------------------------------

/// Create and start the virtual HID device, if it is not already running.
///
/// # Safety
/// `ctx` is the driver context; must be called at PASSIVE_LEVEL.
unsafe fn ahla_start_vhf(ctx: &mut Context) -> NTSTATUS {
    wdf_wait_lock_acquire(ctx.lifecycle);

    let mut status = STATUS_SUCCESS;
    if ctx.vhf_started != 0 {
        // Already published. SET_LAYOUT stops first, so this is only a redundant call.
        wdf_wait_lock_release(ctx.lifecycle);
        return status;
    }

    let mut config: VHF_CONFIG = unsafe { core::mem::zeroed() };
    config.Size = core::mem::size_of::<VHF_CONFIG>() as c_ulong;
    config.DeviceObject = wdf_device_wdm_get_device_object(ctx.device);
    config.ReportDescriptorLength = AHLA_REPORT_DESCRIPTOR.len() as c_ushort;
    config.ReportDescriptor = AHLA_REPORT_DESCRIPTOR.as_ptr() as PUCHAR;
    config.VhfClientContext = ctx as *mut Context as PVOID;
    config.VendorID = AHLA_VENDOR_ID;
    config.ProductID = AHLA_PRODUCT_ID;
    config.VersionNumber = AHLA_VERSION;
    config.EvtVhfAsyncOperationGetFeature = Some(AhlaEvtGetFeature);
    config.EvtVhfAsyncOperationSetFeature = Some(AhlaEvtSetFeature);

    let mut vhf: VHFHANDLE = core::ptr::null_mut();
    status = unsafe { VhfCreate(&mut config, &mut vhf) };
    if !nt_success(status) {
        wdf_wait_lock_release(ctx.lifecycle);
        return status;
    }

    status = unsafe { VhfStart(vhf) };
    if !nt_success(status) {
        unsafe { VhfDelete(vhf, 1) };
        wdf_wait_lock_release(ctx.lifecycle);
        return status;
    }

    ctx.vhf = vhf;
    ctx.vhf_started = 1;
    wdf_wait_lock_release(ctx.lifecycle);
    status
}

/// Delete the virtual HID device if it is running, and put the surface back in autonomous mode.
///
/// # Safety
/// `ctx` is the driver context; must be called at PASSIVE_LEVEL.
unsafe fn ahla_stop_vhf(ctx: &mut Context) {
    wdf_wait_lock_acquire(ctx.lifecycle);

    if ctx.vhf_started != 0 {
        // Synchronous delete (Wait = TRUE): by the time this returns VHF has stopped invoking our callbacks,
        // so the context is safe to reuse or free.
        unsafe { VhfDelete(ctx.vhf, 1) };
        ctx.vhf = core::ptr::null_mut();
        ctx.vhf_started = 0;
    }

    wdf_spin_lock_acquire(ctx.lock);
    ctx.autonomous = 1;
    ctx.frame_pending = 0;
    wdf_spin_lock_release(ctx.lock);

    wdf_wait_lock_release(ctx.lifecycle);
}

// ---------------------------------------------------------------------------------------------------------
// HID feature reports
// ---------------------------------------------------------------------------------------------------------

/// `EVT_VHF_ASYNC_OPERATION` for GET_FEATURE — the host interrogating the device's attributes.
///
/// Not pageable, and not guarded: VHF documents this callback as running at IRQL <= DISPATCH_LEVEL, so
/// nothing here may touch pageable memory. That is why the descriptor, the layout and the frame state are all
/// in non-paged memory and why the only lock taken is the spin lock.
///
/// # Safety
/// Called by VHF with the client context this driver passed to `VHF_CONFIG`.
pub unsafe extern "C" fn AhlaEvtGetFeature(
    vhf_client_context: PVOID,
    vhf_operation_handle: VHFOPERATIONHANDLE,
    vhf_operation_context: PVOID,
    hid_transfer_packet: PHID_XFER_PACKET,
) {
    let _ = vhf_operation_context;
    let ctx = unsafe { &mut *(vhf_client_context as *mut Context) };
    let mut status = STATUS_INVALID_DEVICE_REQUEST;

    if hid_transfer_packet.is_null() || unsafe { (*hid_transfer_packet).reportBuffer }.is_null() {
        unsafe { VhfAsyncOperationComplete(vhf_operation_handle, STATUS_INVALID_PARAMETER) };
        return;
    }

    wdf_spin_lock_acquire(ctx.lock);

    // SAFETY: checked non-null above; VHF owns the packet for the duration of this callback.
    let packet = unsafe { &*hid_transfer_packet };
    match packet.reportId {
        AHLA_REPORT_ATTRIBUTES => {
            if (packet.reportBufferLen as usize) < core::mem::size_of::<AhlaAttributesReport>() {
                status = STATUS_BUFFER_TOO_SMALL;
            } else {
                let mut report: AhlaAttributesReport = unsafe { core::mem::zeroed() };
                report.report_id = AHLA_REPORT_ATTRIBUTES;
                report.lamp_count = ctx.layout.lamp_count as c_ushort;
                report.bounding_box_width_in_micrometers = ctx.layout.bounding_box_width_um;
                report.bounding_box_height_in_micrometers = ctx.layout.bounding_box_height_um;
                report.bounding_box_depth_in_micrometers = ctx.layout.bounding_box_depth_um;
                report.lamp_array_kind = ctx.layout.kind;
                report.min_update_interval_in_microseconds = ctx.layout.min_update_interval_us;
                // SAFETY: the length check above guarantees room for the whole report.
                unsafe { write_report(packet.reportBuffer, report) };
                status = STATUS_SUCCESS;
            }
        }

        AHLA_REPORT_ATTR_RESPONSE => {
            if (packet.reportBufferLen as usize) < core::mem::size_of::<AhlaAttrResponseReport>() {
                status = STATUS_BUFFER_TOO_SMALL;
            } else if ctx.layout.lamp_count == 0 {
                status = STATUS_DEVICE_NOT_READY;
            } else {
                let mut id = ctx.next_lamp_id;
                if id as c_ulong >= ctx.layout.lamp_count {
                    id = 0;
                }
                let lamp = ctx.layout.lamps[id as usize];

                let mut report: AhlaAttrResponseReport = unsafe { core::mem::zeroed() };
                report.report_id = AHLA_REPORT_ATTR_RESPONSE;
                report.lamp_id = id;
                report.position_x_in_micrometers = lamp.position_x_um;
                report.position_y_in_micrometers = lamp.position_y_um;
                report.position_z_in_micrometers = lamp.position_z_um;
                report.update_latency_in_microseconds = lamp.update_latency_us;
                report.lamp_purposes = lamp.purposes;
                report.red_level_count = lamp.red_levels;
                report.green_level_count = lamp.green_levels;
                report.blue_level_count = lamp.blue_levels;
                report.intensity_level_count = lamp.intensity_levels;
                report.is_programmable = lamp.is_programmable;
                report.input_binding = lamp.input_binding;
                // SAFETY: the length check above guarantees room for the whole report.
                unsafe { write_report(packet.reportBuffer, report) };

                // Auto-advance, wrapping: this is how the host walks the whole array with repeated GETs.
                ctx.next_lamp_id = ((id as c_ulong + 1) % ctx.layout.lamp_count) as c_ushort;
                status = STATUS_SUCCESS;
            }
        }

        // Not a GET-able report: STATUS_INVALID_DEVICE_REQUEST stands.
        _ => {}
    }

    wdf_spin_lock_release(ctx.lock);
    unsafe { VhfAsyncOperationComplete(vhf_operation_handle, status) };
}

/// `EVT_VHF_ASYNC_OPERATION` for SET_FEATURE — the host painting the keyboard.
///
/// # Safety
/// Called by VHF with the client context this driver passed to `VHF_CONFIG`.
pub unsafe extern "C" fn AhlaEvtSetFeature(
    vhf_client_context: PVOID,
    vhf_operation_handle: VHFOPERATIONHANDLE,
    vhf_operation_context: PVOID,
    hid_transfer_packet: PHID_XFER_PACKET,
) {
    let _ = vhf_operation_context;
    let ctx = unsafe { &mut *(vhf_client_context as *mut Context) };
    let mut status = STATUS_INVALID_DEVICE_REQUEST;
    let mut deliver = false;

    if hid_transfer_packet.is_null() || unsafe { (*hid_transfer_packet).reportBuffer }.is_null() {
        unsafe { VhfAsyncOperationComplete(vhf_operation_handle, STATUS_INVALID_PARAMETER) };
        return;
    }

    wdf_spin_lock_acquire(ctx.lock);

    // SAFETY: checked non-null above; VHF owns the packet for the duration of this callback.
    let packet = unsafe { &*hid_transfer_packet };
    match packet.reportId {
        AHLA_REPORT_ATTR_REQUEST => {
            if (packet.reportBufferLen as usize) < core::mem::size_of::<AhlaAttrRequestReport>() {
                status = STATUS_BUFFER_TOO_SMALL;
            } else {
                let report = unsafe { read_report::<AhlaAttrRequestReport>(packet.reportBuffer) };
                // Per spec, an out-of-range id selects lamp 0 rather than failing.
                ctx.next_lamp_id = if (report.lamp_id as c_ulong) < ctx.layout.lamp_count {
                    report.lamp_id
                } else {
                    0
                };
                status = STATUS_SUCCESS;
            }
        }

        AHLA_REPORT_MULTI_UPDATE => {
            if (packet.reportBufferLen as usize) < core::mem::size_of::<AhlaMultiUpdateReport>() {
                status = STATUS_BUFFER_TOO_SMALL;
            } else {
                let report = unsafe { read_report::<AhlaMultiUpdateReport>(packet.reportBuffer) };
                let mut i = 0usize;
                while i < (report.lamp_count as usize).min(AHLA_MULTI_UPDATE_LAMP_COUNT) {
                    let id = report.lamp_ids[i];
                    if (id as c_ulong) < ctx.layout.lamp_count {
                        let color = report.update_colors[i];
                        ctx.staging[id as usize] = AhlaColor {
                            red: color.red_channel,
                            green: color.green_channel,
                            blue: color.blue_channel,
                            intensity: color.intensity_channel,
                        };
                    }
                    i += 1;
                }

                if report.lamp_update_flags & AHLA_UPDATE_FLAG_COMPLETE != 0 {
                    publish(ctx);
                    deliver = true;
                }
                status = STATUS_SUCCESS;
            }
        }

        AHLA_REPORT_RANGE_UPDATE => {
            if (packet.reportBufferLen as usize) < core::mem::size_of::<AhlaRangeUpdateReport>() {
                status = STATUS_BUFFER_TOO_SMALL;
            } else {
                let report = unsafe { read_report::<AhlaRangeUpdateReport>(packet.reportBuffer) };
                if report.lamp_id_start <= report.lamp_id_end
                    && (report.lamp_id_end as c_ulong) < ctx.layout.lamp_count
                {
                    let mut id = report.lamp_id_start as c_ulong;
                    while id <= report.lamp_id_end as c_ulong {
                        ctx.staging[id as usize] = AhlaColor {
                            red: report.update_color.red_channel,
                            green: report.update_color.green_channel,
                            blue: report.update_color.blue_channel,
                            intensity: report.update_color.intensity_channel,
                        };
                        id += 1;
                    }

                    if report.lamp_update_flags & AHLA_UPDATE_FLAG_COMPLETE != 0 {
                        publish(ctx);
                        deliver = true;
                    }
                }
                status = STATUS_SUCCESS;
            }
        }

        AHLA_REPORT_CONTROL => {
            if (packet.reportBufferLen as usize) < core::mem::size_of::<AhlaControlReport>() {
                status = STATUS_BUFFER_TOO_SMALL;
            } else {
                let report = unsafe { read_report::<AhlaControlReport>(packet.reportBuffer) };
                let autonomous = if report.autonomous_mode != 0 { 1 } else { 0 };
                if autonomous != ctx.autonomous {
                    ctx.autonomous = autonomous;

                    // Hand-BACK is published at once, so the app stops waiting and repaints its own lighting
                    // instead of leaving the host's last frame frozen on the keyboard. A TAKE-over is
                    // deliberately NOT published: the staged colours are still black at that point, and
                    // shipping them would flash the keyboard off for one interval before the host's first
                    // real frame arrives.
                    if autonomous != 0 {
                        publish(ctx);
                        deliver = true;
                    }
                }
                status = STATUS_SUCCESS;
            }
        }

        _ => {}
    }

    wdf_spin_lock_release(ctx.lock);

    if deliver {
        flush_frame(ctx);
    }

    unsafe { VhfAsyncOperationComplete(vhf_operation_handle, status) };
}

// ---------------------------------------------------------------------------------------------------------
// Report buffer helpers
// ---------------------------------------------------------------------------------------------------------
//
// The HID reports are `#[repr(C, packed)]` because the descriptor is built on one-byte boundaries, and the
// buffers VHF hands over carry no alignment promise. Both directions therefore go through the unaligned
// accessors rather than a cast-and-dereference, which on x86 happens to work and on any other architecture
// is undefined behaviour — and is the kind of thing a driver verifier's unaligned-access checks exist to
// catch.

unsafe fn write_report<T>(destination: PUCHAR, value: T) {
    unsafe { core::ptr::write_unaligned(destination as *mut T, value) };
}

unsafe fn read_report<T: Copy>(source: PUCHAR) -> T {
    unsafe { core::ptr::read_unaligned(source as *const T) }
}

// ---------------------------------------------------------------------------------------------------------
// The symbolic link name
// ---------------------------------------------------------------------------------------------------------

/// `\DosDevices\AcerHelperLampArray`, as the UTF-16 the kernel wants. The C driver spells this with
/// `DECLARE_CONST_UNICODE_STRING`, which counts the characters for you; here the length is asserted instead,
/// because a `UNICODE_STRING` whose `Length` disagrees with its buffer is a name that silently does not match
/// — and then the app's `CreateFile` on `\\.\AcerHelperLampArray` fails with "cannot open the frame channel".
const fn utf16_of(value: &str) -> [u16; 31] {
    let bytes = value.as_bytes();
    let mut out = [0u16; 31];
    let mut i = 0;
    while i < bytes.len() {
        out[i] = bytes[i] as u16; // every character here is ASCII
        i += 1;
    }
    out
}

static SYMBOLIC_LINK_BUFFER: [u16; 31] = utf16_of(AHLA_SYMBOLIC_LINK);

// The link name is `\DosDevices\` followed by the hardware id, and that is not a coincidence to be left
// implicit: user mode opens `\\.\` + the hardware id (LampArrayTransport.DevicePath) and RtlDosPathNameToNt
// turns it into exactly this, so the app finds the control device only while the two agree.
const _: () = {
    let link = AHLA_SYMBOLIC_LINK.as_bytes();
    let id = AHLA_HARDWARE_ID.as_bytes();
    assert!(link.len() == 12 + id.len());
    let mut i = 0;
    while i < id.len() {
        assert!(link[12 + i] == id[i]);
        i += 1;
    }
};

fn unicode_string(value: &str) -> UNICODE_STRING {
    let _ = value;
    UNICODE_STRING {
        Length: (31 * 2) as c_ushort,
        MaximumLength: (31 * 2) as c_ushort,
        Buffer: SYMBOLIC_LINK_BUFFER.as_ptr() as *mut u16,
    }
}

/// A lamp colour that is off. Named so the two arrays in `Context` can be cleared without a `Default` impl
/// that would have to be written for every type in the contract.
const ZERO_COLOR: AhlaColor = AhlaColor { red: 0, green: 0, blue: 0, intensity: 0 };
