import os

base_dir = r"E:\mylb\Gate"
src_file = os.path.join(base_dir, "Program.cs.bak")

with open(src_file, "r", encoding="utf-8") as f:
    raw_lines = f.readlines()

def get_slice(start_1based, end_1based):
    return "".join(raw_lines[start_1based - 1 : end_1based])

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

def write_module(filename, code):
    out_path = os.path.join(base_dir, filename)
    with open(out_path, "w", encoding="utf-8") as f:
        f.write(header)
        f.write(code.rstrip())
        f.write(footer)
    print(f"Created {filename} ({len(code.splitlines())} lines)")

# 1. Program.cs (Core Lifecycle, Main, RunGate, Trace)
# Lines 28 to 1003 + HandlerMethod (3247 to 3254)
part1 = get_slice(28, 1003) + "\n\n" + get_slice(3247, 3254)
write_module("Program.cs", part1)

# 2. Program.Network.cs (InitializeNetworkAdapter + LocalSigner + gRPC invoker + native imports)
# Lines 1056 to 1165 + Lines 2552 to 3246
part_net = get_slice(1056, 1165) + "\n\n" + get_slice(2552, 3246)
write_module("Program.Network.cs", part_net)

# 3. Program.Accounts.cs (Container coords, admission, acc.json decrypt, keys, seeding, controls, deletion, CloseWaitOverlay)
# Lines 1004 to 1055 + Lines 1166 to 1219 + Lines 1715 to 2339 + Lines 3255 to 3368 + Lines 4828 to 5283 + Lines 5307 to 5334
part_acc = (
    get_slice(1004, 1055) + "\n\n" +
    get_slice(1166, 1219) + "\n\n" +
    get_slice(1715, 2339) + "\n\n" +
    get_slice(3255, 3368) + "\n\n" +
    get_slice(4828, 5283) + "\n\n" +
    get_slice(5307, 5334)
)
write_module("Program.Accounts.cs", part_acc)

# 4. Program.Proxies.cs (Proxy check/enable, distribution, sync, test latency)
# Lines 1220 to 1714 + Lines 2340 to 2551 + Lines 4507 to 4569
part_proxy = (
    get_slice(1220, 1714) + "\n\n" +
    get_slice(2340, 2551) + "\n\n" +
    get_slice(4507, 4569)
)
write_module("Program.Proxies.cs", part_proxy)

# 5. Program.Creation.cs (Device data, single create, bulk create form, hooks, proxy test)
# Lines 4061 to 4506 + Lines 4570 to 4827
part_creation = get_slice(4061, 4506) + "\n\n" + get_slice(4570, 4827)
write_module("Program.Creation.cs", part_creation)

# 6. Program.Bank.cs (Order commands, GBSettings, modular tracer stream)
# Lines 3389 to 4060
part_bank = get_slice(3389, 4060)
write_module("Program.Bank.cs", part_bank)

# 7. Program.Hooks.cs (License state, bypasses, startup hooks, state machine patch)
# Lines 3369 to 3388 + Lines 5284 to 5306 + Lines 5335 to 6484
part_hooks = (
    get_slice(3369, 3388) + "\n\n" +
    get_slice(5284, 5306) + "\n\n" +
    get_slice(5335, 6484)
)
write_module("Program.Hooks.cs", part_hooks)

print("Split completed successfully!")
