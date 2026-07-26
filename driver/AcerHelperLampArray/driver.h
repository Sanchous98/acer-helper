/*++

Module Name:

    driver.h

Abstract:

    Device context and prototypes for AcerHelperLampArray.sys — the virtual HID LampArray device that lets
    Windows Dynamic Lighting drive an Acer keyboard through Acer Helper. See public.h for the app-facing
    contract and docs/lamparray.md for the whole design.

--*/

#pragma once

#include <ntddk.h>
#include <wdf.h>
#include <hidport.h>
#include <vhf.h>

#include "public.h"
#include "lamparray.h"

typedef struct _AHLA_CONTEXT
{
    WDFDEVICE   Device;

    //
    // Manual queue holding the app's pending WAIT_FRAME request (at most one in practice). A manual queue is
    // what makes the request cancellable by the framework, so the app's CancelIoEx unblocks it cleanly.
    //
    WDFQUEUE    FrameQueue;

    //
    // Guards everything below except the VHF lifecycle. A spin lock, because the VHF Get/SetFeature callbacks
    // can run at DISPATCH_LEVEL.
    //
    WDFSPINLOCK Lock;

    //
    // Serialises VhfCreate/VhfStart/VhfDelete, which are PASSIVE_LEVEL-only, against a concurrent
    // SET_LAYOUT/STOP/last-handle-close on another thread (the IOCTL queue is parallel).
    //
    WDFWAITLOCK Lifecycle;

    VHFHANDLE   Vhf;
    BOOLEAN     VhfStarted;

    //
    // Open user-mode handles. The app opens two (control + frames); the virtual device is torn down when the
    // count reaches zero, so a crashed app can't leave a lighting device listed that nothing backs.
    //
    LONG        OpenCount;

    AHLA_LAYOUT Layout;

    //
    // Which lamp the next LampAttributesResponse GET describes. The host enumerates by GETting repeatedly, and
    // the spec has the device auto-advance after each response (a SET of the request report overrides it).
    //
    USHORT      NextLampId;

    //
    // TRUE = no host owns the surface (the device is free to paint whatever it likes; for us that means the app
    // keeps its own lighting). A host clears it to take control and sets it again to hand the surface back.
    //
    BOOLEAN     Autonomous;

    ULONG       Sequence;

    //
    // Host writes land in Staging and are only promoted to Published when the host marks a batch complete —
    // so the app never paints a half-updated keyboard (a 4-zone frame arrives as one multi-update report, but
    // a bigger array would take several).
    //
    AHLA_COLOR  Staging[AHLA_MAX_LAMPS];
    AHLA_COLOR  Published[AHLA_MAX_LAMPS];

    //
    // A complete frame the app has not collected yet. Last-one-wins: a newer frame overwrites Published, so a
    // slow app never builds a backlog — it just skips intermediate frames.
    //
    BOOLEAN     FramePending;

} AHLA_CONTEXT, *PAHLA_CONTEXT;

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(AHLA_CONTEXT, AhlaGetContext)

DRIVER_INITIALIZE                   DriverEntry;
EVT_WDF_DRIVER_DEVICE_ADD           AhlaEvtDeviceAdd;
EVT_WDF_DEVICE_CONTEXT_CLEANUP      AhlaEvtDeviceCleanup;
EVT_WDF_DEVICE_FILE_CREATE          AhlaEvtFileCreate;
EVT_WDF_FILE_CLOSE                  AhlaEvtFileClose;
EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL  AhlaEvtIoDeviceControl;
EVT_VHF_ASYNC_OPERATION             AhlaEvtGetFeature;
EVT_VHF_ASYNC_OPERATION             AhlaEvtSetFeature;
