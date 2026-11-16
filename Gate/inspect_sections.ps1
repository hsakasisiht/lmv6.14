$matches = Select-String -Path 'E:\mylb\Gate\Program.cs' -Pattern '^[ ]*(// ===|private static |public static |internal static )'
foreach ($m in $matches) {
    if ($m.Line -match '(void|Task|bool|int|string|BotSchema|HandlerMethod|Action|\()') {
        Write-Host "$($m.LineNumber): $($m.Line.Trim())"
    }
}
