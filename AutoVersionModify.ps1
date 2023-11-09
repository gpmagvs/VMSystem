$csprojPath = ".\VMSystem.csproj"

$content = Get-Content -Path $csprojPath -Raw

# 獲取當前日期
$date = Get-Date
$month = $date.Month
$day = $date.Day

if ($content -match "<AssemblyVersion>$month.$day.(.*?)<\/AssemblyVersion>") {
    $old_serialNumber = [int]$matches[1]
    $serialNumber = $old_serialNumber + 1
    $newVersion = "$month.$day.$serialNumber"
    $content = $content.Replace("<AssemblyVersion>$month.$day.$old_serialNumber</AssemblyVersion>", "<AssemblyVersion>$newVersion</AssemblyVersion>")
} else {
    Write-Host "Write defual AssemblyVersion to: $newVersion"
    # 如果當天的版本號尚未存在，則設置為當天日期的第一個版本
    $newVersion = "$month.$day.1"
    $content = $content -replace "(<AssemblyVersion>.*?</AssemblyVersion>)", "<AssemblyVersion>$newVersion</AssemblyVersion>"
}

Set-Content -Path $csprojPath -Value $content
Write-Host "Updated AssemblyVersion to: $newVersion"
