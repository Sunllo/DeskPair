// The control protocol between DeskPair's engine and its virtual display driver.
//
// Mirrored in C# by DeskPair.Platform.Windows.Native.IddControl; DisplayDriverProtocolTests reads this file and
// holds the two to the same codes, sizes and limits. Change both, and the protocol number, together.
#pragma once

#include <initguid.h>

// The device interface the engine opens. The device's security descriptor (set by the INF) lets only SYSTEM and
// administrators open it.
// {27F43708-94D2-4E46-8F95-22C373861DF4}
DEFINE_GUID(GUID_DEVINTERFACE_DESKPAIR_DISPLAY, 0x27f43708, 0x94d2, 0x4e46, 0x8f, 0x95, 0x22, 0xc3, 0x73, 0x86, 0x1d, 0xf4);

#define DESKPAIR_DISPLAY_PROTOCOL 1

// Displays that can be plugged in at once. Each slot is one monitor with an identity of its own (its connector
// index and container id), so Windows remembers each where the user put it.
#define DESKPAIR_DISPLAY_SLOTS 4

// The sizes one display may have. Windows takes at most 200 of a monitor's modes and target modes together (one more
// and IddCxMonitorArrival refuses it; measured on Windows 10 22H2, with and without an EDID), and a size is on offer
// only when it is in both lists. The first list is fixed when the display is plugged in; the second can change at any
// time. So a display is plugged in with up to 199 sizes and exactly one target, the size it is at, and
// IOCTL_..._SELECT makes another of its sizes the one target: the size it was at is no longer on offer, and Windows
// moves the display to the new one itself, in tens of milliseconds and without moving any window. Offering the new
// size beside the old one and switching to it does not work: after the first switch Windows keeps refusing sizes
// added later (ChangeDisplaySettingsEx says DISP_CHANGE_BADMODE).
#define DESKPAIR_DISPLAY_MAX_MODES 199

#define DESKPAIR_DISPLAY_MIN_SIDE 320
#define DESKPAIR_DISPLAY_MAX_SIDE 8192

#define DESKPAIR_DISPLAY_IOCTL(function, access) CTL_CODE(FILE_DEVICE_UNKNOWN, 0x900 + (function), METHOD_BUFFERED, (access))

// out DESKPAIR_DISPLAY_INFO
#define IOCTL_DESKPAIR_DISPLAY_INFO DESKPAIR_DISPLAY_IOCTL(0, FILE_READ_ACCESS)

// in DESKPAIR_DISPLAY_PLUG (followed by ModeCount modes, each size once), out DESKPAIR_DISPLAY_PLUGGED
#define IOCTL_DESKPAIR_DISPLAY_PLUG DESKPAIR_DISPLAY_IOCTL(1, FILE_WRITE_ACCESS)

// in DESKPAIR_DISPLAY_UNPLUG
#define IOCTL_DESKPAIR_DISPLAY_UNPLUG DESKPAIR_DISPLAY_IOCTL(2, FILE_WRITE_ACCESS)

// in DESKPAIR_DISPLAY_WATCHDOG: arms the watchdog, or feeds it; a TimeoutMs of 0 disarms it. When it runs out every
// display is unplugged, so an engine that dies does not leave the screen to a display nobody streams.
#define IOCTL_DESKPAIR_DISPLAY_WATCHDOG DESKPAIR_DISPLAY_IOCTL(3, FILE_WRITE_ACCESS)

// in DESKPAIR_DISPLAY_SELECT: the size the display is to take now, of those it was plugged in with. It works from the
// moment the display is plugged in; the engine still checks that the display got there.
#define IOCTL_DESKPAIR_DISPLAY_SELECT DESKPAIR_DISPLAY_IOCTL(4, FILE_WRITE_ACCESS)

#pragma pack(push, 4)

typedef struct DESKPAIR_DISPLAY_INFO
{
    UINT32 Protocol;
    UINT32 Slots;
    UINT32 MaxModes;
    UINT32 PluggedMask;   // bit n set: slot n has a display
    UINT32 WatchdogMs;    // 0: disarmed
} DESKPAIR_DISPLAY_INFO;

typedef struct DESKPAIR_DISPLAY_MODE
{
    UINT32 Width;
    UINT32 Height;
    UINT32 RefreshHz;
} DESKPAIR_DISPLAY_MODE;

typedef struct DESKPAIR_DISPLAY_PLUG
{
    UINT32 Protocol;
    UINT32 Slot;
    UINT32 Current;       // index into Modes: the size the display comes up at
    UINT32 ModeCount;     // 1..DESKPAIR_DISPLAY_MAX_MODES
    // DESKPAIR_DISPLAY_MODE Modes[ModeCount] follows
} DESKPAIR_DISPLAY_PLUG;

typedef struct DESKPAIR_DISPLAY_PLUGGED
{
    UINT32 Protocol;
    UINT32 Slot;
    UINT32 AdapterLuidLow;
    INT32 AdapterLuidHigh;
    UINT32 TargetId;      // with the adapter LUID, finds the display in QueryDisplayConfig
} DESKPAIR_DISPLAY_PLUGGED;

typedef struct DESKPAIR_DISPLAY_UNPLUG
{
    UINT32 Protocol;
    UINT32 Slot;
} DESKPAIR_DISPLAY_UNPLUG;

typedef struct DESKPAIR_DISPLAY_WATCHDOG
{
    UINT32 Protocol;
    UINT32 TimeoutMs;
} DESKPAIR_DISPLAY_WATCHDOG;

typedef struct DESKPAIR_DISPLAY_SELECT
{
    UINT32 Protocol;
    UINT32 Slot;
    UINT32 Current;       // index into the modes the display was plugged in with
} DESKPAIR_DISPLAY_SELECT;

#pragma pack(pop)
