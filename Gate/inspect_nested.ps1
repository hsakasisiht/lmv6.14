$null = [System.Reflection.Assembly]::LoadFrom('E:\mylb\LordsBotGate\lib\DevExpress.Data.v26.1.dll')
$null = [System.Reflection.Assembly]::LoadFrom('E:\mylb\LordsBotGate\lib\DevExpress.Utils.v26.1.dll')
$null = [System.Reflection.Assembly]::LoadFrom('E:\mylb\LordsBotGate\lib\DevExpress.XtraGrid.v26.1.dll')
$asm = [System.Reflection.Assembly]::LoadFrom('E:\mylb\LordsBotGate\LordsMobileBot.dll')

$t = $asm.GetType('LordsMobileBot.Roles.SegmentedCollectionRole')
if ($t -ne $null) {
    Write-Host "Found SegmentedCollectionRole: $($t.FullName)"
    foreach ($nested in $t.GetNestedTypes([System.Reflection.BindingFlags]'Public,NonPublic')) {
        Write-Host "  Nested Type: $($nested.FullName)"
        foreach ($m in $nested.GetMethods([System.Reflection.BindingFlags]'Public,NonPublic,Instance,Static')) {
            $p = ($m.GetParameters() | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ', '
            Write-Host "    $(if ($m.IsStatic) {'static'} else {'instance'}) $($m.ReturnType.Name) $($m.Name)($p)"
        }
    }
}
