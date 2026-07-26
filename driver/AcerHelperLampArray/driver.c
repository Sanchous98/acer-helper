/*++

Module Name:

    driver.c

Abstract:

    AcerHelperLampArray.sys — a KMDF HID *source* driver that publishes a virtual HID LampArray device through
    the in-box Virtual HID Framework (vhf.sys, declared as a lower filter in the INF). That is the only way to
    make a lighting device visible to Windows Dynamic Lighting: the OS enumerates LampArray *HID collections*,
    and there is no user-mode API to register one. It is the same construction Logitech ships for LIGHTSYNC
    hardware (logi_lamparray.sys + "LowerFilters"={vhf, logi_lamparray} + a user-mode translation service).

    Division of labour (see public.h): this driver holds the static report descriptor, answers the host's
    attribute interrogation out of a layout the app pushes down, accumulates the host's lamp writes into whole
    frames, and hands those frames up. It knows nothing about Acer hardware, zones, rate limiting or who owns
    the backlight — that all lives in the app (Features/LampArrayBridge.cs), so this binary stays small and
    stable, which matters when every change means re-signing.

--*/

#include "driver.h"

static NTSTATUS AhlaStartVhf(_In_ PAHLA_CONTEXT Ctx);
static VOID     AhlaStopVhf(_In_ PAHLA_CONTEXT Ctx);
static VOID     AhlaFlushFrame(_In_ PAHLA_CONTEXT Ctx);
static VOID     AhlaBuildFrame(_In_ PAHLA_CONTEXT Ctx, _Out_ AHLA_FRAME* Frame);
static VOID     AhlaPublish(_In_ PAHLA_CONTEXT Ctx);

#ifdef ALLOC_PRAGMA
#pragma alloc_text (INIT, DriverEntry)
#pragma alloc_text (PAGE, AhlaEvtDeviceAdd)
#endif

NTSTATUS
DriverEntry(
    _In_ PDRIVER_OBJECT  DriverObject,
    _In_ PUNICODE_STRING RegistryPath
    )
{
    WDF_DRIVER_CONFIG config;

    WDF_DRIVER_CONFIG_INIT(&config, AhlaEvtDeviceAdd);

    return WdfDriverCreate(DriverObject, RegistryPath, WDF_NO_OBJECT_ATTRIBUTES, &config, WDF_NO_HANDLE);
}

NTSTATUS
AhlaEvtDeviceAdd(
    _In_    WDFDRIVER       Driver,
    _Inout_ PWDFDEVICE_INIT DeviceInit
    )
{
    NTSTATUS              status;
    WDFDEVICE             device;
    PAHLA_CONTEXT         ctx;
    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_FILEOBJECT_CONFIG fileConfig;
    WDF_IO_QUEUE_CONFIG   queueConfig;
    DECLARE_CONST_UNICODE_STRING(symbolicLink, AHLA_SYMBOLIC_LINK);

    UNREFERENCED_PARAMETER(Driver);
    PAGED_CODE();

    //
    // We need file-object callbacks: the virtual device must exist only while the app holds a handle, so that
    // an app crash or a plain exit removes it from the OS's lighting device list instead of leaving a dead
    // entry the user can select in Settings.
    //
    WDF_FILEOBJECT_CONFIG_INIT(&fileConfig, AhlaEvtFileCreate, AhlaEvtFileClose, WDF_NO_EVENT_CALLBACK);
    WdfDeviceInitSetFileObjectConfig(DeviceInit, &fileConfig, WDF_NO_OBJECT_ATTRIBUTES);
    WdfDeviceInitSetIoType(DeviceInit, WdfDeviceIoBuffered);

    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, AHLA_CONTEXT);
    attributes.EvtCleanupCallback = AhlaEvtDeviceCleanup;
    //
    // Passive execution level for the device (and everything that inherits from it): the cleanup callback
    // calls VhfDelete and the IOCTL path calls VhfCreate/VhfStart, all of which are PASSIVE_LEVEL-only. Nothing
    // here is on a performance path, so there is no reason to run any of it at DISPATCH.
    //
    attributes.ExecutionLevel = WdfExecutionLevelPassive;

    status = WdfDeviceCreate(&DeviceInit, &attributes, &device);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    ctx = AhlaGetContext(device);
    RtlZeroMemory(ctx, sizeof(AHLA_CONTEXT));
    ctx->Device = device;
    ctx->Autonomous = TRUE;   // nothing owns the surface until a host says otherwise

    status = WdfSpinLockCreate(WDF_NO_OBJECT_ATTRIBUTES, &ctx->Lock);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    status = WdfWaitLockCreate(WDF_NO_OBJECT_ATTRIBUTES, &ctx->Lifecycle);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    status = WdfDeviceCreateSymbolicLink(device, &symbolicLink);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    //
    // Default queue: PARALLEL, and forced to PASSIVE_LEVEL because SET_LAYOUT calls VhfCreate/VhfStart, which
    // are PASSIVE-only. Parallel matters because WAIT_FRAME pends for as long as the host is idle; a sequential
    // queue would make the app's STOP wait behind it.
    //
    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&queueConfig, WdfIoQueueDispatchParallel);
    queueConfig.EvtIoDeviceControl = AhlaEvtIoDeviceControl;

    WDF_OBJECT_ATTRIBUTES_INIT(&attributes);
    attributes.ExecutionLevel = WdfExecutionLevelPassive;

    status = WdfIoQueueCreate(device, &queueConfig, &attributes, WDF_NO_HANDLE);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    WDF_IO_QUEUE_CONFIG_INIT(&queueConfig, WdfIoQueueDispatchManual);
    status = WdfIoQueueCreate(device, &queueConfig, WDF_NO_OBJECT_ATTRIBUTES, &ctx->FrameQueue);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    //
    // Note: the virtual HID device is NOT created here. It appears on the first SET_LAYOUT, because its
    // attributes (how many lamps, where they are) come from the app — publishing a LampArray with no lamps
    // would show the user a lighting device that can't be painted.
    //
    return STATUS_SUCCESS;
}

VOID
AhlaEvtDeviceCleanup(
    _In_ WDFOBJECT Object
    )
{
    PAHLA_CONTEXT ctx = AhlaGetContext((WDFDEVICE)Object);

    PAGED_CODE();
    AhlaStopVhf(ctx);
}

VOID
AhlaEvtFileCreate(
    _In_ WDFDEVICE     Device,
    _In_ WDFREQUEST    Request,
    _In_ WDFFILEOBJECT FileObject
    )
{
    PAHLA_CONTEXT ctx = AhlaGetContext(Device);

    UNREFERENCED_PARAMETER(FileObject);

    InterlockedIncrement(&ctx->OpenCount);
    WdfRequestComplete(Request, STATUS_SUCCESS);
}

VOID
AhlaEvtFileClose(
    _In_ WDFFILEOBJECT FileObject
    )
{
    PAHLA_CONTEXT ctx = AhlaGetContext(WdfFileObjectGetDevice(FileObject));

    PAGED_CODE();

    if (InterlockedDecrement(&ctx->OpenCount) > 0) {
        return;
    }

    //
    // Last handle gone: complete any pending WAIT_FRAME and un-publish the device.
    //
    WdfIoQueuePurgeSynchronously(ctx->FrameQueue);
    WdfIoQueueStart(ctx->FrameQueue);
    AhlaStopVhf(ctx);
}

VOID
AhlaEvtIoDeviceControl(
    _In_ WDFQUEUE   Queue,
    _In_ WDFREQUEST Request,
    _In_ size_t     OutputBufferLength,
    _In_ size_t     InputBufferLength,
    _In_ ULONG      IoControlCode
    )
{
    PAHLA_CONTEXT ctx = AhlaGetContext(WdfIoQueueGetDevice(Queue));
    NTSTATUS      status;
    PVOID         buffer;
    size_t        length;

    UNREFERENCED_PARAMETER(InputBufferLength);

    switch (IoControlCode) {

    case IOCTL_AHLA_SET_LAYOUT:
    {
        AHLA_LAYOUT* layout;

        status = WdfRequestRetrieveInputBuffer(Request, sizeof(AHLA_LAYOUT), &buffer, &length);
        if (!NT_SUCCESS(status)) {
            break;
        }

        layout = (AHLA_LAYOUT*)buffer;
        if (layout->Version != AHLA_LAYOUT_VERSION ||
            layout->LampCount == 0 || layout->LampCount > AHLA_MAX_LAMPS) {
            status = STATUS_INVALID_PARAMETER;
            break;
        }

        //
        // Re-publishing with a different layout means re-enumerating: the host caches the attributes it
        // interrogated (lamp count, positions), so simply swapping the table underneath it would leave Windows
        // painting a device shape that no longer exists. Stop first — a no-op on the usual first call.
        //
        AhlaStopVhf(ctx);

        WdfSpinLockAcquire(ctx->Lock);
        RtlCopyMemory(&ctx->Layout, layout, sizeof(AHLA_LAYOUT));
        RtlZeroMemory(ctx->Staging, sizeof(ctx->Staging));
        RtlZeroMemory(ctx->Published, sizeof(ctx->Published));
        ctx->NextLampId = 0;
        ctx->Autonomous = TRUE;
        ctx->FramePending = FALSE;
        ctx->Sequence = 0;
        WdfSpinLockRelease(ctx->Lock);

        status = AhlaStartVhf(ctx);
        break;
    }

    case IOCTL_AHLA_WAIT_FRAME:
    {
        if (OutputBufferLength < sizeof(AHLA_FRAME)) {
            status = STATUS_BUFFER_TOO_SMALL;
            break;
        }

        //
        // Park the request in the manual queue, then immediately try to service it. Doing it in that order is
        // what closes the lost-wakeup race: a frame published between "we saw no frame" and "the request is
        // queued" would otherwise sit unnoticed until the host happened to send another one.
        //
        status = WdfRequestForwardToIoQueue(Request, ctx->FrameQueue);
        if (!NT_SUCCESS(status)) {
            break;
        }

        AhlaFlushFrame(ctx);
        return;   // the request now belongs to the frame queue
    }

    case IOCTL_AHLA_STOP:
        WdfIoQueuePurgeSynchronously(ctx->FrameQueue);
        WdfIoQueueStart(ctx->FrameQueue);
        AhlaStopVhf(ctx);
        status = STATUS_SUCCESS;
        break;

    default:
        status = STATUS_INVALID_DEVICE_REQUEST;
        break;
    }

    WdfRequestComplete(Request, status);
}

//
// ---- virtual HID device lifecycle ----
//

static NTSTATUS
AhlaStartVhf(
    _In_ PAHLA_CONTEXT Ctx
    )
{
    NTSTATUS   status = STATUS_SUCCESS;
    VHF_CONFIG config;

    PAGED_CODE();

    WdfWaitLockAcquire(Ctx->Lifecycle, NULL);

    if (Ctx->VhfStarted) {
        goto Exit;   // already published (SET_LAYOUT stops first, so this is only a redundant call)
    }

    VHF_CONFIG_INIT(&config,
                    WdfDeviceWdmGetDeviceObject(Ctx->Device),
                    (USHORT)sizeof(AhlaReportDescriptor),
                    (PUCHAR)AhlaReportDescriptor);

    config.VhfClientContext                  = Ctx;
    config.VendorID                          = AHLA_VENDOR_ID;
    config.ProductID                         = AHLA_PRODUCT_ID;
    config.VersionNumber                     = AHLA_VERSION;
    config.EvtVhfAsyncOperationGetFeature    = AhlaEvtGetFeature;
    config.EvtVhfAsyncOperationSetFeature    = AhlaEvtSetFeature;

    status = VhfCreate(&config, &Ctx->Vhf);
    if (!NT_SUCCESS(status)) {
        Ctx->Vhf = NULL;
        goto Exit;
    }

    status = VhfStart(Ctx->Vhf);
    if (!NT_SUCCESS(status)) {
        VhfDelete(Ctx->Vhf, TRUE);
        Ctx->Vhf = NULL;
        goto Exit;
    }

    Ctx->VhfStarted = TRUE;

Exit:
    WdfWaitLockRelease(Ctx->Lifecycle);
    return status;
}

static VOID
AhlaStopVhf(
    _In_ PAHLA_CONTEXT Ctx
    )
{
    PAGED_CODE();

    WdfWaitLockAcquire(Ctx->Lifecycle, NULL);

    if (Ctx->VhfStarted) {
        //
        // Synchronous delete (Wait = TRUE): by the time this returns VHF has stopped invoking our callbacks,
        // so the context is safe to reuse or free.
        //
        VhfDelete(Ctx->Vhf, TRUE);
        Ctx->Vhf = NULL;
        Ctx->VhfStarted = FALSE;
    }

    WdfSpinLockAcquire(Ctx->Lock);
    Ctx->Autonomous = TRUE;
    Ctx->FramePending = FALSE;
    WdfSpinLockRelease(Ctx->Lock);

    WdfWaitLockRelease(Ctx->Lifecycle);
}

//
// ---- frames ----
//

//
// Snapshot the published state into an AHLA_FRAME. Caller holds Ctx->Lock.
//
static VOID
AhlaBuildFrame(
    _In_  PAHLA_CONTEXT Ctx,
    _Out_ AHLA_FRAME*   Frame
    )
{
    RtlZeroMemory(Frame, sizeof(AHLA_FRAME));
    Frame->Sequence = Ctx->Sequence;
    Frame->AutonomousMode = Ctx->Autonomous ? 1 : 0;
    Frame->LampCount = Ctx->Layout.LampCount;
    RtlCopyMemory(Frame->Colors, Ctx->Published, sizeof(Frame->Colors));
}

//
// Promote the staged colours to a complete frame. Caller holds Ctx->Lock. Overwrites any frame the app has not
// collected yet — deliberately: for lighting, the newest state is the only interesting one.
//
static VOID
AhlaPublish(
    _In_ PAHLA_CONTEXT Ctx
    )
{
    RtlCopyMemory(Ctx->Published, Ctx->Staging, sizeof(Ctx->Published));
    Ctx->Sequence++;
    Ctx->FramePending = TRUE;
}

//
// Hand the pending frame (if any) to a waiting app request (if any). Must be called WITHOUT Ctx->Lock held.
//
static VOID
AhlaFlushFrame(
    _In_ PAHLA_CONTEXT Ctx
    )
{
    WDFREQUEST request = NULL;
    AHLA_FRAME frame;
    NTSTATUS   status;
    PVOID      output;
    size_t     length;

    WdfSpinLockAcquire(Ctx->Lock);
    if (Ctx->FramePending &&
        NT_SUCCESS(WdfIoQueueRetrieveNextRequest(Ctx->FrameQueue, &request))) {
        AhlaBuildFrame(Ctx, &frame);
        Ctx->FramePending = FALSE;
    }
    WdfSpinLockRelease(Ctx->Lock);

    if (request == NULL) {
        return;   // nothing to deliver, or nobody waiting (the frame stays pending)
    }

    status = WdfRequestRetrieveOutputBuffer(request, sizeof(AHLA_FRAME), &output, &length);
    if (NT_SUCCESS(status)) {
        RtlCopyMemory(output, &frame, sizeof(AHLA_FRAME));
        WdfRequestCompleteWithInformation(request, STATUS_SUCCESS, sizeof(AHLA_FRAME));
    } else {
        WdfRequestComplete(request, status);
    }
}

//
// ---- HID feature reports ----
//

VOID
AhlaEvtGetFeature(
    _In_     PVOID              VhfClientContext,
    _In_     VHFOPERATIONHANDLE VhfOperationHandle,
    _In_opt_ PVOID              VhfOperationContext,
    _In_     PHID_XFER_PACKET   HidTransferPacket
    )
{
    PAHLA_CONTEXT ctx = (PAHLA_CONTEXT)VhfClientContext;
    NTSTATUS      status = STATUS_INVALID_DEVICE_REQUEST;

    UNREFERENCED_PARAMETER(VhfOperationContext);

    if (HidTransferPacket == NULL || HidTransferPacket->reportBuffer == NULL) {
        VhfAsyncOperationComplete(VhfOperationHandle, STATUS_INVALID_PARAMETER);
        return;
    }

    WdfSpinLockAcquire(ctx->Lock);

    switch (HidTransferPacket->reportId) {

    case AHLA_REPORT_ATTRIBUTES:
    {
        AHLA_ATTRIBUTES_REPORT report;

        if (HidTransferPacket->reportBufferLen < sizeof(report)) {
            status = STATUS_BUFFER_TOO_SMALL;
            break;
        }

        RtlZeroMemory(&report, sizeof(report));
        report.ReportId = AHLA_REPORT_ATTRIBUTES;
        report.LampCount = (USHORT)ctx->Layout.LampCount;
        report.BoundingBoxWidthInMicrometers = ctx->Layout.BoundingBoxWidthUm;
        report.BoundingBoxHeightInMicrometers = ctx->Layout.BoundingBoxHeightUm;
        report.BoundingBoxDepthInMicrometers = ctx->Layout.BoundingBoxDepthUm;
        report.LampArrayKind = ctx->Layout.Kind;
        report.MinUpdateIntervalInMicroseconds = ctx->Layout.MinUpdateIntervalUs;

        RtlCopyMemory(HidTransferPacket->reportBuffer, &report, sizeof(report));
        status = STATUS_SUCCESS;
        break;
    }

    case AHLA_REPORT_ATTR_RESPONSE:
    {
        AHLA_ATTR_RESPONSE_REPORT report;
        const AHLA_LAMP*          lamp;
        USHORT                    id = ctx->NextLampId;

        if (HidTransferPacket->reportBufferLen < sizeof(report)) {
            status = STATUS_BUFFER_TOO_SMALL;
            break;
        }
        if (ctx->Layout.LampCount == 0) {
            status = STATUS_DEVICE_NOT_READY;
            break;
        }
        if (id >= ctx->Layout.LampCount) {
            id = 0;
        }

        lamp = &ctx->Layout.Lamps[id];

        RtlZeroMemory(&report, sizeof(report));
        report.ReportId = AHLA_REPORT_ATTR_RESPONSE;
        report.LampId = id;
        report.PositionXInMicrometers = lamp->PositionXUm;
        report.PositionYInMicrometers = lamp->PositionYUm;
        report.PositionZInMicrometers = lamp->PositionZUm;
        report.UpdateLatencyInMicroseconds = lamp->UpdateLatencyUs;
        report.LampPurposes = lamp->Purposes;
        report.RedLevelCount = lamp->RedLevels;
        report.GreenLevelCount = lamp->GreenLevels;
        report.BlueLevelCount = lamp->BlueLevels;
        report.IntensityLevelCount = lamp->IntensityLevels;
        report.IsProgrammable = lamp->IsProgrammable;
        report.InputBinding = lamp->InputBinding;

        RtlCopyMemory(HidTransferPacket->reportBuffer, &report, sizeof(report));

        //
        // Auto-advance, wrapping: this is how the host walks the whole array with repeated GETs.
        //
        ctx->NextLampId = (USHORT)((id + 1) % ctx->Layout.LampCount);
        status = STATUS_SUCCESS;
        break;
    }

    default:
        break;   // not a GET-able report -> STATUS_INVALID_DEVICE_REQUEST
    }

    WdfSpinLockRelease(ctx->Lock);

    VhfAsyncOperationComplete(VhfOperationHandle, status);
}

VOID
AhlaEvtSetFeature(
    _In_     PVOID              VhfClientContext,
    _In_     VHFOPERATIONHANDLE VhfOperationHandle,
    _In_opt_ PVOID              VhfOperationContext,
    _In_     PHID_XFER_PACKET   HidTransferPacket
    )
{
    PAHLA_CONTEXT ctx = (PAHLA_CONTEXT)VhfClientContext;
    NTSTATUS      status = STATUS_INVALID_DEVICE_REQUEST;
    BOOLEAN       deliver = FALSE;

    UNREFERENCED_PARAMETER(VhfOperationContext);

    if (HidTransferPacket == NULL || HidTransferPacket->reportBuffer == NULL) {
        VhfAsyncOperationComplete(VhfOperationHandle, STATUS_INVALID_PARAMETER);
        return;
    }

    WdfSpinLockAcquire(ctx->Lock);

    switch (HidTransferPacket->reportId) {

    case AHLA_REPORT_ATTR_REQUEST:
    {
        AHLA_ATTR_REQUEST_REPORT report;

        if (HidTransferPacket->reportBufferLen < sizeof(report)) {
            status = STATUS_BUFFER_TOO_SMALL;
            break;
        }
        RtlCopyMemory(&report, HidTransferPacket->reportBuffer, sizeof(report));

        //
        // Per spec, an out-of-range id selects lamp 0 rather than failing.
        //
        ctx->NextLampId = (report.LampId < ctx->Layout.LampCount) ? report.LampId : 0;
        status = STATUS_SUCCESS;
        break;
    }

    case AHLA_REPORT_MULTI_UPDATE:
    {
        AHLA_MULTI_UPDATE_REPORT report;
        UCHAR                    i;

        if (HidTransferPacket->reportBufferLen < sizeof(report)) {
            status = STATUS_BUFFER_TOO_SMALL;
            break;
        }
        RtlCopyMemory(&report, HidTransferPacket->reportBuffer, sizeof(report));

        for (i = 0; i < report.LampCount && i < AHLA_MULTI_UPDATE_LAMP_COUNT; i++) {
            USHORT id = report.LampIds[i];
            if (id < ctx->Layout.LampCount) {
                ctx->Staging[id].Red = report.UpdateColors[i].RedChannel;
                ctx->Staging[id].Green = report.UpdateColors[i].GreenChannel;
                ctx->Staging[id].Blue = report.UpdateColors[i].BlueChannel;
                ctx->Staging[id].Intensity = report.UpdateColors[i].IntensityChannel;
            }
        }

        if (report.LampUpdateFlags & AHLA_UPDATE_FLAG_COMPLETE) {
            AhlaPublish(ctx);
            deliver = TRUE;
        }
        status = STATUS_SUCCESS;
        break;
    }

    case AHLA_REPORT_RANGE_UPDATE:
    {
        AHLA_RANGE_UPDATE_REPORT report;
        ULONG                    id;

        if (HidTransferPacket->reportBufferLen < sizeof(report)) {
            status = STATUS_BUFFER_TOO_SMALL;
            break;
        }
        RtlCopyMemory(&report, HidTransferPacket->reportBuffer, sizeof(report));

        if (report.LampIdStart <= report.LampIdEnd && report.LampIdEnd < ctx->Layout.LampCount) {
            for (id = report.LampIdStart; id <= report.LampIdEnd; id++) {
                ctx->Staging[id].Red = report.UpdateColor.RedChannel;
                ctx->Staging[id].Green = report.UpdateColor.GreenChannel;
                ctx->Staging[id].Blue = report.UpdateColor.BlueChannel;
                ctx->Staging[id].Intensity = report.UpdateColor.IntensityChannel;
            }

            if (report.LampUpdateFlags & AHLA_UPDATE_FLAG_COMPLETE) {
                AhlaPublish(ctx);
                deliver = TRUE;
            }
        }
        status = STATUS_SUCCESS;
        break;
    }

    case AHLA_REPORT_CONTROL:
    {
        AHLA_CONTROL_REPORT report;
        BOOLEAN             autonomous;

        if (HidTransferPacket->reportBufferLen < sizeof(report)) {
            status = STATUS_BUFFER_TOO_SMALL;
            break;
        }
        RtlCopyMemory(&report, HidTransferPacket->reportBuffer, sizeof(report));

        autonomous = report.AutonomousMode ? TRUE : FALSE;
        if (autonomous != ctx->Autonomous) {
            ctx->Autonomous = autonomous;

            //
            // Hand-BACK is published at once, so the app stops waiting and repaints its own lighting instead of
            // leaving the host's last frame frozen on the keyboard. A TAKE-over is deliberately NOT published:
            // the staged colours are still black at that point, and shipping them would flash the keyboard off
            // for one interval before the host's first real frame arrives.
            //
            if (autonomous) {
                AhlaPublish(ctx);
                deliver = TRUE;
            }
        }
        status = STATUS_SUCCESS;
        break;
    }

    default:
        break;
    }

    WdfSpinLockRelease(ctx->Lock);

    if (deliver) {
        AhlaFlushFrame(ctx);
    }

    VhfAsyncOperationComplete(VhfOperationHandle, status);
}
