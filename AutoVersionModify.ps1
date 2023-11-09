$csprojPath = ".\VMSystem.csproj"

$content = Get-Content -Path $csprojPath -Raw

# �����e���
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
    # �p�G��Ѫ��������|���s�b�A�h�]�m����Ѥ�����Ĥ@�Ӫ���
    $newVersion = "$month.$day.1"
    $content = $content -replace "(<AssemblyVersion>.*?</AssemblyVersion>)", "<AssemblyVersion>$newVersion</AssemblyVersion>"
}

Set-Content -Path $csprojPath -Value $content
Write-Host "Updated AssemblyVersion to: $newVersion"
