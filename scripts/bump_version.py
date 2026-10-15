#!/usr/bin/env python3
"""Bump the AerControl version everywhere it's declared.

    python3 scripts/bump_version.py patch|minor|major     # 1.2.3 -> 1.2.4 / 1.3.0 / 2.0.0
    python3 scripts/bump_version.py 2.0.0                 # explicit
    python3 scripts/bump_version.py --check               # print current version

VERSION is the source of truth. This updates macos/project.yml (MARKETING_VERSION, and
CURRENT_PROJECT_VERSION +1), windows/Directory.Build.props (<Version>) and
Package.appxmanifest (Identity Version="x.y.z.0"), then prints the new version.
"""
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
SEMVER = re.compile(r"^(\d+)\.(\d+)\.(\d+)$")


def sub(path, pattern, repl, count=1):
    p = ROOT / path
    text = p.read_text(encoding="utf-8")
    new, n = re.subn(pattern, repl, text, count=count, flags=re.M)
    if n == 0:
        sys.exit(f"pattern not found in {path}: {pattern}")
    p.write_text(new, encoding="utf-8")


def main():
    current = (ROOT / "VERSION").read_text().strip()
    m = SEMVER.match(current)
    if not m:
        sys.exit(f"VERSION is not x.y.z: {current!r}")
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    arg = sys.argv[1]
    if arg == "--check":
        print(current)
        return
    major, minor, patch = map(int, m.groups())
    if arg == "major":
        new = f"{major + 1}.0.0"
    elif arg == "minor":
        new = f"{major}.{minor + 1}.0"
    elif arg == "patch":
        new = f"{major}.{minor}.{patch + 1}"
    elif SEMVER.match(arg):
        new = arg
    else:
        sys.exit(f"expected major|minor|patch|x.y.z, got {arg!r}")

    (ROOT / "VERSION").write_text(new + "\n")
    sub("macos/project.yml", r'^(\s*MARKETING_VERSION:\s*)"[^"]*"', rf'\g<1>"{new}"')
    build = int(re.search(r'CURRENT_PROJECT_VERSION:\s*"(\d+)"', (ROOT / "macos/project.yml").read_text()).group(1)) + 1
    sub("macos/project.yml", r'^(\s*CURRENT_PROJECT_VERSION:\s*)"\d+"', rf'\g<1>"{build}"')
    sub("windows/Directory.Build.props", r"<Version>[^<]*</Version>", f"<Version>{new}</Version>")
    sub("windows/src/AerControl.App/Package.appxmanifest",
        r'(<Identity Name="AerControl" Publisher="[^"]*" Version=")[^"]*(")', rf"\g<1>{new}.0\g<2>")
    print(new)


if __name__ == "__main__":
    main()
