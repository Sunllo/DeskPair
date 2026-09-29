// DeskPair's virtual display driver: an indirect display (IddCx) adapter on which DeskPair's engine plugs in up to
// DESKPAIR_DISPLAY_SLOTS monitors of its choosing, each with the list of sizes it may be set to.
//
// Why this and not a general-purpose virtual display driver:
//  - each display comes and goes on its own, under its own identity, where the one DeskPair used before rebuilt
//    them all (and renamed them) whenever one was added or taken away;
//  - each has its own list of up to 199 sizes -- as many as Windows takes for one monitor -- chosen for the viewer
//    who asked for it, so the display can take the size of that viewer's window at once, with no replugging;
//  - it is controlled through a device interface only SYSTEM and administrators can open, not a pipe anybody can
//    write to, and it reads no file;
//  - a watchdog unplugs everything when the engine stops feeding it.
//
// The frames are consumed and released as they come: DeskPair captures the display through Desktop Duplication
// like any other, so nothing here reads them.

#define NOMINMAX
#include <windows.h>
#include <bugcodes.h>
#include <wudfwdm.h>
#include <wdf.h>
#include <iddcx.h>

#include <avrt.h>
#include <d3d11_2.h>
#include <dxgi1_5.h>
#include <wrl.h>

#include <memory>
#include <mutex>
#include <vector>

#include "Control.h"

using Microsoft::WRL::ComPtr;

namespace
{
    // {28405050-3667-4E45-9708-BDD87092D500}; the last byte is the slot.
    GUID ContainerIdFor(UINT32 slot)
    {
        GUID id = { 0x28405050, 0x3667, 0x4e45, { 0x97, 0x08, 0xbd, 0xd8, 0x70, 0x92, 0xd5, 0x00 } };
        id.Data4[7] = static_cast<unsigned char>(slot);
        return id;
    }

    void Trace(const wchar_t* text)
    {
        OutputDebugStringW(L"DeskPairDisplay: ");
        OutputDebugStringW(text);
        OutputDebugStringW(L"\n");
    }

    void FillSignal(DISPLAYCONFIG_VIDEO_SIGNAL_INFO& signal, const DESKPAIR_DISPLAY_MODE& mode, bool monitorMode)
    {
        signal.totalSize.cx = signal.activeSize.cx = mode.Width;
        signal.totalSize.cy = signal.activeSize.cy = mode.Height;
        signal.AdditionalSignalInfo.vSyncFreqDivider = monitorMode ? 0 : 1;
        signal.AdditionalSignalInfo.videoStandard = 255;
        signal.vSyncFreq.Numerator = mode.RefreshHz;
        signal.vSyncFreq.Denominator = 1;
        signal.hSyncFreq.Numerator = mode.RefreshHz * mode.Height;
        signal.hSyncFreq.Denominator = 1;
        signal.scanLineOrdering = DISPLAYCONFIG_SCANLINE_ORDERING_PROGRESSIVE;
        signal.pixelRate = static_cast<UINT64>(mode.RefreshHz) * mode.Width * mode.Height;
    }

    // Consumes a monitor's swap-chain: Windows keeps composing only while the driver takes frames.
    class FrameSink
    {
    public:
        FrameSink(IDDCX_SWAPCHAIN swapChain, LUID renderAdapter, HANDLE frameReady)
            : m_swapChain(swapChain), m_renderAdapter(renderAdapter), m_frameReady(frameReady)
        {
            m_stop = CreateEventW(nullptr, TRUE, FALSE, nullptr);
            m_thread = CreateThread(nullptr, 0, [](LPVOID self) -> DWORD
            {
                static_cast<FrameSink*>(self)->Run();
                return 0;
            }, this, 0, nullptr);
        }

        ~FrameSink()
        {
            SetEvent(m_stop);
            if (m_thread)
            {
                WaitForSingleObject(m_thread, INFINITE);
                CloseHandle(m_thread);
            }

            CloseHandle(m_stop);
        }

        FrameSink(const FrameSink&) = delete;
        FrameSink& operator=(const FrameSink&) = delete;

    private:
        void Run()
        {
            DWORD taskIndex = 0;
            HANDLE task = AvSetMmThreadCharacteristicsW(L"Distribution", &taskIndex);
            Pump();

            // Deleting the swap-chain tells Windows to make a new one if the display is still wanted.
            WdfObjectDelete(reinterpret_cast<WDFOBJECT>(m_swapChain));
            if (task)
            {
                AvRevertMmThreadCharacteristics(task);
            }
        }

        void Pump()
        {
            ComPtr<IDXGIFactory5> factory;
            ComPtr<IDXGIAdapter1> adapter;
            ComPtr<ID3D11Device> device;
            if (FAILED(CreateDXGIFactory2(0, IID_PPV_ARGS(&factory)))
                || FAILED(factory->EnumAdapterByLuid(m_renderAdapter, IID_PPV_ARGS(&adapter)))
                || FAILED(D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                    nullptr, 0, D3D11_SDK_VERSION, &device, nullptr, nullptr)))
            {
                Trace(L"no render device for the swap-chain");
                return;
            }

            ComPtr<IDXGIDevice> dxgiDevice;
            if (FAILED(device.As(&dxgiDevice)))
            {
                return;
            }

            IDARG_IN_SWAPCHAINSETDEVICE setDevice = {};
            setDevice.pDevice = dxgiDevice.Get();
            if (FAILED(IddCxSwapChainSetDevice(m_swapChain, &setDevice)))
            {
                return;
            }

            for (;;)
            {
                IDARG_OUT_RELEASEANDACQUIREBUFFER buffer = {};
                HRESULT hr = IddCxSwapChainReleaseAndAcquireBuffer(m_swapChain, &buffer);
                if (hr == E_PENDING)
                {
                    HANDLE waits[] = { m_frameReady, m_stop };
                    DWORD woke = WaitForMultipleObjects(2, waits, FALSE, 16);
                    if (woke == WAIT_OBJECT_0 || woke == WAIT_TIMEOUT)
                    {
                        continue;
                    }

                    return;
                }

                if (FAILED(hr))
                {
                    return; // abandoned, e.g. the display went
                }

                // Nothing to do with the frame itself; the surface's reference is ours to give back.
                if (buffer.MetaData.pSurface)
                {
                    buffer.MetaData.pSurface->Release();
                }

                if (FAILED(IddCxSwapChainFinishedProcessingFrame(m_swapChain)))
                {
                    return;
                }
            }
        }

        IDDCX_SWAPCHAIN m_swapChain;
        LUID m_renderAdapter;
        HANDLE m_frameReady;
        HANDLE m_stop = nullptr;
        HANDLE m_thread = nullptr;
    };

}

// What a monitor carries: its slot, the sizes it can show and which of them it shows now -- its one target mode, and so
// the only size on offer -- owned until it is deleted. Current changes under g_currentLock; the rest is fixed.
struct MonitorContext
{
    UINT32 Slot;
    UINT32 Current;
    std::vector<DESKPAIR_DISPLAY_MODE>* Modes;
    FrameSink* Sink;
};

static std::mutex g_currentLock;

WDF_DECLARE_CONTEXT_TYPE(MonitorContext);

namespace
{
    IDDCX_TARGET_MODE TargetMode(const DESKPAIR_DISPLAY_MODE& mode)
    {
        IDDCX_TARGET_MODE target = {};
        target.Size = sizeof(target);
        FillSignal(target.TargetVideoSignalInfo.targetVideoSignalInfo, mode, false);
        return target;
    }

    // The adapter: the only one this driver makes, and what every IOCTL acts on.
    class Adapter
    {
    public:
        void Start(IDDCX_ADAPTER adapter)
        {
            std::lock_guard<std::mutex> guard(m_lock);
            m_adapter = adapter;
        }

        NTSTATUS Info(DESKPAIR_DISPLAY_INFO& info)
        {
            std::lock_guard<std::mutex> guard(m_lock);
            info = {};
            info.Protocol = DESKPAIR_DISPLAY_PROTOCOL;
            info.Slots = DESKPAIR_DISPLAY_SLOTS;
            info.MaxModes = DESKPAIR_DISPLAY_MAX_MODES;
            for (UINT32 i = 0; i < DESKPAIR_DISPLAY_SLOTS; i++)
            {
                if (m_monitors[i])
                {
                    info.PluggedMask |= 1u << i;
                }
            }

            info.WatchdogMs = m_watchdogMs;
            return STATUS_SUCCESS;
        }

        NTSTATUS Plug(const DESKPAIR_DISPLAY_PLUG& request, const DESKPAIR_DISPLAY_MODE* modes, DESKPAIR_DISPLAY_PLUGGED& plugged)
        {
            std::lock_guard<std::mutex> operation(m_operation);
            IDDCX_ADAPTER adapter;
            {
                std::lock_guard<std::mutex> guard(m_lock);
                if (!m_adapter)
                {
                    return STATUS_DEVICE_NOT_READY;
                }

                if (m_monitors[request.Slot])
                {
                    return STATUS_DEVICE_BUSY;
                }

                adapter = m_adapter;
            }

            // The monitor answers Windows' questions from its own copy of the list; it lives as long as the monitor.
            auto list = std::make_unique<std::vector<DESKPAIR_DISPLAY_MODE>>(modes, modes + request.ModeCount);

            WDF_OBJECT_ATTRIBUTES attributes;
            WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, MonitorContext);
            attributes.EvtCleanupCallback = [](WDFOBJECT object)
            {
                MonitorContext* context = WdfObjectGet_MonitorContext(object);
                delete context->Sink;
                context->Sink = nullptr;
                delete context->Modes;
                context->Modes = nullptr;
            };

            IDDCX_MONITOR_INFO info = {};
            info.Size = sizeof(info);
            info.MonitorType = DISPLAYCONFIG_OUTPUT_TECHNOLOGY_HDMI;
            info.ConnectorIndex = request.Slot;
            info.MonitorDescription.Size = sizeof(info.MonitorDescription);
            info.MonitorDescription.Type = IDDCX_MONITOR_DESCRIPTION_TYPE_EDID;
            info.MonitorDescription.DataSize = 0; // no EDID: Windows asks for the default modes, which are the list
            info.MonitorDescription.pData = nullptr;
            info.MonitorContainerId = ContainerIdFor(request.Slot);

            IDARG_IN_MONITORCREATE create = {};
            create.ObjectAttributes = &attributes;
            create.pMonitorInfo = &info;
            IDARG_OUT_MONITORCREATE created = {};
            NTSTATUS status = IddCxMonitorCreate(adapter, &create, &created);
            if (!NT_SUCCESS(status))
            {
                return status;
            }

            MonitorContext* context = WdfObjectGet_MonitorContext(created.MonitorObject);
            context->Slot = request.Slot;
            context->Current = request.Current;
            context->Modes = list.release();
            context->Sink = nullptr;

            IDARG_OUT_MONITORARRIVAL arrival = {};
            status = IddCxMonitorArrival(created.MonitorObject, &arrival);
            if (!NT_SUCCESS(status))
            {
                WdfObjectDelete(reinterpret_cast<WDFOBJECT>(created.MonitorObject));
                return status;
            }

            plugged.Protocol = DESKPAIR_DISPLAY_PROTOCOL;
            plugged.Slot = request.Slot;
            plugged.AdapterLuidLow = arrival.OsAdapterLuid.LowPart;
            plugged.AdapterLuidHigh = arrival.OsAdapterLuid.HighPart;
            plugged.TargetId = arrival.OsTargetId;

            std::lock_guard<std::mutex> guard(m_lock);
            m_monitors[request.Slot] = created.MonitorObject;
            return STATUS_SUCCESS;
        }

        NTSTATUS Unplug(UINT32 slot)
        {
            std::lock_guard<std::mutex> operation(m_operation);
            IDDCX_MONITOR monitor = Monitor(slot);
            if (!monitor)
            {
                return STATUS_NOT_FOUND;
            }

            NTSTATUS status = IddCxMonitorDeparture(monitor);
            if (NT_SUCCESS(status))
            {
                std::lock_guard<std::mutex> guard(m_lock);
                m_monitors[slot] = nullptr;
            }

            return status;
        }

        // Makes the display in the slot show another of its sizes: that size becomes its one target mode.
        NTSTATUS Select(UINT32 slot, UINT32 index)
        {
            std::lock_guard<std::mutex> operation(m_operation);
            IDDCX_MONITOR monitor = Monitor(slot);
            if (!monitor)
            {
                return STATUS_NOT_FOUND;
            }

            MonitorContext* context = WdfObjectGet_MonitorContext(monitor);
            if (index >= context->Modes->size())
            {
                return STATUS_INVALID_PARAMETER;
            }

            {
                std::lock_guard<std::mutex> guard(g_currentLock);
                context->Current = index;
            }

            // Not under g_currentLock: Windows may ask for the target modes before this returns.
            IDDCX_TARGET_MODE target = TargetMode((*context->Modes)[index]);
            IDARG_IN_UPDATEMODES update = {};
            update.Reason = IDDCX_UPDATE_REASON_OTHER;
            update.TargetModeCount = 1;
            update.pTargetModes = &target;
            return IddCxMonitorUpdateModes(monitor, &update);
        }

        void UnplugAll()
        {
            for (UINT32 slot = 0; slot < DESKPAIR_DISPLAY_SLOTS; slot++)
            {
                Unplug(slot);
            }
        }

        NTSTATUS Watchdog(UINT32 timeoutMs)
        {
            std::lock_guard<std::mutex> guard(m_lock);
            if (!m_timer)
            {
                m_timer = CreateThreadpoolTimer([](PTP_CALLBACK_INSTANCE, PVOID self, PTP_TIMER)
                {
                    Trace(L"the engine stopped feeding the watchdog; unplugging every display");
                    static_cast<Adapter*>(self)->UnplugAll();
                }, this, nullptr);
                if (!m_timer)
                {
                    return STATUS_INSUFFICIENT_RESOURCES;
                }
            }

            m_watchdogMs = timeoutMs;
            if (timeoutMs == 0)
            {
                SetThreadpoolTimer(m_timer, nullptr, 0, 0);
                return STATUS_SUCCESS;
            }

            ULARGE_INTEGER due;
            due.QuadPart = static_cast<ULONGLONG>(-static_cast<LONGLONG>(timeoutMs) * 10000);
            FILETIME dueTime;
            dueTime.dwLowDateTime = due.LowPart;
            dueTime.dwHighDateTime = due.HighPart;
            SetThreadpoolTimer(m_timer, &dueTime, 0, 0);
            return STATUS_SUCCESS;
        }

        void Stop()
        {
            PTP_TIMER timer;
            {
                std::lock_guard<std::mutex> guard(m_lock);
                timer = m_timer;
                m_timer = nullptr;
                m_adapter = nullptr;
            }

            if (timer)
            {
                SetThreadpoolTimer(timer, nullptr, 0, 0);
                WaitForThreadpoolTimerCallbacks(timer, TRUE);
                CloseThreadpoolTimer(timer);
            }
        }

    private:
        IDDCX_MONITOR Monitor(UINT32 slot)
        {
            std::lock_guard<std::mutex> guard(m_lock);
            return m_monitors[slot];
        }

        // Plugging, unplugging and selecting one at a time -- the watchdog's unplugging included -- so none of them
        // meets a monitor another is halfway through. Windows' callbacks never take it.
        std::mutex m_operation;
        std::mutex m_lock; // the fields below
        IDDCX_ADAPTER m_adapter = nullptr;
        IDDCX_MONITOR m_monitors[DESKPAIR_DISPLAY_SLOTS] = {};
        PTP_TIMER m_timer = nullptr;
        UINT32 m_watchdogMs = 0;
    };

    Adapter g_adapter;
}

extern "C" DRIVER_INITIALIZE DriverEntry;
static EVT_WDF_DRIVER_DEVICE_ADD DeviceAdd;
static EVT_WDF_DEVICE_D0_ENTRY DeviceD0Entry;
static EVT_IDD_CX_DEVICE_IO_CONTROL DeviceIoControl;
static EVT_IDD_CX_ADAPTER_INIT_FINISHED AdapterInitFinished;
static EVT_IDD_CX_ADAPTER_COMMIT_MODES AdapterCommitModes;
static EVT_IDD_CX_PARSE_MONITOR_DESCRIPTION ParseMonitorDescription;
static EVT_IDD_CX_MONITOR_GET_DEFAULT_DESCRIPTION_MODES MonitorGetDefaultModes;
static EVT_IDD_CX_MONITOR_QUERY_TARGET_MODES MonitorQueryTargetModes;
static EVT_IDD_CX_MONITOR_ASSIGN_SWAPCHAIN MonitorAssignSwapChain;
static EVT_IDD_CX_MONITOR_UNASSIGN_SWAPCHAIN MonitorUnassignSwapChain;

extern "C" BOOL WINAPI DllMain(_In_ HINSTANCE, _In_ UINT, _In_opt_ LPVOID)
{
    return TRUE;
}

_Use_decl_annotations_
extern "C" NTSTATUS DriverEntry(PDRIVER_OBJECT driverObject, PUNICODE_STRING registryPath)
{
    WDF_DRIVER_CONFIG config;
    WDF_DRIVER_CONFIG_INIT(&config, DeviceAdd);
    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_OBJECT_ATTRIBUTES_INIT(&attributes);
    return WdfDriverCreate(driverObject, registryPath, &attributes, &config, WDF_NO_HANDLE);
}

_Use_decl_annotations_
static NTSTATUS DeviceAdd(WDFDRIVER, PWDFDEVICE_INIT deviceInit)
{
    WDF_PNPPOWER_EVENT_CALLBACKS power;
    WDF_PNPPOWER_EVENT_CALLBACKS_INIT(&power);
    power.EvtDeviceD0Entry = DeviceD0Entry;
    WdfDeviceInitSetPnpPowerEventCallbacks(deviceInit, &power);

    IDD_CX_CLIENT_CONFIG idd;
    IDD_CX_CLIENT_CONFIG_INIT(&idd);
    idd.EvtIddCxDeviceIoControl = DeviceIoControl;
    idd.EvtIddCxAdapterInitFinished = AdapterInitFinished;
    idd.EvtIddCxAdapterCommitModes = AdapterCommitModes;
    idd.EvtIddCxParseMonitorDescription = ParseMonitorDescription;
    idd.EvtIddCxMonitorGetDefaultDescriptionModes = MonitorGetDefaultModes;
    idd.EvtIddCxMonitorQueryTargetModes = MonitorQueryTargetModes;
    idd.EvtIddCxMonitorAssignSwapChain = MonitorAssignSwapChain;
    idd.EvtIddCxMonitorUnassignSwapChain = MonitorUnassignSwapChain;
    NTSTATUS status = IddCxDeviceInitConfig(deviceInit, &idd);
    if (!NT_SUCCESS(status))
    {
        return status;
    }

    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_OBJECT_ATTRIBUTES_INIT(&attributes);
    attributes.EvtCleanupCallback = [](WDFOBJECT)
    {
        g_adapter.Stop();
    };

    WDFDEVICE device = nullptr;
    status = WdfDeviceCreate(&deviceInit, &attributes, &device);
    if (!NT_SUCCESS(status))
    {
        return status;
    }

    status = WdfDeviceCreateDeviceInterface(device, &GUID_DEVINTERFACE_DESKPAIR_DISPLAY, nullptr);
    if (!NT_SUCCESS(status))
    {
        return status;
    }

    return IddCxDeviceInitialize(device);
}

_Use_decl_annotations_
static NTSTATUS DeviceD0Entry(WDFDEVICE device, WDF_POWER_DEVICE_STATE)
{
    IDDCX_ADAPTER_CAPS caps = {};
    caps.Size = sizeof(caps);
    caps.MaxMonitorsSupported = DESKPAIR_DISPLAY_SLOTS;
    caps.EndPointDiagnostics.Size = sizeof(caps.EndPointDiagnostics);
    caps.EndPointDiagnostics.GammaSupport = IDDCX_FEATURE_IMPLEMENTATION_NONE;
    caps.EndPointDiagnostics.TransmissionType = IDDCX_TRANSMISSION_TYPE_WIRED_OTHER;
    caps.EndPointDiagnostics.pEndPointFriendlyName = L"DeskPair virtual display";
    caps.EndPointDiagnostics.pEndPointManufacturerName = L"Sunllo";
    caps.EndPointDiagnostics.pEndPointModelName = L"DeskPair";
    IDDCX_ENDPOINT_VERSION version = {};
    version.Size = sizeof(version);
    version.MajorVer = 1;
    caps.EndPointDiagnostics.pFirmwareVersion = &version;
    caps.EndPointDiagnostics.pHardwareVersion = &version;

    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_OBJECT_ATTRIBUTES_INIT(&attributes);
    IDARG_IN_ADAPTER_INIT init = {};
    init.WdfDevice = device;
    init.pCaps = &caps;
    init.ObjectAttributes = &attributes;
    IDARG_OUT_ADAPTER_INIT out = {};
    NTSTATUS status = IddCxAdapterInitAsync(&init, &out);
    if (!NT_SUCCESS(status))
    {
        Trace(L"adapter initialisation failed");
    }

    return STATUS_SUCCESS;
}

_Use_decl_annotations_
static NTSTATUS AdapterInitFinished(IDDCX_ADAPTER adapter, const IDARG_IN_ADAPTER_INIT_FINISHED* in)
{
    if (NT_SUCCESS(in->AdapterInitStatus))
    {
        g_adapter.Start(adapter); // no display until the engine asks for one
    }

    return STATUS_SUCCESS;
}

_Use_decl_annotations_
static NTSTATUS AdapterCommitModes(IDDCX_ADAPTER, const IDARG_IN_COMMITMODES*)
{
    return STATUS_SUCCESS; // nothing to reconfigure: there is no hardware behind the displays
}

_Use_decl_annotations_
static NTSTATUS ParseMonitorDescription(const IDARG_IN_PARSEMONITORDESCRIPTION*, IDARG_OUT_PARSEMONITORDESCRIPTION* out)
{
    out->MonitorModeBufferOutputCount = 0; // every monitor is made without an EDID
    return STATUS_INVALID_PARAMETER;
}

_Use_decl_annotations_
static NTSTATUS MonitorGetDefaultModes(IDDCX_MONITOR monitor, const IDARG_IN_GETDEFAULTDESCRIPTIONMODES* in, IDARG_OUT_GETDEFAULTDESCRIPTIONMODES* out)
{
    MonitorContext* context = WdfObjectGet_MonitorContext(monitor);
    const std::vector<DESKPAIR_DISPLAY_MODE>& modes = *context->Modes;
    out->DefaultMonitorModeBufferOutputCount = static_cast<UINT>(modes.size());
    if (in->DefaultMonitorModeBufferInputCount == 0)
    {
        return STATUS_SUCCESS;
    }

    if (in->DefaultMonitorModeBufferInputCount < modes.size())
    {
        return STATUS_BUFFER_TOO_SMALL;
    }

    for (size_t i = 0; i < modes.size(); i++)
    {
        IDDCX_MONITOR_MODE mode = {};
        mode.Size = sizeof(mode);
        mode.Origin = IDDCX_MONITOR_MODE_ORIGIN_DRIVER;
        FillSignal(mode.MonitorVideoSignalInfo, modes[i], true);
        in->pDefaultMonitorModes[i] = mode;
    }

    std::lock_guard<std::mutex> guard(g_currentLock);
    out->PreferredMonitorModeIdx = context->Current;
    return STATUS_SUCCESS;
}

_Use_decl_annotations_
static NTSTATUS MonitorQueryTargetModes(IDDCX_MONITOR monitor, const IDARG_IN_QUERYTARGETMODES* in, IDARG_OUT_QUERYTARGETMODES* out)
{
    // One target mode: the size the engine chose last, and so the only one on offer.
    MonitorContext* context = WdfObjectGet_MonitorContext(monitor);
    UINT32 current;
    {
        std::lock_guard<std::mutex> guard(g_currentLock);
        current = context->Current;
    }

    out->TargetModeBufferOutputCount = 1;
    if (in->TargetModeBufferInputCount >= 1)
    {
        in->pTargetModes[0] = TargetMode((*context->Modes)[current]);
    }

    return STATUS_SUCCESS;
}

_Use_decl_annotations_
static NTSTATUS MonitorAssignSwapChain(IDDCX_MONITOR monitor, const IDARG_IN_SETSWAPCHAIN* in)
{
    MonitorContext* context = WdfObjectGet_MonitorContext(monitor);
    delete context->Sink;
    context->Sink = new FrameSink(in->hSwapChain, in->RenderAdapterLuid, in->hNextSurfaceAvailable);
    return STATUS_SUCCESS;
}

_Use_decl_annotations_
static NTSTATUS MonitorUnassignSwapChain(IDDCX_MONITOR monitor)
{
    MonitorContext* context = WdfObjectGet_MonitorContext(monitor);
    delete context->Sink;
    context->Sink = nullptr;
    return STATUS_SUCCESS;
}

// Validates a request against its declared shape before anything is read from it.
template <typename T>
static bool Read(WDFREQUEST request, size_t minimum, T** buffer, size_t* length)
{
    return NT_SUCCESS(WdfRequestRetrieveInputBuffer(request, minimum, reinterpret_cast<PVOID*>(buffer), length))
        && (*buffer)->Protocol == DESKPAIR_DISPLAY_PROTOCOL;
}

_Use_decl_annotations_
static VOID DeviceIoControl(WDFDEVICE, WDFREQUEST request, size_t, size_t, ULONG code)
{
    NTSTATUS status = STATUS_INVALID_DEVICE_REQUEST;
    size_t written = 0;
    switch (code)
    {
    case IOCTL_DESKPAIR_DISPLAY_INFO:
    {
        DESKPAIR_DISPLAY_INFO* out = nullptr;
        status = WdfRequestRetrieveOutputBuffer(request, sizeof(*out), reinterpret_cast<PVOID*>(&out), nullptr);
        if (NT_SUCCESS(status))
        {
            status = g_adapter.Info(*out);
            written = sizeof(*out);
        }

        break;
    }

    case IOCTL_DESKPAIR_DISPLAY_PLUG:
    {
        DESKPAIR_DISPLAY_PLUG* in = nullptr;
        size_t length = 0;
        DESKPAIR_DISPLAY_PLUGGED* out = nullptr;
        if (!Read(request, sizeof(*in), &in, &length))
        {
            status = STATUS_REVISION_MISMATCH;
        }
        else if (in->Slot >= DESKPAIR_DISPLAY_SLOTS || in->ModeCount == 0 || in->ModeCount > DESKPAIR_DISPLAY_MAX_MODES
            || in->Current >= in->ModeCount
            || length < sizeof(*in) + static_cast<size_t>(in->ModeCount) * sizeof(DESKPAIR_DISPLAY_MODE))
        {
            status = STATUS_INVALID_PARAMETER;
        }
        else if (!NT_SUCCESS(status = WdfRequestRetrieveOutputBuffer(request, sizeof(*out), reinterpret_cast<PVOID*>(&out), nullptr)))
        {
            break;
        }
        else
        {
            const DESKPAIR_DISPLAY_MODE* modes = reinterpret_cast<const DESKPAIR_DISPLAY_MODE*>(in + 1);
            status = STATUS_SUCCESS;
            for (UINT32 i = 0; i < in->ModeCount && NT_SUCCESS(status); i++)
            {
                const DESKPAIR_DISPLAY_MODE& m = modes[i];
                if (m.Width < DESKPAIR_DISPLAY_MIN_SIDE || m.Width > DESKPAIR_DISPLAY_MAX_SIDE
                    || m.Height < DESKPAIR_DISPLAY_MIN_SIDE || m.Height > DESKPAIR_DISPLAY_MAX_SIDE
                    || m.RefreshHz < 24 || m.RefreshHz > 240)
                {
                    status = STATUS_INVALID_PARAMETER;
                }

                for (UINT32 j = 0; j < i && NT_SUCCESS(status); j++)
                {
                    if (modes[j].Width == m.Width && modes[j].Height == m.Height && modes[j].RefreshHz == m.RefreshHz)
                    {
                        status = STATUS_INVALID_PARAMETER; // each size once: a selection names one
                    }
                }
            }

            if (NT_SUCCESS(status))
            {
                status = g_adapter.Plug(*in, modes, *out);
                written = NT_SUCCESS(status) ? sizeof(*out) : 0;
            }
        }

        break;
    }

    case IOCTL_DESKPAIR_DISPLAY_UNPLUG:
    {
        DESKPAIR_DISPLAY_UNPLUG* in = nullptr;
        size_t length = 0;
        status = !Read(request, sizeof(*in), &in, &length) ? STATUS_REVISION_MISMATCH
            : in->Slot >= DESKPAIR_DISPLAY_SLOTS ? STATUS_INVALID_PARAMETER
            : g_adapter.Unplug(in->Slot);
        break;
    }

    case IOCTL_DESKPAIR_DISPLAY_SELECT:
    {
        DESKPAIR_DISPLAY_SELECT* in = nullptr;
        size_t length = 0;
        status = !Read(request, sizeof(*in), &in, &length) ? STATUS_REVISION_MISMATCH
            : in->Slot >= DESKPAIR_DISPLAY_SLOTS ? STATUS_INVALID_PARAMETER
            : g_adapter.Select(in->Slot, in->Current);
        break;
    }

    case IOCTL_DESKPAIR_DISPLAY_WATCHDOG:
    {
        DESKPAIR_DISPLAY_WATCHDOG* in = nullptr;
        size_t length = 0;
        status = !Read(request, sizeof(*in), &in, &length) ? STATUS_REVISION_MISMATCH
            : in->TimeoutMs != 0 && (in->TimeoutMs < 1000 || in->TimeoutMs > 600000) ? STATUS_INVALID_PARAMETER
            : g_adapter.Watchdog(in->TimeoutMs);
        break;
    }
    }

    WdfRequestCompleteWithInformation(request, status, written);
}
