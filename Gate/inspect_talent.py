import subprocess

cmd = [r'C:\Users\dd092\.dotnet\tools\ilspycmd.cmd', '-il', '-t', 'LordsMobileBot.AppClasses.AccountManager.accountRecord+<idnkuf>d__349', r'E:\mylb\LordsBotGate\LordsMobileBot.dll']
p = subprocess.run(cmd, capture_output=True, text=True)
for line in p.stdout.splitlines():
    if 'call' in line:
        print(line)
