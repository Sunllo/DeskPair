using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DeskPair.Codec.OpenH264.Native;

#pragma warning disable IDE1006, CA1401, SYSLIB1054, CS0649

/// <summary>
/// Layouts mirror codec_api.h / codec_app_def.h of OpenH264 2.6 on 64-bit targets; sizes are asserted by
/// tests. The encoder/decoder objects are C++ instances, called through their vtable with `this` first.
/// </summary>
internal static unsafe partial class OpenH264
{
    private const string LibraryName = "openh264";

    public const int UsageScreenContentRealTime = 1;
    public const int RcBitrateMode = 1;
    public const int ProfileBaseline = 66;
    public const int ProfileMain = 77;
    public const int VideoFormatI420 = 23;
    public const int SpatialLayer0 = 0;
    public const int SpatialLayerAll = 4;
    public const int ComplexityLow = 0;
    public const int ComplexityMedium = 1;
    public const int ErrorConSliceCopyCrossIdrFreezeResChange = 5;
    public const int VideoBitstreamAvc = 0;

    public const int EncoderOptionDataFormat = 0;
    public const int EncoderOptionBitrate = 5;
    public const int EncoderOptionMaxBitrate = 6;
    public const int EncoderOptionTraceLevel = 25;

    public const int DecoderOptionEndOfStream = 1;
    public const int DecoderOptionErrorConIdc = 8;
    public const int DecoderOptionNumOfThreads = 19;

    public const int FrameTypeInvalid = 0;
    public const int FrameTypeIdr = 1;
    public const int FrameTypeI = 2;
    public const int FrameTypeP = 3;
    public const int FrameTypeSkip = 4;

    public const int DsErrorFree = 0;
    public const int DsFramePending = 1;

    private static readonly object LoadLock = new();
    private static bool _resolverInstalled;
    private static nint _handle;
    private static string? _loadError;

    public static string? LoadError => _loadError;

    /// <summary>Loads the library once; false (with <see cref="LoadError"/>) when no binary is available for this platform.</summary>
    public static bool TryLoad()
    {
        lock (LoadLock)
        {
            if (_handle != 0)
            {
                return true;
            }

            if (_loadError is not null)
            {
                return false;
            }

            if (!_resolverInstalled)
            {
                NativeLibrary.SetDllImportResolver(typeof(OpenH264).Assembly, Resolve);
                _resolverInstalled = true;
            }

            nint handle = Resolve(LibraryName, typeof(OpenH264).Assembly, null);
            if (handle == 0)
            {
                _loadError = $"OpenH264 library not found (looked for {string.Join(", ", CandidateNames())}).";
                return false;
            }

            _handle = handle;
            return true;
        }
    }

    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != LibraryName)
        {
            return 0;
        }

        if (_handle != 0)
        {
            return _handle;
        }

        foreach (string candidate in CandidatePaths())
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out nint h))
            {
                return h;
            }
        }

        foreach (string bare in CandidateNames())
        {
            if (NativeLibrary.TryLoad(bare, out nint h))
            {
                return h;
            }
        }

        return 0;
    }

    private static IEnumerable<string> CandidateNames()
    {
        if (OperatingSystem.IsWindows())
        {
            return ["openh264-8.dll", "openh264.dll", "libopenh264.dll"];
        }

        if (OperatingSystem.IsMacOS())
        {
            return ["libopenh264.8.dylib", "libopenh264.dylib"];
        }

        return ["libopenh264.so.8", "libopenh264.so"];
    }

    private static IEnumerable<string> CandidatePaths()
    {
        string? explicitPath = Environment.GetEnvironmentVariable("SUNLLO_OPENH264_PATH");
        if (!string.IsNullOrEmpty(explicitPath))
        {
            yield return explicitPath;
        }

        string baseDir = AppContext.BaseDirectory;
        string rid = RuntimeInformation.RuntimeIdentifier;
        string arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        string osRid = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        foreach (string name in CandidateNames())
        {
            yield return Path.Combine(baseDir, name);
            yield return Path.Combine(baseDir, "runtimes", rid, "native", name);
            yield return Path.Combine(baseDir, "runtimes", $"{osRid}-{arch}", "native", name);
        }
    }

    // ---- C entry points ----

    // OpenH264's entry points and interface methods are cdecl (EXTAPI in codec_api.h); on 32-bit Windows the default is stdcall.
    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int WelsCreateSVCEncoder(nint* ppEncoder);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void WelsDestroySVCEncoder(nint pEncoder);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial CLong WelsCreateDecoder(nint* ppDecoder);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void WelsDestroyDecoder(nint pDecoder);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void WelsGetCodecVersionEx(OpenH264Version* pVersion);

    // ---- vtables (order from ISVCEncoderVtbl / ISVCDecoderVtbl) ----

    private static nint* Vtable(nint obj) => *(nint**)obj;

    public static int EncInitializeExt(nint enc, SEncParamExt* p) => ((delegate* unmanaged[Cdecl]<nint, SEncParamExt*, int>)Vtable(enc)[1])(enc, p);

    public static int EncGetDefaultParams(nint enc, SEncParamExt* p) => ((delegate* unmanaged[Cdecl]<nint, SEncParamExt*, int>)Vtable(enc)[2])(enc, p);

    public static int EncUninitialize(nint enc) => ((delegate* unmanaged[Cdecl]<nint, int>)Vtable(enc)[3])(enc);

    public static int EncEncodeFrame(nint enc, SSourcePicture* pic, SFrameBSInfo* info) => ((delegate* unmanaged[Cdecl]<nint, SSourcePicture*, SFrameBSInfo*, int>)Vtable(enc)[4])(enc, pic, info);

    /// <summary>C++ signature is ForceIntraFrame(bool bIDR, int iLayerId = -1); the default is not in the vtable call, so pass -1 (all layers).</summary>
    public static int EncForceIntraFrame(nint enc, bool idr) => ((delegate* unmanaged[Cdecl]<nint, byte, int, int>)Vtable(enc)[6])(enc, idr ? (byte)1 : (byte)0, -1);

    public static int EncSetOption(nint enc, int option, void* value) => ((delegate* unmanaged[Cdecl]<nint, int, void*, int>)Vtable(enc)[7])(enc, option, value);

    public static CLong DecInitialize(nint dec, SDecodingParam* p) => ((delegate* unmanaged[Cdecl]<nint, SDecodingParam*, CLong>)Vtable(dec)[0])(dec, p);

    public static CLong DecUninitialize(nint dec) => ((delegate* unmanaged[Cdecl]<nint, CLong>)Vtable(dec)[1])(dec);

    public static int DecDecodeFrameNoDelay(nint dec, byte* src, int srcLen, byte** dst, SBufferInfo* info) => ((delegate* unmanaged[Cdecl]<nint, byte*, int, byte**, SBufferInfo*, int>)Vtable(dec)[3])(dec, src, srcLen, dst, info);

    public static CLong DecSetOption(nint dec, int option, void* value) => ((delegate* unmanaged[Cdecl]<nint, int, void*, CLong>)Vtable(dec)[8])(dec, option, value);
}

[StructLayout(LayoutKind.Sequential)]
internal struct OpenH264Version
{
    public uint Major, Minor, Revision, Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SSliceArgument
{
    public int uiSliceMode;
    public uint uiSliceNum;
    public fixed uint uiSliceMbNum[35];
    public uint uiSliceSizeConstraint;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SSpatialLayerConfig
{
    public int iVideoWidth;
    public int iVideoHeight;
    public float fFrameRate;
    public int iSpatialBitrate;
    public int iMaxSpatialBitrate;
    public int uiProfileIdc;
    public int uiLevelIdc;
    public int iDLayerQp;
    public SSliceArgument sSliceArgument;
    public byte bVideoSignalTypePresent;
    public byte uiVideoFormat;
    public byte bFullRange;
    public byte bColorDescriptionPresent;
    public byte uiColorPrimaries;
    public byte uiTransferCharacteristics;
    public byte uiColorMatrix;
    public byte bAspectRatioPresent;
    public int eAspectRatio;
    public ushort sAspectRatioExtWidth;
    public ushort sAspectRatioExtHeight;
}

[InlineArray(4)]
internal struct SpatialLayers
{
    private SSpatialLayerConfig _element;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SEncParamExt
{
    public int iUsageType;
    public int iPicWidth;
    public int iPicHeight;
    public int iTargetBitrate;
    public int iRCMode;
    public float fMaxFrameRate;
    public int iTemporalLayerNum;
    public int iSpatialLayerNum;
    public SpatialLayers sSpatialLayers;
    public int iComplexityMode;
    public uint uiIntraPeriod;
    public int iNumRefFrame;
    public int eSpsPpsIdStrategy;
    public byte bPrefixNalAddingCtrl;
    public byte bEnableSSEI;
    public byte bSimulcastAVC;
    public int iPaddingFlag;
    public int iEntropyCodingModeFlag;
    public byte bEnableFrameSkip;
    public int iMaxBitrate;
    public int iMaxQp;
    public int iMinQp;
    public uint uiMaxNalSize;
    public byte bEnableLongTermReference;
    public int iLTRRefNum;
    public uint iLtrMarkPeriod;
    public ushort iMultipleThreadIdc;
    public byte bUseLoadBalancing;
    public int iLoopFilterDisableIdc;
    public int iLoopFilterAlphaC0Offset;
    public int iLoopFilterBetaOffset;
    public byte bEnableDenoise;
    public byte bEnableBackgroundDetection;
    public byte bEnableAdaptiveQuant;
    public byte bEnableFrameCroppingFlag;
    public byte bEnableSceneChangeDetect;
    public byte bIsLosslessLink;
    public byte bFixRCOverShoot;
    public int iIdrBitrateRatio;
    public byte bPsnrY;
    public byte bPsnrU;
    public byte bPsnrV;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SSourcePicture
{
    public int iColorFormat;
    public fixed int iStride[4];
    public byte* pData0;
    public byte* pData1;
    public byte* pData2;
    public byte* pData3;
    public int iPicWidth;
    public int iPicHeight;
    public long uiTimeStamp;
    public byte bPsnrY;
    public byte bPsnrU;
    public byte bPsnrV;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SLayerBSInfo
{
    public byte uiTemporalId;
    public byte uiSpatialId;
    public byte uiQualityId;
    public int eFrameType;
    public byte uiLayerType;
    public int iSubSeqId;
    public int iNalCount;
    public int* pNalLengthInByte;
    public byte* pBsBuf;
    public fixed float rPsnr[3];
}

[InlineArray(128)]
internal struct LayerInfos
{
    private SLayerBSInfo _element;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SFrameBSInfo
{
    public int iLayerNum;
    public LayerInfos sLayerInfo;
    public int eFrameType;
    public int iFrameSizeInBytes;
    public long uiTimeStamp;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SBitrateInfo
{
    public int iLayer;
    public int iBitrate;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SVideoProperty
{
    public uint size;
    public int eVideoBsType;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SDecodingParam
{
    public nint pFileNameRestructed;
    public uint uiCpuLoad;
    public byte uiTargetDqLayer;
    public int eEcActiveIdc;
    public byte bParseOnly;
    public SVideoProperty sVideoProperty;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SSysMEMBuffer
{
    public int iWidth;
    public int iHeight;
    public int iFormat;
    public fixed int iStride[2];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SBufferInfo
{
    public int iBufferStatus;
    public ulong uiInBsTimeStamp;
    public ulong uiOutYuvTimeStamp;
    public SSysMEMBuffer sSystemBuffer;
    public byte* pDst0;
    public byte* pDst1;
    public byte* pDst2;
}
