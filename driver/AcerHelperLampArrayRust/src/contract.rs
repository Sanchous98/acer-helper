//! The wire contract with Acer Helper's user-mode side — `driver/AcerHelperLampArray/public.h` and
//! `lamparray.h`, restated in Rust.
//!
//! CHANGE ONE, CHANGE THE OTHER. `Infrastructure/Vendors/Generic/LampArrayTransport.Windows.cs` writes and
//! reads these structures by hand with `BinaryPrimitives` over a `byte[]`, so field order, sizes, packing,
//! field offsets and the three `CTL_CODE` values are all load-bearing. The C header guards its half with
//! `C_ASSERT`; this module guards its half with `const _: () = assert!(...)`, which is the same tripwire
//! moved to where the Rust driver would be able to break it. A change to either side that the other does not
//! make is a compile error here rather than a driver that misreads the app's buffers at runtime — which
//! matters more than usual, because this driver cannot be loaded and exercised on the machine that builds it.

use core::ffi::{c_long, c_uchar, c_ulong, c_ushort};

// ---------------------------------------------------------------------------------------------------------
// public.h
// ---------------------------------------------------------------------------------------------------------

/// `AHLA_HARDWARE_ID` / the INF's hardware id. The app creates the device node itself (`SwDeviceCreate`), so
/// the virtual LampArray exists only while Acer Helper is running and has the feature switched on.
pub const AHLA_HARDWARE_ID: &str = "AcerHelperLampArray";

/// `AHLA_SYMBOLIC_LINK` — what user mode opens as `\\.\AcerHelperLampArray`.
pub const AHLA_SYMBOLIC_LINK: &str = "\\DosDevices\\AcerHelperLampArray";

/// `AHLA_MAX_LAMPS`. 64 keeps the fixed-size IOCTL buffers small (a layout is ~1.8 KB, a frame ~0.3 KB) while
/// leaving room for per-key hardware.
pub const AHLA_MAX_LAMPS: usize = 64;

pub const AHLA_LAYOUT_VERSION: c_ulong = 1;

/// Reported on the virtual HID device. Not a claim on anyone's USB vendor id — the device never appears on a
/// bus — but changing them changes the child hardware ids, which re-triggers PnP install.
pub const AHLA_VENDOR_ID: c_ushort = 0x1209;
pub const AHLA_PRODUCT_ID: c_ushort = 0xACE1;
pub const AHLA_VERSION: c_ushort = 0x0100;

/// One lamp, in the units the HID LampArray reports use: micrometres from the device's top-left corner,
/// microseconds of latency.
#[repr(C)]
#[derive(Clone, Copy)]
pub struct AhlaLamp {
    pub position_x_um: c_ulong,
    pub position_y_um: c_ulong,
    pub position_z_um: c_ulong,
    pub update_latency_us: c_ulong,
    /// `LampPurposes` bit field.
    pub purposes: c_ulong,
    /// 255 for 8-bit-per-channel LEDs.
    pub red_levels: c_uchar,
    pub green_levels: c_uchar,
    pub blue_levels: c_uchar,
    /// 1 = no independent gain; the host bakes brightness into RGB.
    pub intensity_levels: c_uchar,
    pub is_programmable: c_uchar,
    /// HID usage of the key this lamp sits under; 0 for zone lamps.
    pub input_binding: c_uchar,
    /// Keeps the struct 4-byte aligned and the layout explicit.
    pub reserved: [c_uchar; 2],
}

/// `IOCTL_AHLA_SET_LAYOUT` payload: the whole lamp table in one shot. Lamp ids are implicit (index 0..N-1),
/// which is what the HID host expects when it enumerates by walking ids upwards.
#[repr(C)]
#[derive(Clone, Copy)]
pub struct AhlaLayout {
    pub version: c_ulong,
    pub lamp_count: c_ulong,
    pub bounding_box_width_um: c_ulong,
    pub bounding_box_height_um: c_ulong,
    pub bounding_box_depth_um: c_ulong,
    /// `LampArrayKind` (1 = Keyboard, 7 = Chassis, ...).
    pub kind: c_ulong,
    /// Advertised to the host as the fastest it should push frames.
    pub min_update_interval_us: c_ulong,
    pub lamps: [AhlaLamp; AHLA_MAX_LAMPS],
}

#[repr(C)]
#[derive(Clone, Copy)]
pub struct AhlaColor {
    pub red: c_uchar,
    pub green: c_uchar,
    pub blue: c_uchar,
    pub intensity: c_uchar,
}

/// `IOCTL_AHLA_WAIT_FRAME` payload: one COMPLETE frame. The host sets an "update complete" flag on the last
/// report of a batch; partial batches are never handed up, so the app never paints a half-updated keyboard.
#[repr(C)]
#[derive(Clone, Copy)]
pub struct AhlaFrame {
    /// The order the driver published frames in. It is a plain counter: nothing on the app side consumes it,
    /// and nothing in the transport reports a gap — the app paints the newest frame it gets and the driver
    /// overwrites an uncollected one, so a dropped frame is invisible by design rather than reported. Kept
    /// for the wire format's sake and for a debugger; see `LampArrayTransport.DecodeFrame`, which decodes it
    /// into `LampFrame.Sequence` and stops there.
    pub sequence: c_ulong,
    /// 1 = the host released the surface; the app should take its lighting back.
    pub autonomous_mode: c_ulong,
    pub lamp_count: c_ulong,
    pub colors: [AhlaColor; AHLA_MAX_LAMPS],
}

/// `AHLA_DEVICE_TYPE` — vendor range.
pub const AHLA_DEVICE_TYPE: c_ulong = 0xB007;

const FILE_READ_ACCESS: c_ulong = 0x0001;
const FILE_WRITE_ACCESS: c_ulong = 0x0002;
const METHOD_BUFFERED: c_ulong = 0;

/// `CTL_CODE(DeviceType, Function, Method, Access)`, from `wdm.h`, spelled out so the Rust constants are
/// computed the same way the C ones are rather than transcribed as magic numbers. The app's
/// `LampArrayTransport.Ctl` is the same expression.
const fn ctl_code(device_type: c_ulong, function: c_ulong, method: c_ulong, access: c_ulong) -> c_ulong {
    (device_type << 16) | (access << 14) | (function << 2) | method
}

/// Publishes the virtual device, or re-publishes it with a new layout.
pub const IOCTL_AHLA_SET_LAYOUT: c_ulong = ctl_code(AHLA_DEVICE_TYPE, 0x800, METHOD_BUFFERED, FILE_WRITE_ACCESS);
/// Pends until the next complete frame; last-one-wins, never a backlog.
pub const IOCTL_AHLA_WAIT_FRAME: c_ulong = ctl_code(AHLA_DEVICE_TYPE, 0x801, METHOD_BUFFERED, FILE_READ_ACCESS);
/// Un-publishes the device (also happens when the last handle closes).
pub const IOCTL_AHLA_STOP: c_ulong = ctl_code(AHLA_DEVICE_TYPE, 0x802, METHOD_BUFFERED, FILE_WRITE_ACCESS);

// ---------------------------------------------------------------------------------------------------------
// lamparray.h — the HID LampArray wire format
// ---------------------------------------------------------------------------------------------------------

/// GET: device-wide attributes (lamp count, bounding box, kind).
pub const AHLA_REPORT_ATTRIBUTES: c_uchar = 1;
/// SET: select which lamp the next attributes GET describes.
pub const AHLA_REPORT_ATTR_REQUEST: c_uchar = 2;
/// GET: the selected lamp's attributes (then auto-advances).
pub const AHLA_REPORT_ATTR_RESPONSE: c_uchar = 3;
/// SET: up to 8 (lamp id, colour) pairs.
pub const AHLA_REPORT_MULTI_UPDATE: c_uchar = 4;
/// SET: one colour for a contiguous id range.
pub const AHLA_REPORT_RANGE_UPDATE: c_uchar = 5;
/// SET: autonomous mode on/off (the host takes or releases the surface).
pub const AHLA_REPORT_CONTROL: c_uchar = 6;

pub const AHLA_MULTI_UPDATE_LAMP_COUNT: usize = 8;
pub const AHLA_UPDATE_FLAG_COMPLETE: c_uchar = 1;

// These six mirror lamparray.h's `#include <pshpack1.h>` block: packed, so the sizes below are the wire
// sizes and not the compiler's idea of a comfortable layout.

#[repr(C, packed)]
#[derive(Clone, Copy)]
pub struct AhlaAttributesReport {
    pub report_id: c_uchar,
    pub lamp_count: c_ushort,
    pub bounding_box_width_in_micrometers: c_ulong,
    pub bounding_box_height_in_micrometers: c_ulong,
    pub bounding_box_depth_in_micrometers: c_ulong,
    pub lamp_array_kind: c_ulong,
    pub min_update_interval_in_microseconds: c_ulong,
}

#[repr(C, packed)]
#[derive(Clone, Copy)]
pub struct AhlaAttrRequestReport {
    pub report_id: c_uchar,
    pub lamp_id: c_ushort,
}

#[repr(C, packed)]
#[derive(Clone, Copy)]
pub struct AhlaAttrResponseReport {
    pub report_id: c_uchar,
    pub lamp_id: c_ushort,
    pub position_x_in_micrometers: c_ulong,
    pub position_y_in_micrometers: c_ulong,
    pub position_z_in_micrometers: c_ulong,
    pub update_latency_in_microseconds: c_ulong,
    pub lamp_purposes: c_ulong,
    pub red_level_count: c_uchar,
    pub green_level_count: c_uchar,
    pub blue_level_count: c_uchar,
    pub intensity_level_count: c_uchar,
    pub is_programmable: c_uchar,
    pub input_binding: c_uchar,
}

#[repr(C, packed)]
#[derive(Clone, Copy)]
pub struct AhlaHidColor {
    pub red_channel: c_uchar,
    pub green_channel: c_uchar,
    pub blue_channel: c_uchar,
    pub intensity_channel: c_uchar,
}

#[repr(C, packed)]
#[derive(Clone, Copy)]
pub struct AhlaMultiUpdateReport {
    pub report_id: c_uchar,
    pub lamp_count: c_uchar,
    pub lamp_update_flags: c_uchar,
    pub lamp_ids: [c_ushort; AHLA_MULTI_UPDATE_LAMP_COUNT],
    pub update_colors: [AhlaHidColor; AHLA_MULTI_UPDATE_LAMP_COUNT],
}

#[repr(C, packed)]
#[derive(Clone, Copy)]
pub struct AhlaRangeUpdateReport {
    pub report_id: c_uchar,
    pub lamp_update_flags: c_uchar,
    pub lamp_id_start: c_ushort,
    pub lamp_id_end: c_ushort,
    pub update_color: AhlaHidColor,
}

#[repr(C, packed)]
#[derive(Clone, Copy)]
pub struct AhlaControlReport {
    pub report_id: c_uchar,
    pub autonomous_mode: c_uchar,
}

// ---------------------------------------------------------------------------------------------------------
// The tripwires
// ---------------------------------------------------------------------------------------------------------
//
// Each of these is the `C_ASSERT` from public.h or lamparray.h, restated. `offset_of!` is used wherever the
// C# side indexes a byte offset by hand — those are the numbers `LampArrayTransport.Serialize` and
// `DecodeFrame` hard-code, so an offset that drifted would corrupt every layout and every frame.

const _: () = {
    use core::mem::{offset_of, size_of};

    // public.h
    assert!(size_of::<AhlaLamp>() == 28);
    assert!(offset_of!(AhlaLamp, position_x_um) == 0);
    assert!(offset_of!(AhlaLamp, position_y_um) == 4);
    assert!(offset_of!(AhlaLamp, position_z_um) == 8);
    assert!(offset_of!(AhlaLamp, update_latency_us) == 12);
    assert!(offset_of!(AhlaLamp, purposes) == 16);
    // LampArrayTransport.Serialize writes 255,255,255,1 at o+20..o+23 and IsProgrammable at o+24,
    // InputBinding at o+25 — so these five offsets are named by the app's byte indices, not by taste.
    assert!(offset_of!(AhlaLamp, red_levels) == 20);
    assert!(offset_of!(AhlaLamp, intensity_levels) == 23);
    assert!(offset_of!(AhlaLamp, is_programmable) == 24);
    assert!(offset_of!(AhlaLamp, input_binding) == 25);
    assert!(offset_of!(AhlaLamp, reserved) == 26);

    assert!(size_of::<AhlaLayout>() == 28 + AHLA_MAX_LAMPS * 28);
    assert!(offset_of!(AhlaLayout, lamps) == 28);
    assert!(offset_of!(AhlaLayout, min_update_interval_us) == 24);

    assert!(size_of::<AhlaColor>() == 4);

    assert!(size_of::<AhlaFrame>() == 12 + AHLA_MAX_LAMPS * 4);
    assert!(offset_of!(AhlaFrame, sequence) == 0);
    assert!(offset_of!(AhlaFrame, autonomous_mode) == 4);
    assert!(offset_of!(AhlaFrame, lamp_count) == 8);
    assert!(offset_of!(AhlaFrame, colors) == 12);

    // The three IOCTL codes, against the values `LampArrayTransport.Windows.cs` reaches by its own route.
    // That file does not call CTL_CODE — it reimplements it in C# as
    // `Ctl(function, access) => (DeviceType << 16) | (access << 14) | (function << 2)` with the access
    // constants 1 and 2 — so the numbers below are what the app actually sends, written out. The `ctl_code`
    // calls above are the same expression as the C macro's; these assertions say what it evaluates to.
    assert!(IOCTL_AHLA_SET_LAYOUT == 0xB007_A000);
    assert!(IOCTL_AHLA_WAIT_FRAME == 0xB007_6004);
    assert!(IOCTL_AHLA_STOP == 0xB007_A008);
    // The device path and the hardware id the app opens and creates. `AHLA_SYMBOLIC_LINK` is the kernel-side
    // spelling and `\\.\AcerHelperLampArray` is what CreateFileW is given; both are asserted in lib.rs.
    assert!(AHLA_MAX_LAMPS == 64);
    assert!(AHLA_LAYOUT_VERSION == 1);

    // lamparray.h. The `+ 1` is the leading report id byte the descriptor promises.
    assert!(size_of::<AhlaAttributesReport>() == 1 + 22);
    assert!(size_of::<AhlaAttrRequestReport>() == 1 + 2);
    assert!(size_of::<AhlaAttrResponseReport>() == 1 + 28);
    assert!(size_of::<AhlaMultiUpdateReport>() == 1 + 50);
    assert!(size_of::<AhlaRangeUpdateReport>() == 1 + 9);
    assert!(size_of::<AhlaControlReport>() == 1 + 1);
};

// ---------------------------------------------------------------------------------------------------------
// The report descriptor
// ---------------------------------------------------------------------------------------------------------

/// The static HID report descriptor handed to VHF, taken verbatim from `lamparray.h`.
///
/// It is Microsoft's canonical LampArray descriptor — generated by their WaratahCmd tool and published in
/// github.com/microsoft/ArduinoHidForWindows (MIT, Copyright (c) Microsoft Corporation) — reused unchanged on
/// purpose: it is what Windows' own LampArray sample devices report, so the driver is tested against the same
/// bytes the OS is known to accept. Note that it is COMPLETELY STATIC: lamp count, geometry and kind travel
/// in the *attributes report*, not here, which is why a new layout from the app never regenerates it.
///
/// It lives in this file, beside the report structs it describes, rather than in the driver: the descriptor
/// and the struct sizes are one contract, and the assertion below is what keeps them together.
pub const AHLA_REPORT_DESCRIPTOR: [u8; 292] = [
    0x05, 0x59, // UsagePage(Lighting And Illumination[0x0059])
    0x09, 0x01, // UsageId(LampArray[0x0001])
    0xA1, 0x01, // Collection(Application)
    0x85, 0x01, //     ReportId(1)
    0x09, 0x02, //     UsageId(LampArrayAttributesReport[0x0002])
    0xA1, 0x02, //     Collection(Logical)
    0x09, 0x03, //         UsageId(LampCount[0x0003])
    0x15, 0x00, //         LogicalMinimum(0)
    0x27, 0xFF, 0xFF, 0x00, 0x00, //         LogicalMaximum(65,535)
    0x95, 0x01, //         ReportCount(1)
    0x75, 0x10, //         ReportSize(16)
    0xB1, 0x03, //         Feature(Constant, Variable, Absolute)
    0x09, 0x04, //         UsageId(BoundingBoxWidthInMicrometers[0x0004])
    0x09, 0x05, //         UsageId(BoundingBoxHeightInMicrometers[0x0005])
    0x09, 0x06, //         UsageId(BoundingBoxDepthInMicrometers[0x0006])
    0x09, 0x07, //         UsageId(LampArrayKind[0x0007])
    0x09, 0x08, //         UsageId(MinUpdateIntervalInMicroseconds[0x0008])
    0x27, 0xFF, 0xFF, 0xFF, 0x7F, //         LogicalMaximum(2,147,483,647)
    0x95, 0x05, //         ReportCount(5)
    0x75, 0x20, //         ReportSize(32)
    0xB1, 0x03, //         Feature(Constant, Variable, Absolute)
    0xC0, //     EndCollection()
    0x85, 0x02, //     ReportId(2)
    0x09, 0x20, //     UsageId(LampAttributesRequestReport[0x0020])
    0xA1, 0x02, //     Collection(Logical)
    0x09, 0x21, //         UsageId(LampId[0x0021])
    0x27, 0xFF, 0xFF, 0x00, 0x00, //         LogicalMaximum(65,535)
    0x95, 0x01, //         ReportCount(1)
    0x75, 0x10, //         ReportSize(16)
    0xB1, 0x02, //         Feature(Data, Variable, Absolute)
    0xC0, //     EndCollection()
    0x85, 0x03, //     ReportId(3)
    0x09, 0x22, //     UsageId(LampAttributesResponseReport[0x0022])
    0xA1, 0x02, //     Collection(Logical)
    0x09, 0x21, //         UsageId(LampId[0x0021])
    0xB1, 0x02, //         Feature(Data, Variable, Absolute)
    0x09, 0x23, //         UsageId(PositionXInMicrometers[0x0023])
    0x09, 0x24, //         UsageId(PositionYInMicrometers[0x0024])
    0x09, 0x25, //         UsageId(PositionZInMicrometers[0x0025])
    0x09, 0x27, //         UsageId(UpdateLatencyInMicroseconds[0x0027])
    0x09, 0x26, //         UsageId(LampPurposes[0x0026])
    0x27, 0xFF, 0xFF, 0xFF, 0x7F, //         LogicalMaximum(2,147,483,647)
    0x95, 0x05, //         ReportCount(5)
    0x75, 0x20, //         ReportSize(32)
    0xB1, 0x02, //         Feature(Data, Variable, Absolute)
    0x09, 0x28, //         UsageId(RedLevelCount[0x0028])
    0x09, 0x29, //         UsageId(GreenLevelCount[0x0029])
    0x09, 0x2A, //         UsageId(BlueLevelCount[0x002A])
    0x09, 0x2B, //         UsageId(IntensityLevelCount[0x002B])
    0x09, 0x2C, //         UsageId(IsProgrammable[0x002C])
    0x09, 0x2D, //         UsageId(InputBinding[0x002D])
    0x26, 0xFF, 0x00, //         LogicalMaximum(255)
    0x95, 0x06, //         ReportCount(6)
    0x75, 0x08, //         ReportSize(8)
    0xB1, 0x02, //         Feature(Data, Variable, Absolute)
    0xC0, //     EndCollection()
    0x85, 0x04, //     ReportId(4)
    0x09, 0x50, //     UsageId(LampMultiUpdateReport[0x0050])
    0xA1, 0x02, //     Collection(Logical)
    0x09, 0x03, //         UsageId(LampCount[0x0003])
    0x25, 0x08, //         LogicalMaximum(8)
    0x95, 0x01, //         ReportCount(1)
    0xB1, 0x02, //         Feature(Data, Variable, Absolute)
    0x09, 0x55, //         UsageId(LampUpdateFlags[0x0055])
    0x25, 0x01, //         LogicalMaximum(1)
    0xB1, 0x02, //         Feature(Data, Variable, Absolute)
    0x09, 0x21, //         UsageId(LampId[0x0021])
    0x27, 0xFF, 0xFF, 0x00, 0x00, //         LogicalMaximum(65,535)
    0x95, 0x08, //         ReportCount(8)
    0x75, 0x10, //         ReportSize(16)
    0xB1, 0x02, //         Feature(Data, Variable, Absolute)
    0x09, 0x51, //         UsageId(RedUpdateChannel[0x0051])
    0x09, 0x52, //         UsageId(GreenUpdateChannel[0x0052])
    0x09, 0x53, //         UsageId(BlueUpdateChannel[0x0053])
    0x09, 0x54, //         UsageId(IntensityUpdateChannel[0x0054])
    0x09, 0x51, 0x09, 0x52, 0x09, 0x53, 0x09, 0x54, //         ... x4 more channel groups,
    0x09, 0x51, 0x09, 0x52, 0x09, 0x53, 0x09, 0x54, //         one per lamp slot in the report
    0x09, 0x51, 0x09, 0x52, 0x09, 0x53, 0x09, 0x54,
    0x09, 0x51, 0x09, 0x52, 0x09, 0x53, 0x09, 0x54,
    0x09, 0x51, 0x09, 0x52, 0x09, 0x53, 0x09, 0x54,
    0x09, 0x51, 0x09, 0x52, 0x09, 0x53, 0x09, 0x54,
    0x09, 0x51, 0x09, 0x52, 0x09, 0x53, 0x09, 0x54,
    0x26, 0xFF, 0x00, //         LogicalMaximum(255)
    0x95, 0x20, //         ReportCount(32)
    0x75, 0x08, //         ReportSize(8)
    0xB1, 0x02, //         Feature(Data, Variable, Absolute)
    0xC0, //     EndCollection()
    0x85, 0x05, //     ReportId(5)
    0x09, 0x60, //     UsageId(LampRangeUpdateReport[0x0060])
    0xA1, 0x02, //     Collection(Logical)
    0x09, 0x55, //         UsageId(LampUpdateFlags[0x0055])
    0x25, 0x01, //         LogicalMaximum(1)
    0x95, 0x01, //         ReportCount(1)
    0xB1, 0x02, //         Feature(Data, Variable, Absolute)
    0x09, 0x61, //         UsageId(LampIdStart[0x0061])
    0x09, 0x62, //         UsageId(LampIdEnd[0x0062])
    0x27, 0xFF, 0xFF, 0x00, 0x00, //         LogicalMaximum(65,535)
    0x95, 0x02, //         ReportCount(2)
    0x75, 0x10, //         ReportSize(16)
    0xB1, 0x02, //         Feature(Data, Variable, Absolute)
    0x09, 0x51, //         UsageId(RedUpdateChannel[0x0051])
    0x09, 0x52, //         UsageId(GreenUpdateChannel[0x0052])
    0x09, 0x53, //         UsageId(BlueUpdateChannel[0x0053])
    0x09, 0x54, //         UsageId(IntensityUpdateChannel[0x0054])
    0x26, 0xFF, 0x00, //         LogicalMaximum(255)
    0x95, 0x04, //         ReportCount(4)
    0x75, 0x08, //         ReportSize(8)
    0xB1, 0x02, //         Feature(Data, Variable, Absolute)
    0xC0, //     EndCollection()
    0x85, 0x06, //     ReportId(6)
    0x09, 0x70, //     UsageId(LampArrayControlReport[0x0070])
    0xA1, 0x02, //     Collection(Logical)
    0x09, 0x71, //         UsageId(AutonomousMode[0x0071])
    0x25, 0x01, //         LogicalMaximum(1)
    0x95, 0x01, //         ReportCount(1)
    0xB1, 0x02, //         Feature(Data, Variable, Absolute)
    0xC0, //     EndCollection()
    0xC0, // EndCollection()
];

const _: () = assert!(AHLA_REPORT_DESCRIPTOR.len() == 292);

/// The `NTSTATUS` of the WDK is a `LONG`, i.e. 32-bit on Windows. `c_long` from `core::ffi` is 32-bit when
/// the crate is compiled for `x86_64-pc-windows-msvc`, which is the only target this crate builds for — this
/// assertion is here to say so out loud, because on the Linux host `c_long` is 64-bit and a build that forgot
/// its target would silently produce 64-bit statuses and 64-bit `ULONG` fields everywhere.
const _: () = assert!(core::mem::size_of::<c_long>() == 4);
