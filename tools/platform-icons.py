#!/usr/bin/env python3
"""
Turns the desktop's platform logos into assets the phones can draw.

`src/DeskPair.Desktop/Assets/PlatformIcons.axaml` is the one copy of the artwork. The desktop draws those
geometries directly and fits each to its box, because every icon came from its own viewBox and none of them
agree. The phones cannot read Avalonia resources, so this writes the same path data out twice more: an SVG
per icon for the iOS asset catalogue, and a VectorDrawable per icon for Android.

The viewBox is computed here rather than guessed, for the reason the axaml comment gives -- the boxes
differ -- and a wrong one silently crops or shrinks an icon rather than failing.

Run it after changing an icon:

    python tools/platform-icons.py
"""
import pathlib
import re
import sys
import xml.sax.saxutils as sax

try:
    from svgelements import Path
except ImportError:  # pragma: no cover - a developer without the tool gets told, not a stack trace
    sys.exit("pip install svgelements")

ROOT = pathlib.Path(__file__).resolve().parent.parent
SOURCE = ROOT / "src/DeskPair.Desktop/Assets/PlatformIcons.axaml"
IOS = ROOT / "mobile/iosApp/iosApp/Assets.xcassets"
ANDROID = ROOT / "mobile/androidApp/src/main/res/drawable"

IOS_CONTENTS = """{
  "images" : [
    {
      "filename" : "%s.svg",
      "idiom" : "universal"
    }
  ],
  "info" : {
    "author" : "xcode",
    "version" : 1
  },
  "properties" : {
    "preserves-vector-representation" : true,
    "template-rendering-intent" : "template"
  }
}
"""


def icons():
    text = SOURCE.read_text(encoding="utf-8")
    for key, data in re.findall(
        r'<StreamGeometry x:Key="icon\.([a-z]+)">(.*?)</StreamGeometry>', text, re.S
    ):
        yield key, " ".join(data.split())


def main():
    IOS.mkdir(parents=True, exist_ok=True)
    ANDROID.mkdir(parents=True, exist_ok=True)

    written = 0
    for name, data in icons():
        box = Path(data).bbox()
        if box is None:
            sys.exit(f"{name}: the path has no bounds; is the data complete?")

        x0, y0, x1, y1 = box
        width, height = x1 - x0, y1 - y0

        # iOS: an SVG in the asset catalogue, rendered as a template so the tint follows the label beside
        # it. The transform moves the artwork to the origin, because a viewBox with a non-zero origin is
        # honoured inconsistently once Xcode has compiled the catalogue.
        image = IOS / f"platform-{name}.imageset"
        image.mkdir(exist_ok=True)
        (image / f"platform-{name}.svg").write_text(
            '<svg xmlns="http://www.w3.org/2000/svg" '
            f'viewBox="0 0 {width:.3f} {height:.3f}" width="{width:.3f}" height="{height:.3f}">'
            f'<path d="{sax.escape(data)}" fill="currentColor" '
            f'transform="translate({-x0:.3f} {-y0:.3f})"/></svg>\n',
            encoding="utf-8",
        )
        (image / "Contents.json").write_text(IOS_CONTENTS % f"platform-{name}", encoding="utf-8")

        # Android: a VectorDrawable, whose path syntax is SVG's. The viewport is the bounding box and the
        # group carries the same translation. No android:tint: the colour belongs to the caller, and
        # Compose's Icon tints the painter to whatever the row's text is using.
        (ANDROID / f"ic_platform_{name}.xml").write_text(
            '<vector xmlns:android="http://schemas.android.com/apk/res/android"\n'
            '    android:width="24dp"\n'
            '    android:height="24dp"\n'
            f'    android:viewportWidth="{width:.3f}"\n'
            f'    android:viewportHeight="{height:.3f}">\n'
            f'    <group android:translateX="{-x0:.3f}" android:translateY="{-y0:.3f}">\n'
            f'        <path android:fillColor="@android:color/white" android:pathData="{sax.escape(data)}" />\n'
            "    </group>\n"
            "</vector>\n",
            encoding="utf-8",
        )

        print(f"  {name:<8} {width:7.1f} x {height:7.1f}")
        written += 1

    if written == 0:
        sys.exit("no icons found; has PlatformIcons.axaml changed shape?")

    print(f"{written} icons written to the asset catalogue and to res/drawable")


if __name__ == "__main__":
    main()
