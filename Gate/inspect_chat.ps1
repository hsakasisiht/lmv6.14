$null = [System.Reflection.Assembly]::LoadFrom('E:\mylb\LordsBotGate\lib\DevExpress.Data.v26.1.dll')
$null = [System.Reflection.Assembly]::LoadFrom('E:\mylb\LordsBotGate\lib\DevExpress.Utils.v26.1.dll')
$null = [System.Reflection.Assembly]::LoadFrom('E:\mylb\LordsBotGate\lib\DevExpress.XtraGrid.v26.1.dll')
$asm = [System.Reflection.Assembly]::LoadFrom('E:\mylb\LordsBotGate\LordsMobileBot.dll')

$asp = $asm.GetType('LordsMobileBot.Learning.AdvancedSystemPredictor')
if ($asp -ne $null) {
    $m = $asp.GetMethod('PredictFlexibleEngine', [System.Reflection.BindingFlags]'Public,NonPublic,Instance,Static')
    if ($m -ne $null) {
        Write-Host "PredictFlexibleEngine Return: $($m.ReturnType.FullName)"
        foreach ($f in $m.ReturnType.GetFields([System.Reflection.BindingFlags]'Public,NonPublic,Instance')) {
            Write-Host "  Field: $($f.FieldType.Name) $($f.Name)"
        }
    }
}

$crr = $asm.GetType('LordsMobileBot.Roles.CustomReporterRole')
if ($crr -ne $null) {
    $m2 = $crr.GetMethod('GetDefaultSequentialStruct', [System.Reflection.BindingFlags]'Public,NonPublic,Instance,Static')
    if ($m2 -ne $null) {
        Write-Host "GetDefaultSequentialStruct Return: $($m2.ReturnType.FullName)"
        foreach ($f2 in $m2.ReturnType.GetFields([System.Reflection.BindingFlags]'Public,NonPublic,Instance')) {
            Write-Host "  Field: $($f2.FieldType.Name) $($f2.Name)"
        }
    }
}
