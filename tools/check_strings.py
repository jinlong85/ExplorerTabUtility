#!/usr/bin/env python3
"""Checks the UI string files (ExplorerTabUtility/Localization/Strings.*.xaml).

The strings are edited directly in the XAML files (there is no generator). Every language file must have exactly the
same keys as Strings.en-US.xaml (the base / fallback), no duplicate keys, and the same {0}/{1} placeholders, so that
Loc.Format() never fails and no text silently falls back to English.

Usage:  python tools/check_strings.py      (exit code 1 when a problem is found)
"""
import re
import sys
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parent.parent / "ExplorerTabUtility" / "Localization"
BASE = "Strings.en-US.xaml"
X = "{http://schemas.microsoft.com/winfx/2006/xaml}"


def load(path):
    tree = ET.parse(path)
    entries, problems = {}, []
    for element in tree.getroot():
        key = element.get(X + "Key")
        if key is None:
            continue
        if key in entries:
            problems.append(f"{path.name}: duplicate key {key}")
        entries[key] = element.text or ""
    return entries, problems


def placeholders(text):
    return sorted(set(re.findall(r"\{(\d+)(?:[^}]*)\}", text)))


def main():
    base, problems = load(ROOT / BASE)
    for path in sorted(ROOT.glob("Strings.*.xaml")):
        if path.name == BASE:
            continue
        other, dup = load(path)
        problems += dup
        for key in sorted(set(base) - set(other)):
            problems.append(f"{path.name}: missing key {key}")
        for key in sorted(set(other) - set(base)):
            problems.append(f"{path.name}: key {key} is not in {BASE}")
        for key in sorted(set(base) & set(other)):
            if placeholders(base[key]) != placeholders(other[key]):
                problems.append(f"{path.name}: {key} placeholders {placeholders(other[key])} != {placeholders(base[key])} in {BASE}")
        if not path.read_bytes().startswith(b"\xef\xbb\xbf"):
            problems.append(f"{path.name}: must be saved as UTF-8 with BOM")

    for problem in problems:
        print(problem)
    print(f"{len(base)} keys checked, {len(problems)} problem(s)")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
