/*++

Module Name:

    public.h

Abstract:

    The contract between AcerHelperLampArray.sys and Acer Helper's user-mode side
    (Vendors/Generic/LampArrayTransport.Windows.cs). CHANGE ONE, CHANGE THE OTHER: the C# transport writes and
    reads these structures by hand (BinaryPrimitives over a byte[]), so the field order, the sizes and the
    IOCTL codes below are all load-bearing. The C_ASSERTs at the bottom are the tripwire for the C side; the
    C# side names the same numbers as constants.

    Design note: the driver deliberately owns as little as possible. It answers the host's *attribute* queries
    (which lamps exist, where they are) out of the layout blob the app pushes down, and it accumulates the
    host's lamp writes into a frame. It knows nothing about zones, Acer hardware, rate limiting or ownership —
    all of that is C# (Features/LampArrayBridge.cs), which is what keeps this signed binary stable.

--*/

#pragma once

//
// Hardware id matched by AcerHelperLampArray.inf. The app creates the device node itself at runtime
// (SwDeviceCreate), so the virtual LampArray only exists while Acer Helper is running and has the feature
// enabled — no stale lighting device left listed in Windows Settings.
//
#define AHLA_HARDWARE_ID        L"AcerHelperLampArray"

//
// Control device the app opens (user mode: \\.\AcerHelperLampArray).
//
#define AHLA_SYMBOLIC_LINK      L"\\DosDevices\\AcerHelperLampArray"

//
// Upper bound on lamps. Real laptops here need 4..9 (keyboard zones + a lightbar); 64 keeps the fixed-size
// IOCTL buffers small (a layout is ~1.8 KB, a frame ~0.3 KB) while leaving room for per-key hardware.
//
#define AHLA_MAX_LAMPS          64

#define AHLA_LAYOUT_VERSION     1

//
// Reported on the virtual HID device. These are NOT a claim on anyone's USB vendor id: the device never
// appears on a bus, and nothing but the child devnode's ids and Device Manager's display use them. Keep them
// stable anyway — changing them changes the child hardware ids, which re-triggers PnP install.
//
#define AHLA_VENDOR_ID          0x1209
#define AHLA_PRODUCT_ID         0xACE1
#define AHLA_VERSION            0x0100

//
// One lamp, in the units the HID LampArray reports use: micrometres from the top-left corner of the device,
// microseconds of latency. Level counts are 8-bit because the HID fields are (LogicalMaximum(255)).
//
typedef struct _AHLA_LAMP
{
    ULONG PositionXUm;
    ULONG PositionYUm;
    ULONG PositionZUm;
    ULONG UpdateLatencyUs;
    ULONG Purposes;             // LampPurposes bit field
    UCHAR RedLevels;            // 255 for 8-bit-per-channel LEDs
    UCHAR GreenLevels;
    UCHAR BlueLevels;
    UCHAR IntensityLevels;      // 1 = no independent gain; the host bakes brightness into RGB
    UCHAR IsProgrammable;
    UCHAR InputBinding;         // HID usage of the key this lamp sits under; 0 for zone lamps
    UCHAR Reserved[2];          // keep the struct 4-byte aligned and the layout explicit
} AHLA_LAMP;

//
// IOCTL_AHLA_SET_LAYOUT payload: the whole lamp table in one shot. Lamp ids are implicit (index 0..N-1),
// which is what the HID host expects to be able to enumerate by walking ids upwards.
//
typedef struct _AHLA_LAYOUT
{
    ULONG     Version;              // AHLA_LAYOUT_VERSION
    ULONG     LampCount;            // 1..AHLA_MAX_LAMPS
    ULONG     BoundingBoxWidthUm;
    ULONG     BoundingBoxHeightUm;
    ULONG     BoundingBoxDepthUm;
    ULONG     Kind;                 // LampArrayKind (1 = Keyboard, 7 = Chassis, ...)
    ULONG     MinUpdateIntervalUs;  // advertised to the host as the fastest it should push frames
    AHLA_LAMP Lamps[AHLA_MAX_LAMPS];
} AHLA_LAYOUT;

typedef struct _AHLA_COLOR
{
    UCHAR Red;
    UCHAR Green;
    UCHAR Blue;
    UCHAR Intensity;
} AHLA_COLOR;

//
// IOCTL_AHLA_WAIT_FRAME payload: one COMPLETE frame (the host sets an "update complete" flag on the last
// report of a batch; partial batches are never handed up, so the app never paints a half-updated keyboard).
// Sequence lets the app see that frames were dropped — which is normal and fine, it only paints the newest.
//
typedef struct _AHLA_FRAME
{
    ULONG      Sequence;
    ULONG      AutonomousMode;      // 1 = the host released the surface; the app should take its lighting back
    ULONG      LampCount;
    AHLA_COLOR Colors[AHLA_MAX_LAMPS];
} AHLA_FRAME;

//
// IOCTLs. Vendor device type; buffered, because every payload is a small fixed-size struct.
//
//   SET_LAYOUT   in:  AHLA_LAYOUT   publishes the virtual device (or re-publishes it with a new layout)
//   WAIT_FRAME   out: AHLA_FRAME    pends until the next complete frame; last-one-wins, never a backlog
//   STOP         -                  un-publishes the device (also happens when the last handle closes)
//
#define AHLA_DEVICE_TYPE        0xB007

#define IOCTL_AHLA_SET_LAYOUT   CTL_CODE(AHLA_DEVICE_TYPE, 0x800, METHOD_BUFFERED, FILE_WRITE_ACCESS)
#define IOCTL_AHLA_WAIT_FRAME   CTL_CODE(AHLA_DEVICE_TYPE, 0x801, METHOD_BUFFERED, FILE_READ_ACCESS)
#define IOCTL_AHLA_STOP         CTL_CODE(AHLA_DEVICE_TYPE, 0x802, METHOD_BUFFERED, FILE_WRITE_ACCESS)

//
// The user-mode side hard-codes these sizes; a mismatch here is a wire-format break, so fail the build.
//
C_ASSERT(sizeof(AHLA_LAMP) == 28);
C_ASSERT(sizeof(AHLA_LAYOUT) == 28 + AHLA_MAX_LAMPS * 28);
C_ASSERT(sizeof(AHLA_COLOR) == 4);
C_ASSERT(sizeof(AHLA_FRAME) == 12 + AHLA_MAX_LAMPS * 4);
