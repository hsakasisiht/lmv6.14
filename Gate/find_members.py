import os

src_file = r"E:\mylb\Gate\Program.cs.bak"
with open(src_file, "r", encoding="utf-8") as f:
    lines = f.readlines()

# Remove the outer class Program wrapper
# Line 28: public static class Program
# Line 6484: } (closing Program)

# Let's verify line 28 and 6484
print("Line 28:", repr(lines[27]))
print("Last line:", repr(lines[-1]))

# Find top-level members inside Program:
# When brace_depth == 1 (inside Program class), a new member starts.
