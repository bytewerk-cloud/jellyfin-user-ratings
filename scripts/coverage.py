"""Summarise a Cobertura report; exit 1 if line coverage is below 100%."""
import sys
import xml.etree.ElementTree as ET
from collections import defaultdict

root = ET.parse(sys.argv[1]).getroot()
files = defaultdict(dict)  # filename -> {line: hits}
for cls in root.iter("class"):
    for line in cls.iter("line"):
        n = int(line.get("number"))
        files[cls.get("filename")][n] = max(files[cls.get("filename")].get(n, 0), int(line.get("hits")))

total = covered = 0
print(f"{'file':60} {'lines':>9}  missed")
for name in sorted(files):
    lines = files[name]
    hit = sum(1 for h in lines.values() if h > 0)
    missed = sorted(n for n, h in lines.items() if h == 0)
    total += len(lines)
    covered += hit
    short = name.split("Jellyfin.Plugin.Ducks/")[-1]
    print(f"{short:60} {hit:>4}/{len(lines):<4}  {','.join(map(str, missed))}")
pct = 100.0 * covered / total if total else 100.0
print(f"\nLine coverage: {covered}/{total} = {pct:.1f}%  (branch rate {float(root.get('branch-rate')) * 100:.1f}%)")
sys.exit(0 if covered == total else 1)
