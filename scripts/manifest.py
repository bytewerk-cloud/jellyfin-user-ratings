"""Writes the plugin's meta.json and maintains the plugin repository manifest.json.

Plugin identity comes from build.yaml so it is defined once.
  manifest.py meta --version V --target-abi A --changelog C            → prints meta.json
  manifest.py add  --manifest F --version V --target-abi A --changelog C --url U --checksum M
"""
import argparse
import datetime
import json
import os
import re

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def plugin_info():
    text = open(os.path.join(ROOT, "build.yaml"), encoding="utf-8").read()

    def field(name):
        m = re.search(r'^%s:\s*"?(.*?)"?\s*$' % name, text, re.M)
        return m.group(1) if m else ""

    overview = re.search(r"^overview:\s*>\s*\n((?:\s+.*\n)+)", text, re.M)
    return {
        "guid": field("guid"),
        "name": field("name"),
        "description": field("description"),
        "overview": " ".join(overview.group(1).split()) if overview else field("overview"),
        "owner": field("owner"),
        "category": field("category"),
    }


def now():
    return datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def meta(args):
    info = plugin_info()
    print(json.dumps({
        "category": info["category"],
        "changelog": args.changelog,
        "description": info["description"],
        "guid": info["guid"],
        "name": info["name"],
        "overview": info["overview"],
        "owner": info["owner"],
        "targetAbi": args.target_abi,
        "timestamp": now(),
        "version": args.version,
        "status": "Active",
        "autoUpdate": True,
        "imagePath": "",
        "assemblies": ["Jellyfin.Plugin.Ducks.dll"],
    }, indent=2))


def add(args):
    info = plugin_info()
    try:
        manifest = json.load(open(args.manifest, encoding="utf-8"))
    except FileNotFoundError:
        manifest = []
    entry = next((p for p in manifest if p["guid"] == info["guid"]), None)
    if entry is None:
        entry = dict(info, versions=[])
        manifest.append(entry)
    entry.update({k: v for k, v in info.items()})
    entry["versions"] = [v for v in entry.get("versions", []) if v["version"] != args.version]
    entry["versions"].insert(0, {
        "version": args.version,
        "changelog": args.changelog,
        "targetAbi": args.target_abi,
        "sourceUrl": args.url,
        "checksum": args.checksum,
        "timestamp": now(),
    })
    with open(args.manifest, "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=2)
        f.write("\n")


parser = argparse.ArgumentParser()
sub = parser.add_subparsers(dest="cmd", required=True)
for name in ("meta", "add"):
    p = sub.add_parser(name)
    p.add_argument("--version", required=True)
    p.add_argument("--target-abi", required=True)
    p.add_argument("--changelog", default="")
    if name == "add":
        p.add_argument("--manifest", required=True)
        p.add_argument("--url", required=True)
        p.add_argument("--checksum", required=True)
args = parser.parse_args()
{"meta": meta, "add": add}[args.cmd](args)
