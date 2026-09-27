# libvpx (VP9)

`src/DeskPair.Codec.Vpx` binds libvpx's C API for VP9, and this is where a built library goes.

## Why this one is different from OpenH264

`native/openh264/README.md` explains why no H.264 binary ships with this repository: the source is BSD-2 but
the codec is patented, and Cisco's royalty payment follows only the binaries Cisco itself distributes.

VP9 has no such problem. libvpx is BSD-3, and Google grants the patents royalty-free to anyone, for any
purpose, with no distribution condition. **A libvpx build may be shipped with this product.** Nothing is
committed here yet only because the build has to happen somewhere; there is no licence reason to withhold it.

That is the point of Phase 7 in the codec plan. H.264 and H.265 reach the far end through hardware or through
an OS codec whose licence somebody else pays for. VP9 is the one codec this project can put on a machine that
has neither — a Linux host with no VAAPI, a VM, a stripped Windows edition — without depending on a licence
it does not hold.

## Where the loader looks

In order, and the first one that loads wins:

1. `SUNLLO_LIBVPX_PATH` — a full path to the library.
2. Beside the executable.
3. `runtimes/<rid>/native/` beside the executable, e.g. `runtimes/win-x64/native/vpx.dll`.
4. The system loader's own search, by name.

Names tried: `vpx.dll`, `libvpx.dll`, `libvpx-1.dll` on Windows; `libvpx.so.11`, `libvpx.so.9`, `libvpx.so`
on Linux; the matching `.dylib` names on macOS. Find nothing and `VpxVideoEncoderFactory.Describe()` returns
an empty list, `VpxVideoDecoderFactory.Probe()` returns `None`, and negotiation simply never offers VP9.

## Building one

### Windows

```
pwsh tools/build-libvpx.ps1
```

Two steps, because neither alone gives .NET something to load. vcpkg builds libvpx and fetches its own nasm,
so nothing has to be installed beyond Visual Studio — but its Windows port produces a **static** `vpx.lib`,
since libvpx's MSVC build has no shared-library configuration. The script then links a DLL from that static
library using libvpx's own export lists (`vpx/exports_com`, `exports_enc`, `exports_dec` and the per-codec
lists) as a module definition file, so the DLL exports exactly what libvpx means to export and nothing more.

The result is `artifacts/vpx/shim/vpx.dll`, which `tools/publish.ps1` copies into a build when it is there.

Two things that cost time the first time round:

- MSVC fails with `cannot open compiler generated file ''` when the intermediate paths approach MAX_PATH,
  which a build under a deep temp directory reaches on its own. The script keeps vcpkg's build trees at
  `C:pxbt` for that reason.
- The `data` entries in libvpx's export lists (`vpx_codec_vp9_cx_algo` and friends) must be marked `DATA` in
  the `.def`, or the loader hands back the address of a pointer instead of the codec interface itself.

### Linux and macOS

The distribution package is fine and is what most machines already have: `libvpx-dev` on Debian and Ubuntu,
`libvpx-devel` on Fedora, `brew install libvpx` on macOS. Only the shared library is needed at run time.

### From source

```
git clone --depth 1 --branch v1.15.2 https://chromium.googlesource.com/webm/libvpx
cd libvpx && ./configure --enable-vp9 --disable-vp8 --enable-pic --enable-shared \
    --disable-examples --disable-tools --disable-docs --disable-unit-tests
make -j
```

The x86-64 builds need nasm or yasm for the assembly; there is no pure-C path for them.

## Which version

The binding was written against **libvpx 1.15.2** and is verified against **1.16.0**, which is what vcpkg
currently builds. Two things tie it to a version, and both are checked rather than assumed. Both earned
their keep on the first run against a real library: one absorbed a moved ABI version, the other caught a
wrong expectation in this binding before it could reach the encoder.

- **The ABI version.** `VPX_ENCODER_ABI_VERSION` and `VPX_DECODER_ABI_VERSION` are compile-time constants
  that move between releases, and initialising with the wrong one is refused cleanly with
  `VPX_CODEC_ABI_MISMATCH`. The binding tries the version it was written against and then a band either
  side. This is not theoretical: 1.15.2 computes 37 and 1.16.0 computes 39, because `VPX_TPL_ABI_VERSION`
  went from 4 to 5 and `VPX_EXT_RATECTRL_ABI_VERSION` from `6 + tpl` to `7 + tpl`. The decoder's stayed
  at 12.
- **The shape of `vpx_codec_enc_cfg_t`.** The binding writes fields by offset. Before it writes anything it
  reads back the defaults libvpx just filled in and checks nine distinctive ones against
  `vp9_cx_iface.c`'s own table — including `rc_target_bitrate` at +112, which is the field that proves the
  eight-byte hole the two `vpx_fixed_buf_t` open at +76 was accounted for. A libvpx that lays the struct out
  differently is refused, and the refusal names the field and both values, rather than being silently
  misconfigured — that failure would otherwise appear as bad pictures, not as an error. It fired on the very
  first run against a real library, on a `kf_max_dist` expectation of 9999, which is VP8's default; VP9's
  is 128.

`VpxLayoutTests` derives every offset from the C declarations quoted in its comments, so a wrong constant
fails the build's test run rather than a session.
