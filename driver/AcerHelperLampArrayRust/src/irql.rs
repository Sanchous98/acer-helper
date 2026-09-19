//! The two type-level rules that make two of the C driver's defects unwritable here.
//!
//! Both defects are the same shape: an obligation the C compiler cannot see, discharged by a comment and by
//! remembering. A queue's execution level is an attribute that inherited silently from the device when the
//! caller passed `WDF_NO_OBJECT_ATTRIBUTES`; a `PAGED_CODE()` assertion is a macro that has to be paired, by
//! hand, with a function that was actually placed in the pageable section. Neither can be checked by a C
//! compiler, and neither was.
//!
//! Here they are type parameters and macro expansions instead, so the wrong program does not type-check:
//!
//! * **`Queue<L>`** records the execution level it was created with. `WdfIoQueueRetrieveNextRequest` — the
//!   call the C driver's `AhlaFlushFrame` makes — exists only on `Queue<Dispatch>`. It is not a runtime check
//!   and not a lint: `Queue<Passive>` has no such method, so a DISPATCH-reachable queue that was created
//!   Passive cannot be read, and the code that tried fails to compile.
//!
//! * **`paged_fn!`** places a function in the `PAGE` section *and* hands it a `PagedGuard` in one expansion.
//!   `PagedGuard` is constructed by nothing else, so an assertion that the caller is pageable cannot appear
//!   in a function that is not pageable — the two are one act.

use core::marker::PhantomData;

use crate::ffi::*;

// ---------------------------------------------------------------------------------------------------------
// Execution levels
// ---------------------------------------------------------------------------------------------------------

mod sealed {
    pub trait Sealed {}
}

/// An IRQL band. Implemented by [`Passive`] and [`Dispatch`] and by nothing else — the `sealed` supertrait is
/// what keeps a third, meaningless level from being written.
pub trait Level: sealed::Sealed {
    /// The `WDF_EXECUTION_LEVEL` this marker stands for. `WdfExecutionLevelInheritFromParent` is deliberately
    /// not among them: inheritance is the mechanism that produced the C defect.
    const EXECUTION_LEVEL: WDF_EXECUTION_LEVEL;
}

/// PASSIVE_LEVEL only, i.e. `WdfExecutionLevelPassive`.
///
/// This is the level of anything that calls `VhfCreate`, `VhfStart` or `VhfDelete`, all of which the VHF
/// documentation restricts to PASSIVE_LEVEL.
pub struct Passive;

/// `<= DISPATCH_LEVEL`, i.e. `WdfExecutionLevelDispatch`.
///
/// This is what a queue must be at to be touched from a `EVT_VHF_ASYNC_OPERATION` callback: VHF documents
/// those as running at IRQL `<= DISPATCH_LEVEL`, and a request parked in a PASSIVE-only queue may not be
/// retrieved from there — the framework's passive-level machinery is not available.
pub struct Dispatch;

impl sealed::Sealed for Passive {}
impl sealed::Sealed for Dispatch {}

impl Level for Passive {
    const EXECUTION_LEVEL: WDF_EXECUTION_LEVEL = val::EXECUTION_PASSIVE;
}

impl Level for Dispatch {
    const EXECUTION_LEVEL: WDF_EXECUTION_LEVEL = val::EXECUTION_DISPATCH;
}

// ---------------------------------------------------------------------------------------------------------
// Attributes that state their level
// ---------------------------------------------------------------------------------------------------------

/// A `WDF_OBJECT_ATTRIBUTES` whose execution level is always stated.
///
/// `WDF_OBJECT_ATTRIBUTES_INIT` zeroes `ExecutionLevel`, which is `WdfExecutionLevelInheritFromParent`, so
/// an object created with the plain init inherits whatever its parent had. That is exactly how the C driver's
/// `FrameQueue` ended up Passive: it was created with `WDF_NO_OBJECT_ATTRIBUTES` under a device whose
/// `ExecutionLevel` is `WdfExecutionLevelPassive`. Here there is no way to build attributes without naming a
/// level, and the level is the one the returned object's type records.
pub struct Attributes {
    raw: WDF_OBJECT_ATTRIBUTES,
}

impl Attributes {
    /// Attributes at `L`, with no cleanup callback.
    pub fn at<L: Level>() -> Self {
        // SAFETY: zero is the state WDF_OBJECT_ATTRIBUTES_INIT produces — every field means "none" at zero,
        // including the two callbacks and the context-type pointer.
        let mut raw: WDF_OBJECT_ATTRIBUTES = unsafe { core::mem::zeroed() };
        raw.Size = core::mem::size_of::<WDF_OBJECT_ATTRIBUTES>() as u32;
        raw.ExecutionLevel = L::EXECUTION_LEVEL;
        Self { raw }
    }

    /// The same attributes with a cleanup callback — the device's, in this driver, which is where the virtual
    /// HID device is torn down.
    pub fn at_with_cleanup<L: Level>(
        cleanup: PFN_WDF_OBJECT_CONTEXT_CLEANUP,
    ) -> Self {
        let mut attributes = Self::at::<L>();
        attributes.raw.EvtCleanupCallback = cleanup;
        attributes
    }

    pub fn raw(&self) -> PWDF_OBJECT_ATTRIBUTES {
        &self.raw as *const WDF_OBJECT_ATTRIBUTES as *mut WDF_OBJECT_ATTRIBUTES
    }
}

// ---------------------------------------------------------------------------------------------------------
// Queues that know their level
// ---------------------------------------------------------------------------------------------------------

/// A `WDFQUEUE` and the execution level it was created at.
pub struct Queue<L: Level> {
    handle: WDFQUEUE,
    _level: PhantomData<L>,
}

impl<L: Level> Queue<L> {
    /// Create the queue and record its level in the returned type.
    ///
    /// The level is written into the attributes *by this function*, from the type parameter, so the queue's
    /// level and the type of the handle that reaches it are the same fact. There is no overload that lets a
    /// caller pass attributes of its own choosing and no `WDF_NO_OBJECT_ATTRIBUTES` path.
    pub fn create(device: WDFDEVICE, config: &mut WDF_IO_QUEUE_CONFIG) -> Result<Self, NTSTATUS> {
        let attributes = Attributes::at::<L>();
        let mut handle: WDFQUEUE = core::ptr::null_mut();
        let status = wdf_io_queue_create(device, config as *mut _, attributes.raw(), &mut handle);
        if nt_success(status) {
            Ok(Self { handle, _level: PhantomData })
        } else {
            Err(status)
        }
    }

    pub fn raw(&self) -> WDFQUEUE {
        self.handle
    }

    /// `WdfIoQueueStart`. Legal at any level the framework allows a queue to be touched at, so it is on the
    /// level-generic impl rather than on `Dispatch` alone.
    pub fn start(&self) {
        wdf_io_queue_start(self.handle);
    }

    pub fn device(&self) -> WDFDEVICE {
        wdf_io_queue_get_device(self.handle)
    }
}

impl Queue<Dispatch> {
    /// `WdfIoQueueRetrieveNextRequest` — and the whole reason this module exists.
    ///
    /// The framework documents this call as `_IRQL_requires_max_(DISPATCH_LEVEL)` on the queue, meaning the
    /// queue must itself be usable at DISPATCH_LEVEL. It is implemented here, on `Queue<Dispatch>` only, so
    /// the C driver's defect — retrieving from a Passive queue inside `EVT_VHF_ASYNC_OPERATION` — has no
    /// spelling: `Queue<Passive>` has no `retrieve_next`, and `Queue<Dispatch>` is only ever produced by
    /// `Queue::create` having been asked for `Dispatch`.
    ///
    /// Returns `None` when the queue is empty, which is the ordinary case (nothing is waiting for a frame).
    pub fn retrieve_next(&self) -> Option<WDFREQUEST> {
        wdf_io_queue_retrieve_next_request(self.handle)
    }
}

// ---------------------------------------------------------------------------------------------------------
// Pageable functions
// ---------------------------------------------------------------------------------------------------------

/// Proof that the code holding it is running in this driver's pageable section.
///
/// Constructed by exactly one thing: the expansion of [`paged_fn!`], which also emits the `#[link_section]`
/// that puts the function there. That is the point — the C driver's `PAGED_CODE()` is a statement about where
/// the function *is*, and the only way to keep the statement true is to make writing it the same act as
/// placing it.
pub struct PagedGuard(());

impl PagedGuard {
    /// # Safety
    ///
    /// Only [`paged_fn!`] may call this, because only its expansion has already placed the caller in `PAGE`.
    /// `pub` rather than `pub(crate)` so the macro can reach it from the driver module; the doc comment is
    /// the contract, and the macro is the only caller in the crate.
    #[doc(hidden)]
    #[inline(always)]
    pub unsafe fn new() -> Self {
        // The WDK's PAGED_CODE() asserts IRQL <= APC_LEVEL — a pageable function may not be running at
        // DISPATCH_LEVEL, where the page it lives on could be out. That assertion is a checked-build-only
        // check in the WDK and this reproduces it: `debug_assert!` is compiled out of a release build, which
        // is the only profile this crate links a driver from.
        debug_assert!(unsafe { current_irql() } <= APC_LEVEL, "PAGED_CODE() at raised IRQL");
        Self(())
    }
}

/// `APC_LEVEL`, from `wdm.h`.
const APC_LEVEL: u8 = 1;

#[inline(always)]
unsafe fn current_irql() -> u8 {
    unsafe { KeGetCurrentIrql() as u8 }
}

/// Declare a function that lives in the pageable section and asserts so.
///
/// ```
/// paged_fn! {
///     unsafe fn AhlaStartVhf(ctx: *mut Context) -> NTSTATUS {
///         // ... VhfCreate / VhfStart, PASSIVE_LEVEL only ...
///     }
/// }
/// ```
///
/// Expands to an `unsafe extern "C"` function in `PAGE`, so it can be handed straight to KMDF as an
/// `EVT_*` callback, or called directly.
#[macro_export]
macro_rules! paged_fn {
    (
        $(#[$meta:meta])*
        unsafe fn $name:ident($($arg:ident : $ty:ty),* $(,)?) -> $ret:ty
        $body:block
    ) => {
        $(#[$meta])*
        // `inline(never)` matters: an inlined copy would live in the caller's section and the placement
        // below would mean nothing.
        #[inline(never)]
        #[link_section = "PAGE"]
        pub unsafe extern "C" fn $name($($arg: $ty),*) -> $ret {
            // The guard and the section attribute are the same expansion, which is what makes an assertion
            // outside the pageable section unrepresentable.
            let _paged: $crate::irql::PagedGuard = unsafe { $crate::irql::PagedGuard::new() };
            $body
        }
    };
}
