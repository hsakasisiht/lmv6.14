#!/usr/bin/env python3
"""
Lords Mobile Live Game Version Resolver
Dual-source hybrid resolution:
  Source 1: Apple iTunes Storefront CDN (Major.Minor)
  Source 2: IGG Official Windows Update Plist (Build / Patch)
"""
import sys
import json
import re
import urllib.request

def resolve_version():
    major = 2
    minor = 208
    build = 321

    # Source 1: Apple AppStore
    try:
        req1 = urllib.request.Request("https://itunes.apple.com/lookup?id=1071976327", headers={"User-Agent": "Mozilla/5.0"})
        with urllib.request.urlopen(req1, timeout=6) as resp:
            data = json.loads(resp.read().decode("utf-8"))
            if data.get("results") and len(data["results"]) > 0:
                v = data["results"][0].get("version", "")
                m = re.match(r"^(\d+)\.(\d+)", v)
                if m:
                    major = int(m.group(1))
                    minor = int(m.group(2))
    except Exception as e:
        pass

    # Source 2: IGG Update.plist
    try:
        req2 = urllib.request.Request("http://download-lo-snd.igg.com/win6/Update.plist", headers={"User-Agent": "Mozilla/5.0"})
        with urllib.request.urlopen(req2, timeout=6) as resp:
            text = resp.read().decode("utf-8")
            line1 = text.strip().splitlines()[0]
            parts = line1.split("/")
            if len(parts) >= 3 and parts[2].isdigit():
                build = int(parts[2])
    except Exception as e:
        pass

    return f"{major}.{minor}.{build}"

if __name__ == "__main__":
    ver = resolve_version()
    print(ver)
