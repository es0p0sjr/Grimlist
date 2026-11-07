"""Check the AndroidTest APK for the native startup binding lost by stale builds.

Run before distributing an APK. This narrow regression check does not replace
installing that exact APK and checking a cold launch on a physical Android phone.
"""

import argparse
import sys
import zipfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("apk")
    args = parser.parse_args()
    symbol = b"Java_crc6488302ad6e9e4df1a_MauiApplication_n_1onCreate\0"
    try:
        with zipfile.ZipFile(args.apk) as apk:
            for abi in ("arm64-v8a", "x86_64"):
                library = f"lib/{abi}/libxamarin-app.so"
                if symbol not in apk.read(library):
                    raise ValueError(f"{library}: missing MAUI native onCreate binding; clean and rebuild")
                print(f"PASS: {abi} contains the MAUI native onCreate binding")
    except (OSError, KeyError, ValueError, zipfile.BadZipFile) as error:
        print(f"FAIL: {error}", file=sys.stderr)
        return 1
    print("Physical-device cold launch verification is still required.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
