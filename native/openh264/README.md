# OpenH264 — not shipped, but supported if you bring your own

`DeskPair.Codec.OpenH264` can encode and decode H.264 in software through Cisco's OpenH264
(`reference/openh264-master`, BSD-2, see `LICENSE`). **This repository ships no built binary, and neither
does the product.** Without one the factory reports itself unavailable and `FallbackVideoEncoderFactory`
simply skips it, leaving Media Foundation to do the work on Windows.

## Why not

The BSD-2 licence covers the *source*. H.264 is separately patented, and Cisco pays those royalties **only
for the binaries Cisco itself distributes**, which its own consumers download at run time from
`ciscobinary.openh264.org`. A binary built from this source is outside that coverage, so shipping one makes
the vendor liable for AVC royalties. Sunllo chose not to take that on for what is only a fallback path.

## Bringing your own

The loader (`Native/OpenH264Interop.cs`) searches, in order:

1. `$SUNLLO_OPENH264_PATH`
2. `<app>/openh264-8.dll` (or `libopenh264.so.8` / `libopenh264.8.dylib`)
3. `<app>/runtimes/<rid>/native/<name>`

Drop a library at any of those and the encoder and decoder come back with no rebuild. Do this only if you
have your own AVC licence, or you are using a binary Cisco distributes.

## Building one (for your own use)

```
:: from a VS x64 developer prompt, with nasm (2.16) and meson/ninja (pip install meson ninja) on PATH
cd reference\openh264-master
meson setup buildmsvc --buildtype=release -Dtests=disabled -Ddefault_library=shared
ninja -C buildmsvc
:: then put buildmsvc\openh264-8.dll beside the executable, or set SUNLLO_OPENH264_PATH to it
```

Linux: `meson setup build --buildtype=release -Dtests=disabled && ninja -C build` → `libopenh264.so.8`.
macOS: same → `libopenh264.8.dylib`.

`.gitignore` keeps `*.dll` out of this repository; do not add an exception for these.
