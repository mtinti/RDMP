#!/usr/bin/env python3
"""Generate one published ExtractionFilter per Scottish health board on the
SHARE_Demography catalogue. Input = the board NAME; the code maps it to the
single-letter Region cipher and builds the filter name + WHERE.

    python3 scripts/make_healthboard_filters.py --all
    python3 scripts/make_healthboard_filters.py "Tayside" "Greater Glasgow & Clyde"

Region codes verified against SHARE_Demography's published Safe-Haven filters
([SHARE].dbo.[Demography].[Region] IN ('T','F','V') etc.).
"""
import re
import sys

CATALOGUE = "SHARE_Demography"
REGION_COL = "[SHARE].dbo.[Demography].[Region]"

# canonical board name -> Region cipher
BOARDS = {
    "Ayrshire & Arran": "A",
    "Borders": "B",
    "Dumfries & Galloway": "Y",
    "Fife": "F",
    "Forth Valley": "V",
    "Grampian": "N",
    "Greater Glasgow & Clyde": "G",
    "Highland": "H",
    "Lanarkshire": "L",
    "Lothian": "S",
    "Orkney": "R",
    "Shetland": "Z",
    "Tayside": "T",
    "Western Isles": "W",
    "Argyll & Clyde (legacy)": "C",
}

# common aliases / shorthands -> canonical name
ALIASES = {
    "ggc": "Greater Glasgow & Clyde", "glasgow": "Greater Glasgow & Clyde",
    "greaterglasgowandclyde": "Greater Glasgow & Clyde",
    "ayrshirearran": "Ayrshire & Arran",
    "dumfriesgalloway": "Dumfries & Galloway", "dandg": "Dumfries & Galloway",
    "westernisles": "Western Isles", "eileananiar": "Western Isles",
    "forthvalley": "Forth Valley", "argyllclyde": "Argyll & Clyde (legacy)",
}


def _norm(s):
    return re.sub(r"(&|and|\(legacy\)|nhs|health\s*board|hb)", "", s.lower())\
        .replace(" ", "").strip(" -")


_LOOKUP = {_norm(k): k for k in BOARDS} | ALIASES


def resolve(name):
    """Board name (any common form) -> (canonical_name, region_code)."""
    key = _norm(name)
    canon = _LOOKUP.get(key)
    if not canon:                       # fuzzy: unique substring match
        hits = [c for c in BOARDS if key and (key in _norm(c) or _norm(c) in key)]
        canon = hits[0] if len(hits) == 1 else None
    if not canon:
        raise ValueError(f"Unknown health board: {name!r}. Known: {', '.join(BOARDS)}")
    return canon, BOARDS[canon]


def filter_spec(name):
    canon, code = resolve(name)
    return {
        "catalogue": CATALOGUE,
        "name": f"Health Board - {canon}",
        "where": f"{REGION_COL} = '{code}'",
        "board": canon,
        "code": code,
    }


def emit_cli(names):
    """Emit a runnable `rdmp cmd` bash script: one master ExtractionFilter per
    board on SHARE_Demography. VERIFY the NewObject/Set signatures once with
    `rdmp cmd DescribeCommand NewObject` — only those two lines are RDMP-specific."""
    print("#!/usr/bin/env bash")
    print("set -euo pipefail")
    print("# --- configure ---")
    print('RDMP="${RDMP:-rdmp}"          # or: RDMP="dotnet /path/to/rdmp.dll"')
    print("# Master filters attach to an ExtractionInformation (a column) of SHARE_Demography.")
    print("# Find it:  $RDMP cmd ListSupportedCommands | grep -i filter   # confirm NewObject/Set")
    print("#           (pick an ExtractionInformation id on the SHARE_Demography catalogue)")
    print('EI="${EI:?set EI, e.g. EI=ExtractionInformation:1234}"')
    print()
    for n in names:
        f = filter_spec(n)
        print(f'# {f["board"]}  (Region {f["code"]})')
        print(f'out=$("$RDMP" cmd NewObject ExtractionFilter "$EI" "{f["name"]}")')
        print('''id=$(printf '%s' "$out" | grep -oE 'ExtractionFilter:[0-9]+' | head -1)''')
        print(f'"$RDMP" cmd Set "$id" WhereSQL "{f["where"]}"')
        print()


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    names = list(BOARDS) if "--all" in sys.argv else args
    if not names:
        sys.exit(__doc__)
    if "--cli" in sys.argv:
        emit_cli(names)
        return
    for n in names:
        try:
            f = filter_spec(n)
            print(f"{f['board']:30} code={f['code']}  name={f['name']!r}")
            print(f"   WHERE: {f['where']}")
        except ValueError as e:
            print(f"!! {e}")


if __name__ == "__main__":
    main()
