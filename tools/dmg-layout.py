"""Write or verify Finder layout on a mounted image, without scripting Finder."""

import argparse
import pathlib
import struct
import subprocess

from ds_store import DSStore
from mac_alias import Alias


POSITIONS = {
    "YuWangSync.app": (160, 180),
    "Applications": (520, 180),
    "安装说明.txt": (340, 286),
}
BOUNDS = "{{100, 100}, {680, 450}}"


def write_layout(mount):
    with DSStore.open(str(mount / ".DS_Store"), "w+") as store:
        store["."]["vSrn"] = ("long", 1)
        store["."]["icvl"] = ("type", b"icnv")
        store["."]["bwsp"] = {
            "WindowBounds": BOUNDS,
            "ShowStatusBar": False,
            "ShowTabView": False,
            "ShowToolbar": False,
            "ShowPathbar": False,
            "ShowSidebar": False,
            "ContainerShowSidebar": False,
            "PreviewPaneVisibility": False,
        }
        store["."]["icvp"] = {
            "viewOptionsVersion": 1,
            "backgroundType": 2,
            "backgroundImageAlias": Alias.for_file(str(mount / ".background.tiff")).to_bytes(),
            "gridOffsetX": 0.0,
            "gridOffsetY": 0.0,
            "gridSpacing": 100.0,
            "arrangeBy": "none",
            "showIconPreview": False,
            "showItemInfo": False,
            "labelOnBottom": True,
            "textSize": 13.0,
            "iconSize": 96.0,
            "scrollPositionX": 0.0,
            "scrollPositionY": 0.0,
        }
        for name, position in POSITIONS.items():
            store[name]["Iloc"] = position


def verify_layout(mount):
    with DSStore.open(str(mount / ".DS_Store"), "r") as store:
        assert store["."]["icvl"] == (b"type", b"icnv"), "Finder must use icon view"
        window = store["."]["bwsp"]
        assert window["WindowBounds"] == BOUNDS
        assert not any(window[key] for key in ("ShowToolbar", "ShowSidebar", "ShowStatusBar", "ShowPathbar"))
        icons = store["."]["icvp"]
        assert icons["arrangeBy"] == "none" and icons["iconSize"] == 96
        assert icons["showIconPreview"] is False, "Quick Look must not replace the small document icon"
        assert icons["backgroundType"] == 2 and icons["backgroundImageAlias"]
        for name, position in POSITIONS.items():
            assert store[name]["Iloc"] == position, f"Incorrect position: {name}"
            assert (mount / name).exists(), f"Missing installation item: {name}"
    assert (mount / ".background.tiff").stat().st_size > 0
    # CPython exposes os.getxattr on Linux, but not on the macOS runner.
    def attribute(name):
        return bytes.fromhex(subprocess.check_output([
            "/usr/bin/xattr", "-px", name, str(mount / "安装说明.txt")
        ], text=True))

    info = attribute("com.apple.FinderInfo")
    assert struct.unpack_from(">H", info, 8)[0] & 0x0400, "Small custom instruction icon was lost"
    assert attribute("com.apple.ResourceFork"), "Instruction icon has no resources"
    print("DMG layout verified: fixed positions, background, icon view, small instruction icon.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("operation", choices=("write", "verify"))
    parser.add_argument("mount", type=pathlib.Path)
    args = parser.parse_args()
    if args.operation == "write":
        write_layout(args.mount)
    verify_layout(args.mount)
