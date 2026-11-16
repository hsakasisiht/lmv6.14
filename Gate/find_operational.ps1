$null = [System.Reflection.Assembly]::LoadFrom('E:\mylb\LordsBotGate\lib\DevExpress.Data.v26.1.dll')
$null = [System.Reflection.Assembly]::LoadFrom('E:\mylb\LordsBotGate\lib\DevExpress.Utils.v26.1.dll')
$null = [System.Reflection.Assembly]::LoadFrom('E:\mylb\LordsBotGate\lib\DevExpress.XtraGrid.v26.1.dll')
$asm = [System.Reflection.Assembly]::LoadFrom('E:\mylb\LordsBotGate\LordsMobileBot.dll')

try {
    $types = $asm.GetTypes()
} catch [System.Reflection.ReflectionTypeLoadException] {
    $types = $_.Exception.Types | Where-Object { $_ -ne $null }
}

$matched = $types | Where-Object { $_.Name -like '*Segmented*' -or $_.Name -like '*Operational*' -or $_.Name -like '*Authorizer*' }
foreach ($m in $matched) {
    Write-Host "Matched: $($m.FullName)"
    foreach ($nested in $m.GetNestedTypes([System.Reflection.BindingFlags]'Public,NonPublic')) {
        Write-Host "  Nested: $($nested.FullName)"
        foreach ($method in $nested.GetMethods([System.Reflection.BindingFlags]'Public,NonPublic,Instance,Static')) {
            if ($method.Name -like '*Stop*' -or $method.Name -like '*Sender*' -or $method.Name -like '*Authorizer*') {
                $p = ($method.GetParameters() | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ', '
                Write-Host "    $(if ($method.IsStatic) {'static'} else {'instance'}) $($method.ReturnType.Name) $($method.Name)($p)"
            }
        }
    }
}
