#!/usr/bin/env python3
"""Checks the hand-written docs in docs/site (guides and home page).

Fails when:
  (a) a fenced block is tagged csharp, cs or c# (C# samples must come from
      docs/site/snippets through [!code-csharp[](...#region)] includes);
  (b) a https://github.com/roman-ambinder/a2a/(blob|tree)/main/<path> link
      points to a path that does not exist in the repo;
  (c) a [!code-...[](file#region)] include names a file or region that does not
      exist (DocFX renders a missing region as an empty block without a warning).

Usage: python3 docs/site/check-guides.py
"""
import re
import sys
import urllib.parse
from pathlib import Path

SITE = Path(__file__).resolve().parent
REPO = SITE.parent.parent

FENCE = re.compile(r"^\s{0,3}(`{3,}|~{3,})\s*([^\s`{]*)")
REPO_LINK = re.compile(
    r"https://github\.com/roman-ambinder/a2a/(?:blob|tree)/main/([^\s)\]>\"'`<]*)"
)
INCLUDE = re.compile(
    r"\[!code(?:-[A-Za-z#+-]+)?\[[^\]]*\]\(([^)\s#]+)(?:#([^)\s]+))?(?:\s+\"[^\"]*\")?\)\]"
)
BAD_LANGS = {"csharp", "cs", "c#", "c-sharp"}


def region_exists(text: str, name: str) -> bool:
    if re.search(r"^\s*#region\s+" + re.escape(name) + r"\s*$", text, re.M):
        return True
    return re.search(r"^\s*//\s*<" + re.escape(name) + r">\s*$", text, re.M) is not None


def check(path: Path) -> list[str]:
    errors = []
    rel = path.relative_to(REPO)
    in_fence = None
    for n, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
        m = FENCE.match(line)
        if m:
            marker, lang = m.group(1), m.group(2).lower()
            if in_fence is None:
                in_fence = marker[0]
                if lang in BAD_LANGS:
                    errors.append(
                        f"{rel}:{n}: fenced block tagged '{lang}'. "
                        "Put the code in docs/site/snippets and include it with "
                        "[!code-csharp[](../snippets/File.cs#Region)]."
                    )
            elif marker[0] == in_fence and not lang:
                in_fence = None
            continue

        for link in REPO_LINK.finditer(line):
            target = link.group(1).split("#", 1)[0].split("?", 1)[0]
            target = urllib.parse.unquote(target).rstrip(".,;:")
            if not target or not (REPO / target).exists():
                errors.append(
                    f"{rel}:{n}: repo link points to a path that does not exist on disk: '{target}'"
                )

        if in_fence is None:
            for inc in INCLUDE.finditer(line):
                file_part, region = inc.group(1), inc.group(2)
                if re.match(r"^[a-z]+://", file_part):
                    continue
                src = (path.parent / file_part).resolve()
                if not src.is_file():
                    errors.append(f"{rel}:{n}: code include file not found: '{file_part}'")
                elif region and not re.match(r"^L\d+", region):
                    if not region_exists(src.read_text(encoding="utf-8"), region):
                        errors.append(
                            f"{rel}:{n}: region '{region}' not found in '{file_part}'"
                        )
    return errors


def main() -> int:
    files = sorted((SITE / "guides").glob("*.md")) + [SITE / "index.md"]
    errors = []
    for f in files:
        if f.exists():
            errors.extend(check(f))
    if errors:
        print("check-guides: FAILED", file=sys.stderr)
        for e in errors:
            print("  " + e, file=sys.stderr)
        return 1
    print(f"check-guides: ok ({len(files)} files)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
