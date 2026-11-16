import os, sys

src_file = r"E:\mylb\Gate\Program.cs.bak"
with open(src_file, "r", encoding="utf-8") as f:
    clean_lines = []
    for l in f:
        s = l.strip()
        if s.startswith("#region") or s.startswith("#endregion"):
            clean_lines.append("// " + s + "\n")
        else:
            clean_lines.append(l)

# Parse top-level members
depth = 0
in_program = False
member_start = None
members = []

for idx, line in enumerate(clean_lines):
    stripped = line.strip()
    code_part = line.split("//")[0]
    open_count = code_part.count("{")
    close_count = code_part.count("}")
    
    prev_depth = depth
    depth += (open_count - close_count)
    
    if not in_program:
        if "public static class Program" in line:
            in_program = True
        continue
    
    if in_program and depth == 1 and prev_depth == 1:
        if member_start is None and stripped and not stripped.startswith("//"):
            member_start = idx
        elif member_start is not None and (stripped.endswith(";") or stripped.endswith("}")):
            members.append((member_start, idx, clean_lines[member_start].strip()))
            member_start = None
    elif in_program and depth == 1 and prev_depth > 1:
        if member_start is not None:
            members.append((member_start, idx, clean_lines[member_start].strip()))
            member_start = None
    elif in_program and depth == 0:
        if member_start is not None:
            members.append((member_start, idx - 1, clean_lines[member_start].strip()))
            member_start = None
        break

print(f"Total members parsed: {len(members)}")

mapping = {
    "Program.cs": [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 45, 119],
    "Program.Network.cs": [15, 17, 39, 40, 41, 42, 43, 44],
    "Program.Accounts.cs": [12, 13, 14, 16, 18, 19, 21, 28, 29, 30, 31, 32, 33, 34, 35, 46, 49, 50, 51, 83, 84, 85, 86, 87, 88, 89, 90, 91, 92, 93, 94, 99],
    "Program.Proxies.cs": [22, 23, 24, 25, 26, 27, 36, 37, 38, 74, 75],
    "Program.Creation.cs": [20, 65, 66, 67, 68, 69, 70, 71, 72, 73, 76, 77, 78, 79, 80, 81, 82],
    "Program.Bank.cs": [53, 54, 55, 56, 57, 58, 59, 60, 61, 62, 63, 64],
    "Program.Hooks.cs": [47, 48, 52, 95, 96, 97, 98, 100, 101, 102, 103, 104, 105, 106, 107, 108, 109, 110, 111, 112, 113, 114, 115, 116, 117, 118]
}

# Verify all members accounted for
assigned = set()
for k, v in mapping.items():
    assigned.update(v)

all_indices = set(range(len(members)))
missing = all_indices - assigned
if missing:
    print(f"Warning: missing indices: {missing}")
    for m in missing:
        print(f"  {m}: {members[m][2]}")
else:
    print("All 120 members accounted for!")

header = """using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DevExpress.LookAndFeel;
using DevExpress.XtraGrid.Views.Grid;
using DevExpress.XtraGrid.Views.Base;
using Google.Protobuf;
using Google.Protobuf.Collections;
using Grpc.Core;
using LordsMobileBot;

namespace LordsBotGate;

public static partial class Program
{
"""

footer = "\n}\n"

base_dir = r"E:\mylb\Gate"
for filename, indices in mapping.items():
    content = []
    for idx in sorted(indices):
        s, e, _ = members[idx]
        block = "".join(clean_lines[s : e + 1])
        content.append(block.strip())
    
    body = "\n\n".join(content)
    out_path = os.path.join(base_dir, filename)
    with open(out_path, "w", encoding="utf-8") as f:
        f.write(header)
        f.write(body)
        f.write(footer)
    print(f"Wrote {filename}: {len(body.splitlines())} lines")

print("All files written successfully!")
