"""Checks ICC profiles with LittleCMS (through Pillow's ImageCms), independently of SoftCal's own reader.

Usage: python tools/validate_icc.py PROFILE.icc [PROFILE2.icc ...]

For each profile it prints the description, version, white point and colorants as LittleCMS reads them, whether a
'vcgt' tag is present, and the result of converting a few sRGB colors into the profile. Exit code 1 if any profile
fails to load or to build a transform. Development tool only: the app does not depend on Python.
"""

import sys

from PIL import Image, ImageCms


def check(path: str) -> bool:
    try:
        profile = ImageCms.getOpenProfile(path)
    except (OSError, ImageCms.PyCMSError) as error:
        print(f"{path}: FAILED to open: {error}")
        return False

    p = profile.profile
    print(f"{path}")
    print(f"  description : {ImageCms.getProfileDescription(profile).strip()}")
    print(f"  version     : {p.version}, class {p.device_class}, space {p.xcolor_space.strip()} -> {p.connection_space.strip()}")
    print(f"  media white : {p.media_white_point}")
    print(f"  colorants   : R {p.red_colorant}  G {p.green_colorant}  B {p.blue_colorant}")
    print(f"  vcgt        : {'present' if p.is_matrix_shaper and p.profile_id is not None and _has_tag(path, b'vcgt') else 'absent'}")

    try:
        srgb = ImageCms.createProfile("sRGB")
        transform = ImageCms.buildTransform(srgb, profile, "RGB", "RGB", renderingIntent=ImageCms.Intent.RELATIVE_COLORIMETRIC)
    except ImageCms.PyCMSError as error:
        print(f"  FAILED to build a transform: {error}")
        return False

    image = Image.new("RGB", (5, 1))
    colors = [(255, 255, 255), (128, 128, 128), (255, 0, 0), (0, 255, 0), (0, 0, 0)]
    image.putdata(colors)
    out = ImageCms.applyTransform(image, transform)
    print("  sRGB -> profile (relative colorimetric):")
    pixels = out.get_flattened_data() if hasattr(out, "get_flattened_data") else out.getdata()
    for source, result in zip(colors, pixels):
        print(f"    {source} -> {result}")
    return True


def _has_tag(path: str, signature: bytes) -> bool:
    with open(path, "rb") as file:
        data = file.read()
    count = int.from_bytes(data[128:132], "big")
    return any(data[132 + 12 * i:136 + 12 * i] == signature for i in range(count))


if __name__ == "__main__":
    results = [check(path) for path in sys.argv[1:]]
    sys.exit(0 if results and all(results) else 1)
