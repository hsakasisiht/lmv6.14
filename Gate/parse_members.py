import re

src_file = r"E:\mylb\Gate\Program.cs.bak"
with open(src_file, "r", encoding="utf-8") as f:
    lines = f.readlines()

# Clean up #region / #endregion
clean_lines = []
for l in lines:
    stripped = l.strip()
    if stripped.startswith("#region") or stripped.startswith("#endregion"):
        clean_lines.append("// " + stripped + "\n")
    else:
        clean_lines.append(l)

# Let's find top-level members inside Program class
# Program class starts at line 28 (0-indexed 27) with '{' at line 29 (0-indexed 28)
depth = 0
in_program = False
member_start = None
members = []

for idx, line in enumerate(clean_lines):
    stripped = line.strip()
    
    # Track depth by counting braces (simple heuristic, ignore comments)
    code_part = line.split("//")[0]
    
    # Handle string literals simple check
    open_count = code_part.count("{")
    close_count = code_part.count("}")
    
    prev_depth = depth
    depth += (open_count - close_count)
    
    if not in_program:
        if "public static class Program" in line:
            in_program = True
        continue
    
    if in_program and depth == 1 and prev_depth == 1:
        # We are at the root level inside Program
        if member_start is None and stripped and not stripped.startswith("//"):
            member_start = idx
        elif member_start is not None and (stripped.endswith(";") or stripped.endswith("}")):
            members.append((member_start, idx, clean_lines[member_start].strip()))
            member_start = None
    elif in_program and depth == 1 and prev_depth > 1:
        # A block just finished at depth 1
        if member_start is not None:
            members.append((member_start, idx, clean_lines[member_start].strip()))
            member_start = None
    elif in_program and depth == 0:
        # Exited Program class
        if member_start is not None:
            members.append((member_start, idx - 1, clean_lines[member_start].strip()))
            member_start = None
        break

print(f"Discovered {len(members)} top-level members inside Program class:")
for s, e, text in members[:25]:
    print(f"Lines {s+1}-{e+1}: {text[:70]}")
