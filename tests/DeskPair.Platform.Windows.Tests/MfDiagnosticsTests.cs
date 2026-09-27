using SharpGen.Runtime;
using DeskPair.Platform.Windows.Codec;
using Vortice.MediaFoundation;
using Xunit.Abstractions;

namespace DeskPair.Platform.Windows.Tests;

/// <summary>Ground-truth probe of what the Microsoft H.264 encoder accepts; kept as documentation of the MFT's contract.</summary>
public class MfDiagnosticsTests(ITestOutputHelper output)
{
    [Fact]
    public void Probe_encoder_output_type_acceptance()
    {
        MediaFactory.MFStartup(true).CheckError();
        try
        {
            using IMFTransform t = MediaFoundation.CreateTransform(MediaFoundation.H264EncoderClsid);
            t.GetStreamCount(out int inputs, out int outputs);
            output.WriteLine($"streams: {inputs} in / {outputs} out");

            for (int i = 0; i < 4; i++)
            {
                try
                {
                    using IMFMediaType avail = t.GetOutputAvailableType(0, i);
                    uint count = avail.Count;
                    output.WriteLine($"available output type {i}: {count} attributes");
                    for (uint k = 0; k < count; k++)
                    {
                        avail.GetItemByIndex(k, out Guid key);
                        output.WriteLine($"   {key}");
                    }
                }
                catch (SharpGenException e)
                {
                    output.WriteLine($"available output type {i}: {e.ResultCode}");
                    break;
                }
            }

            (string Name, Action<IMFMediaType> Fill)[] variants =
            [
                ("major+sub only", m => { m.Set(MediaFoundation.MtMajorType, MediaFoundation.MediaTypeVideo); m.Set(MediaFoundation.MtSubtype, MediaFoundation.FormatH264); }),
                ("+bitrate", m => { m.Set(MediaFoundation.MtMajorType, MediaFoundation.MediaTypeVideo); m.Set(MediaFoundation.MtSubtype, MediaFoundation.FormatH264); m.Set(MediaFoundation.MtAvgBitrate, 2_000_000u); }),
                ("+size", m => { m.Set(MediaFoundation.MtMajorType, MediaFoundation.MediaTypeVideo); m.Set(MediaFoundation.MtSubtype, MediaFoundation.FormatH264); m.Set(MediaFoundation.MtAvgBitrate, 2_000_000u); m.Set(MediaFoundation.MtFrameSize, MediaFoundation.Pack(320, 240)); }),
                ("+rate", m => { m.Set(MediaFoundation.MtMajorType, MediaFoundation.MediaTypeVideo); m.Set(MediaFoundation.MtSubtype, MediaFoundation.FormatH264); m.Set(MediaFoundation.MtAvgBitrate, 2_000_000u); m.Set(MediaFoundation.MtFrameSize, MediaFoundation.Pack(320, 240)); m.Set(MediaFoundation.MtFrameRate, MediaFoundation.Pack(30, 1)); }),
                ("+interlace", m => { m.Set(MediaFoundation.MtMajorType, MediaFoundation.MediaTypeVideo); m.Set(MediaFoundation.MtSubtype, MediaFoundation.FormatH264); m.Set(MediaFoundation.MtAvgBitrate, 2_000_000u); m.Set(MediaFoundation.MtFrameSize, MediaFoundation.Pack(320, 240)); m.Set(MediaFoundation.MtFrameRate, MediaFoundation.Pack(30, 1)); m.Set(MediaFoundation.MtInterlaceMode, 2u); }),
                ("+profile", m => { m.Set(MediaFoundation.MtMajorType, MediaFoundation.MediaTypeVideo); m.Set(MediaFoundation.MtSubtype, MediaFoundation.FormatH264); m.Set(MediaFoundation.MtAvgBitrate, 2_000_000u); m.Set(MediaFoundation.MtFrameSize, MediaFoundation.Pack(320, 240)); m.Set(MediaFoundation.MtFrameRate, MediaFoundation.Pack(30, 1)); m.Set(MediaFoundation.MtInterlaceMode, 2u); m.Set(MediaFoundation.MtMpeg2Profile, 77u); }),
            ];
            foreach ((string name, Action<IMFMediaType> fill) in variants)
            {
                using IMFMediaType m = MediaFactory.MFCreateMediaType();
                fill(m);
                uint n = m.Count;
                try
                {
                    t.SetOutputType(0, m, 1 /* MFT_SET_TYPE_TEST_ONLY */);
                    output.WriteLine($"{name} ({n} attrs): ACCEPTED");
                }
                catch (SharpGenException e)
                {
                    output.WriteLine($"{name} ({n} attrs): {e.ResultCode} — attrs: {Dump(m)}");
                }
            }
        }
        finally
        {
            MediaFactory.MFShutdown();
        }
    }

    [Fact]
    public void Probe_real_set_with_and_without_codec_api()
    {
        MediaFactory.MFStartup(true).CheckError();
        try
        {
            foreach (bool codecApiFirst in new[] { false, true })
            {
                using IMFTransform t = MediaFoundation.CreateTransform(MediaFoundation.H264EncoderClsid);
                ICodecAPI? api = codecApiFirst ? t.TryGetCodecApi() : null;
                output.WriteLine($"codecApiFirst={codecApiFirst}: api={(api is null ? "null" : "ok")}");
                using IMFMediaType m = MediaFactory.MFCreateMediaType();
                m.Set(MediaFoundation.MtMajorType, MediaFoundation.MediaTypeVideo);
                m.Set(MediaFoundation.MtSubtype, MediaFoundation.FormatH264);
                m.Set(MediaFoundation.MtAvgBitrate, 2_000_000u);
                m.Set(MediaFoundation.MtFrameSize, MediaFoundation.Pack(320, 240));
                m.Set(MediaFoundation.MtFrameRate, MediaFoundation.Pack(30, 1));
                m.Set(MediaFoundation.MtInterlaceMode, 2u);
                try
                {
                    t.SetOutputType(0, m, 0);
                    output.WriteLine("   real SetOutputType: OK");
                }
                catch (SharpGenException e)
                {
                    output.WriteLine($"   real SetOutputType: {e.ResultCode}");
                    continue;
                }

                using IMFMediaType input = MediaFactory.MFCreateMediaType();
                input.Set(MediaFoundation.MtMajorType, MediaFoundation.MediaTypeVideo);
                input.Set(MediaFoundation.MtSubtype, MediaFoundation.FormatNv12);
                input.Set(MediaFoundation.MtFrameSize, MediaFoundation.Pack(320, 240));
                input.Set(MediaFoundation.MtFrameRate, MediaFoundation.Pack(30, 1));
                input.Set(MediaFoundation.MtInterlaceMode, 2u);
                try
                {
                    t.SetInputType(0, input, 0);
                    output.WriteLine("   SetInputType NV12: OK");
                }
                catch (SharpGenException e)
                {
                    output.WriteLine($"   SetInputType NV12: {e.ResultCode}");
                }

                if (api is not null)
                {
                    output.WriteLine($"   codecapi ForceKeyFrame supported={api.IsSupported(MediaFoundation.CodecApiForceKeyFrame)} set={api.SetValue(MediaFoundation.CodecApiForceKeyFrame, 1u)}");
                    output.WriteLine($"   codecapi MeanBitRate set={api.SetValue(MediaFoundation.CodecApiMeanBitRate, 1_000_000u)}");
                }
            }
        }
        finally
        {
            MediaFactory.MFShutdown();
        }
    }

    [Fact]
    public void Probe_startup_flavours_and_profiles()
    {
        foreach (bool lite in new[] { true, false })
        {
            MediaFactory.MFStartup(lite).CheckError();
            try
            {
                foreach ((string name, uint? profile) in new (string, uint?)[] { ("no-profile", null), ("base", 66u), ("main", 77u), ("high", 100u) })
                {
                    using IMFTransform t = MediaFoundation.CreateTransform(MediaFoundation.H264EncoderClsid);
                    using IMFMediaType m = MediaFactory.MFCreateMediaType();
                    m.Set(MediaFoundation.MtMajorType, MediaFoundation.MediaTypeVideo);
                    m.Set(MediaFoundation.MtSubtype, MediaFoundation.FormatH264);
                    m.Set(MediaFoundation.MtAvgBitrate, 2_000_000u);
                    m.Set(MediaFoundation.MtFrameSize, MediaFoundation.Pack(320, 240));
                    m.Set(MediaFoundation.MtFrameRate, MediaFoundation.Pack(30, 1));
                    m.Set(MediaFoundation.MtInterlaceMode, 2u);
                    m.Set(MediaFoundation.MtPixelAspectRatio, MediaFoundation.Pack(1, 1));
                    if (profile is not null)
                    {
                        m.Set(MediaFoundation.MtMpeg2Profile, profile.Value);
                    }

                    try
                    {
                        t.SetOutputType(0, m, 0);
                        output.WriteLine($"lite={lite} {name}: OK");
                    }
                    catch (SharpGenException e)
                    {
                        output.WriteLine($"lite={lite} {name}: {e.ResultCode}");
                    }
                }
            }
            finally
            {
                MediaFactory.MFShutdown();
            }
        }
    }

    [Fact]
    public void Probe_decoder_and_enumerated_encoders()
    {
        MediaFactory.MFStartup(true).CheckError();
        try
        {
            // Decoder: does a real SetInputType/SetOutputType work in this process?
            using (IMFTransform d = MediaFoundation.CreateTransform(MediaFoundation.H264DecoderClsid))
            {
                using IMFMediaType input = MediaFactory.MFCreateMediaType();
                input.Set(MediaFoundation.MtMajorType, MediaFoundation.MediaTypeVideo);
                input.Set(MediaFoundation.MtSubtype, MediaFoundation.FormatH264);
                try
                {
                    d.SetInputType(0, input, 0);
                    using IMFMediaType avail = d.GetOutputAvailableType(0, 0);
                    d.SetOutputType(0, avail, 0);
                    output.WriteLine("decoder: real set OK");
                }
                catch (SharpGenException e)
                {
                    output.WriteLine($"decoder: {e.ResultCode}");
                }
            }

            // Enumerate registered H.264 encoders (sync | async | hardware).
            var outType = new RegisterTypeInfo { GuidMajorType = MediaFoundation.MediaTypeVideo, GuidSubtype = MediaFoundation.FormatH264 };
            MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, 0x1 | 0x2 | 0x4 | 0x40, null, outType, out nint activates, out uint count);
            output.WriteLine($"enumerated encoders: {count}");
            for (uint i = 0; i < count; i++)
            {
                nint ptr = System.Runtime.InteropServices.Marshal.ReadIntPtr(activates, (int)(i * nint.Size));
                using var activate = new IMFActivate(ptr);
                string name = "?";
                try
                {
                    name = activate.GetString(new Guid("314ffbae-5b41-4c95-9c19-4e7d586face3"));
                }
                catch (SharpGenException)
                {
                }

                try
                {
                    activate.ActivateObject(MediaFoundation.IMFTransformIid, out IMFTransform? t).CheckError();
                    using (t)
                    {
                    using IMFMediaType m = MediaFactory.MFCreateMediaType();
                    m.Set(MediaFoundation.MtMajorType, MediaFoundation.MediaTypeVideo);
                    m.Set(MediaFoundation.MtSubtype, MediaFoundation.FormatH264);
                    m.Set(MediaFoundation.MtAvgBitrate, 2_000_000u);
                    m.Set(MediaFoundation.MtFrameSize, MediaFoundation.Pack(320, 240));
                    m.Set(MediaFoundation.MtFrameRate, MediaFoundation.Pack(30, 1));
                    m.Set(MediaFoundation.MtInterlaceMode, 2u);
                    m.Set(MediaFoundation.MtPixelAspectRatio, MediaFoundation.Pack(1, 1));
                    try
                    {
                        t!.SetOutputType(0, m, 0);
                        output.WriteLine($"  [{i}] {name}: real set OK");
                    }
                    catch (SharpGenException e)
                    {
                        output.WriteLine($"  [{i}] {name}: {e.ResultCode}");
                    }
                    }
                }
                catch (SharpGenException e)
                {
                    output.WriteLine($"  [{i}] {name}: activate failed {e.ResultCode}");
                }
            }

            System.Runtime.InteropServices.Marshal.FreeCoTaskMem(activates);
        }
        finally
        {
            MediaFactory.MFShutdown();
        }
    }

    private static string Dump(IMFMediaType m)
    {
        uint count = m.Count;
        var keys = new List<string>();
        for (uint k = 0; k < count; k++)
        {
            m.GetItemByIndex(k, out Guid key);
            keys.Add(key.ToString()[..8]);
        }

        return string.Join(",", keys);
    }
    /// <summary>
    /// Does this machine offer a royalty-free video codec of its own? Windows carries MSVPXENC.dll and
    /// MSVP9DEC.dll, but neither is listed under the Media Foundation transform categories, so this asks
    /// MFTEnumEx directly rather than trusting the registry. Diagnostic only -- it asserts nothing, it
    /// reports, because the answer decides whether a software VP9 path can be had for free on Windows.
    /// </summary>
    [Fact]
    public void Report_whether_media_foundation_offers_vp8_or_vp9()
    {
        MediaFactory.MFStartup(true).CheckError();
        try
        {
            foreach ((string name, Guid subtype) in new[] { ("VP9", MediaFoundation.FormatVp90), ("VP8", MediaFoundation.FormatVp80), ("H.264", MediaFoundation.FormatH264) })
            {
                output.WriteLine($"{name} encoders: {Describe(TransformCategoryGuids.VideoEncoder, subtype, asOutput: true)}");
                output.WriteLine($"{name} decoders: {Describe(TransformCategoryGuids.VideoDecoder, subtype, asOutput: false)}");
            }
        }
        finally
        {
            MediaFactory.MFShutdown();
        }
    }

    /// <summary>Names every MFT registered for a subtype, or says there are none.</summary>
    private static string Describe(Guid category, Guid subtype, bool asOutput)
    {
        const uint EnumSync = 0x1, EnumAsync = 0x2, EnumHardware = 0x4, EnumSortAndFilter = 0x40;
        var friendlyName = new Guid("314FFBAE-5B41-4C95-9C19-4E7D586FACE3");
        var info = new RegisterTypeInfo { GuidMajorType = MediaFoundation.MediaTypeVideo, GuidSubtype = subtype };
        try
        {
            MediaFactory.MFTEnumEx(
                category,
                EnumSync | EnumAsync | EnumHardware | EnumSortAndFilter,
                asOutput ? null : info,
                asOutput ? info : null,
                out nint activates,
                out uint count);
            if (count == 0)
            {
                return "none";
            }

            var names = new List<string>();
            for (uint i = 0; i < count; i++)
            {
                nint ptr = System.Runtime.InteropServices.Marshal.ReadIntPtr(activates, (int)(i * nint.Size));
                var activate = new IMFActivate(ptr);
                try
                {
                    names.Add(activate.GetString(friendlyName));
                }
                catch (SharpGenException)
                {
                    names.Add("(unnamed)");
                }

                activate.Dispose();
            }

            System.Runtime.InteropServices.Marshal.FreeCoTaskMem(activates);
            return string.Join("; ", names);
        }
        catch (SharpGenException e)
        {
            return $"enumeration failed: {e.Message}";
        }
    }

    /// <summary>
    /// The VP9 MFT enumerates, but enumerating is not encoding. This activates it and tries to agree on the
    /// same contract the H.264 encoder is driven with -- VP9 out, NV12 in, a real frame size and bitrate --
    /// because that is what decides whether a royalty-free path exists on Windows without shipping anything.
    /// </summary>
    [Fact]
    public void Report_whether_the_vp9_encoder_can_be_configured()
    {
        const uint EnumSync = 0x1, EnumAsync = 0x2, EnumHardware = 0x4, EnumSortAndFilter = 0x40;
        MediaFactory.MFStartup(true).CheckError();
        try
        {
            var wanted = new RegisterTypeInfo { GuidMajorType = MediaFoundation.MediaTypeVideo, GuidSubtype = MediaFoundation.FormatVp90 };
            MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, EnumSync | EnumAsync | EnumHardware | EnumSortAndFilter, null, wanted, out nint activates, out uint count);
            if (count == 0)
            {
                output.WriteLine("no VP9 encoder to configure");
                return;
            }

            nint ptr = System.Runtime.InteropServices.Marshal.ReadIntPtr(activates, 0);
            using var activate = new IMFActivate(ptr);
            activate.ActivateObject(MediaFoundation.IMFTransformIid, out IMFTransform? transform).CheckError();
            using (transform)
            {
                transform!.GetStreamCount(out int inputs, out int outputs);
                output.WriteLine($"activated: {inputs} in / {outputs} out");

                // An asynchronous MFT refuses everything until it is unlocked; this is what MfVideoEncoder does.
                var transformAsync = new Guid("F81A699A-649A-497D-8C73-29F8FED6AD7A");
                var transformAsyncUnlock = new Guid("E5666D6B-3422-4EB6-A421-DA7DB1F8E207");
                try
                {
                    using IMFAttributes attrs = transform.Attributes;
                    bool isAsync = false;
                    try
                    {
                        isAsync = attrs.GetUInt32(transformAsync) == 1;
                    }
                    catch (SharpGenException)
                    {
                    }

                    output.WriteLine($"async MFT: {isAsync}");
                    if (isAsync)
                    {
                        attrs.Set(transformAsyncUnlock, 1u);
                        output.WriteLine("unlocked");
                    }
                }
                catch (SharpGenException e)
                {
                    output.WriteLine($"attributes unavailable: {e.Message}");
                }

                // Ask what it offers before asserting anything: the MFT knows its own contract.
                for (int i = 0; i < 6; i++)
                {
                    try
                    {
                        using IMFMediaType avail = transform.GetOutputAvailableType(0, i);
                        output.WriteLine($"offers output {i}: {avail.GetGUID(MediaFoundation.MtSubtype)} ({avail.Count} attributes)");
                    }
                    catch (SharpGenException e)
                    {
                        output.WriteLine($"offers output {i}: none ({e.ResultCode})");
                        break;
                    }
                }

                // Which is it: the attributes are wrong, or it refuses any caller? Try the cheapest cases.
                void Try(string what, Action attempt)
                {
                    try
                    {
                        attempt();
                        output.WriteLine($"{what}: OK");
                    }
                    catch (SharpGenException e)
                    {
                        output.WriteLine($"{what}: {e.ResultCode}");
                    }
                }

                // 1. Its own advertised type, entirely untouched.
                using (IMFMediaType bare = transform.GetOutputAvailableType(0, 1))
                {
                    Try("SetOutputType(its own VP90 type, unmodified)", () => transform.SetOutputType(0, bare, 0));
                    Try("SetOutputType(.., TEST_ONLY)", () => transform.SetOutputType(0, bare, 2));
                }

                // 2. Its own type plus the three things an encoder always needs.
                using (IMFMediaType filled = transform.GetOutputAvailableType(0, 1))
                {
                    filled.Set(MediaFoundation.MtAvgBitrate, 8_000_000u);
                    filled.Set(MediaFoundation.MtFrameSize, MediaFoundation.Pack(1920, 1080));
                    filled.Set(MediaFoundation.MtFrameRate, MediaFoundation.Pack(30, 1));
                    filled.Set(MediaFoundation.MtInterlaceMode, MediaFoundation.InterlaceProgressive);
                    Try("SetOutputType(its own VP90 type + bitrate/size/rate/interlace)", () => transform.SetOutputType(0, filled, 0));
                }

                // 3. Input first, in case this transform wants the order the other way round.
                using (IMFMediaType input0 = MediaFactory.MFCreateMediaType())
                {
                    input0.Set(MediaFoundation.MtMajorType, MediaFoundation.MediaTypeVideo);
                    input0.Set(MediaFoundation.MtSubtype, MediaFoundation.FormatNv12);
                    input0.Set(MediaFoundation.MtFrameSize, MediaFoundation.Pack(1920, 1080));
                    input0.Set(MediaFoundation.MtFrameRate, MediaFoundation.Pack(30, 1));
                    input0.Set(MediaFoundation.MtInterlaceMode, MediaFoundation.InterlaceProgressive);
                    Try("SetInputType(NV12) before any output type", () => transform.SetInputType(0, input0, 0));
                }

                // 4b. Bisect: which attribute turns "something is missing" into "something is wrong"?
                void Step(string what, Action<IMFMediaType> fill)
                {
                    using IMFMediaType m = transform.GetOutputAvailableType(0, 1);
                    fill(m);
                    Try($"  output {what}", () => transform.SetOutputType(0, m, 2)); // TEST_ONLY, no commitment
                }

                Step("+size", m => m.Set(MediaFoundation.MtFrameSize, MediaFoundation.Pack(1920, 1080)));
                Step("+size+rate", m =>
                {
                    m.Set(MediaFoundation.MtFrameSize, MediaFoundation.Pack(1920, 1080));
                    m.Set(MediaFoundation.MtFrameRate, MediaFoundation.Pack(30, 1));
                });
                Step("+size+rate+bitrate", m =>
                {
                    m.Set(MediaFoundation.MtFrameSize, MediaFoundation.Pack(1920, 1080));
                    m.Set(MediaFoundation.MtFrameRate, MediaFoundation.Pack(30, 1));
                    m.Set(MediaFoundation.MtAvgBitrate, 8_000_000u);
                });
                Step("+size+rate+bitrate+interlace", m =>
                {
                    m.Set(MediaFoundation.MtFrameSize, MediaFoundation.Pack(1920, 1080));
                    m.Set(MediaFoundation.MtFrameRate, MediaFoundation.Pack(30, 1));
                    m.Set(MediaFoundation.MtAvgBitrate, 8_000_000u);
                    m.Set(MediaFoundation.MtInterlaceMode, MediaFoundation.InterlaceProgressive);
                });

                Step("+size+PAR", m =>
                {
                    m.Set(MediaFoundation.MtFrameSize, MediaFoundation.Pack(1920, 1080));
                    m.Set(MediaFoundation.MtPixelAspectRatio, MediaFoundation.Pack(1, 1));
                });
                Step("+size+rate+PAR", m =>
                {
                    m.Set(MediaFoundation.MtFrameSize, MediaFoundation.Pack(1920, 1080));
                    m.Set(MediaFoundation.MtFrameRate, MediaFoundation.Pack(30, 1));
                    m.Set(MediaFoundation.MtPixelAspectRatio, MediaFoundation.Pack(1, 1));
                });
                Step("+size+rate(25)+PAR+interlace+bitrate", m =>
                {
                    m.Set(MediaFoundation.MtFrameSize, MediaFoundation.Pack(1920, 1080));
                    m.Set(MediaFoundation.MtFrameRate, MediaFoundation.Pack(25, 1));
                    m.Set(MediaFoundation.MtPixelAspectRatio, MediaFoundation.Pack(1, 1));
                    m.Set(MediaFoundation.MtInterlaceMode, MediaFoundation.InterlaceProgressive);
                    m.Set(MediaFoundation.MtAvgBitrate, 8_000_000u);
                });
                Step("+size+rate+PAR+interlace+bitrate (the full H.264 recipe)", m =>
                {
                    m.Set(MediaFoundation.MtFrameSize, MediaFoundation.Pack(1920, 1080));
                    m.Set(MediaFoundation.MtFrameRate, MediaFoundation.Pack(30, 1));
                    m.Set(MediaFoundation.MtPixelAspectRatio, MediaFoundation.Pack(1, 1));
                    m.Set(MediaFoundation.MtInterlaceMode, MediaFoundation.InterlaceProgressive);
                    m.Set(MediaFoundation.MtAvgBitrate, 8_000_000u);
                });

                // 5. Input is agreed now -- does output become acceptable? (This MFT may want the reverse order.)
                using (IMFMediaType after = transform.GetOutputAvailableType(0, 1))
                {
                    after.Set(MediaFoundation.MtAvgBitrate, 8_000_000u);
                    after.Set(MediaFoundation.MtFrameSize, MediaFoundation.Pack(1920, 1080));
                    after.Set(MediaFoundation.MtFrameRate, MediaFoundation.Pack(30, 1));
                    after.Set(MediaFoundation.MtInterlaceMode, MediaFoundation.InterlaceProgressive);
                    Try("SetOutputType(VP90 + attrs) AFTER the input type", () => transform.SetOutputType(0, after, 0));
                }

                // 4. What input types does it admit to, with no output type agreed?
                for (int i = 0; i < 6; i++)
                {
                    try
                    {
                        using IMFMediaType avail = transform.GetInputAvailableType(0, i);
                        output.WriteLine($"offers input {i}: {avail.GetGUID(MediaFoundation.MtSubtype)}");
                    }
                    catch (SharpGenException e)
                    {
                        output.WriteLine($"offers input {i}: none ({e.ResultCode})");
                        break;
                    }
                }
            }

            System.Runtime.InteropServices.Marshal.FreeCoTaskMem(activates);
        }
        finally
        {
            MediaFactory.MFShutdown();
        }
    }

}
