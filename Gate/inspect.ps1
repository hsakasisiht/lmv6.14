Add-Type -Path "E:\mylb\LordsBotGate\lib\DevExpress.Data.v26.1.dll" -ErrorAction SilentlyContinue
Add-Type -Path "E:\mylb\LordsBotGate\lib\DevExpress.Utils.v26.1.dll" -ErrorAction SilentlyContinue
Add-Type -Path "E:\mylb\LordsBotGate\lib\DevExpress.XtraGrid.v26.1.dll" -ErrorAction SilentlyContinue
$asm = [System.Reflection.Assembly]::LoadFrom("E:\mylb\LordsBotGate\LordsMobileBot.dll")
try {
    $types = $asm.GetTypes()
} catch [System.Reflection.ReflectionTypeLoadException] {
    $types = $Error[0].Exception.Types | Where-Object { $_ -ne $null }
}
$t = $types | Where-Object { $_.Name -eq "accountRecord" }
Write-Host "=== PROPERTIES ==="
foreach ($p in $t.GetProperties()) {
    Write-Host "$($p.Name) : $($p.PropertyType.FullName)"
}
Write-Host "=== FIELDS ==="
foreach ($f in $t.GetFields([System.Reflection.BindingFlags]"Public,NonPublic,Instance")) {
    Write-Host "$($f.Name) : $($f.FieldType.FullName)"
}
