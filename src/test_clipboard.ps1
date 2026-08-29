# 端到端测试 PasteImageToExplorer.exe（需先运行程序）
# 用法: powershell -NoProfile -STA -ExecutionPolicy Bypass -File test_clipboard.ps1
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$cache = Join-Path $env:TEMP 'ExplorerClipboardImages'
$script:pass = 0
$script:fail = 0

function Assert([string]$name, [bool]$condition) {
    if ($condition) { Write-Host ("[PASS] " + $name); $script:pass++ }
    else { Write-Host ("[FAIL] " + $name) -ForegroundColor Red; $script:fail++ }
}

# 尽力保存用户当前剪贴板，测试结束后恢复
$originalText = $null
$originalImage = $null
$originalFiles = $null
try {
    if ([System.Windows.Forms.Clipboard]::ContainsText()) {
        $originalText = [System.Windows.Forms.Clipboard]::GetText()
    } elseif ([System.Windows.Forms.Clipboard]::ContainsImage()) {
        $originalImage = [System.Windows.Forms.Clipboard]::GetImage()
    } elseif ([System.Windows.Forms.Clipboard]::ContainsFileDropList()) {
        $originalFiles = New-Object System.Collections.Specialized.StringCollection
        foreach ($f in [System.Windows.Forms.Clipboard]::GetFileDropList()) { [void]$originalFiles.Add($f) }
    }
} catch { }

# ---- 测试 1: 位图 -> 文件 + 图像保留 ----
$bmp = New-Object System.Drawing.Bitmap 120, 90
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.Clear([System.Drawing.Color]::FromArgb(255, 40, 160, 80))
$g.Dispose()
[System.Windows.Forms.Clipboard]::SetImage($bmp)
$bmp.Dispose()
Start-Sleep -Milliseconds 1500
Assert "T1 clipboard has file drop list" ([System.Windows.Forms.Clipboard]::ContainsFileDropList())
Assert "T1 clipboard still has image" ([System.Windows.Forms.Clipboard]::ContainsImage())
$png1 = $null
if ([System.Windows.Forms.Clipboard]::ContainsFileDropList()) {
    $png1 = @([System.Windows.Forms.Clipboard]::GetFileDropList())[0]
}
Assert "T1 PNG file created" ($png1 -ne $null -and (Test-Path $png1))
if ($png1 -and (Test-Path $png1)) {
    $img = [System.Drawing.Image]::FromFile($png1)
    Assert "T1 PNG size is 120x90" ($img.Width -eq 120 -and $img.Height -eq 90)
    $img.Dispose()
}

# ---- 测试 2: PNG 格式（带透明通道）-> 字节原样保存 ----
$bmp2 = New-Object System.Drawing.Bitmap 64, 64
$g2 = [System.Drawing.Graphics]::FromImage($bmp2)
$g2.Clear([System.Drawing.Color]::FromArgb(128, 200, 30, 30))
$g2.Dispose()
$ms = New-Object System.IO.MemoryStream
$bmp2.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp2.Dispose()
$pngBytes = $ms.ToArray()
$do = New-Object System.Windows.Forms.DataObject
$do.SetData("PNG", $false, (New-Object System.IO.MemoryStream -ArgumentList @(,$pngBytes)))
[System.Windows.Forms.Clipboard]::SetDataObject($do, $true)
Start-Sleep -Milliseconds 1500
$png2 = $null
if ([System.Windows.Forms.Clipboard]::ContainsFileDropList()) {
    $png2 = @([System.Windows.Forms.Clipboard]::GetFileDropList())[0]
}
Assert "T2 PNG file created" ($png2 -ne $null -and (Test-Path $png2))
if ($png2 -and (Test-Path $png2)) {
    $saved = [System.IO.File]::ReadAllBytes($png2)
    $same = ($saved.Length -eq $pngBytes.Length)
    if ($same) {
        for ($i = 0; $i -lt $saved.Length; $i++) {
            if ($saved[$i] -ne $pngBytes[$i]) { $same = $false; break }
        }
    }
    Assert "T2 PNG bytes preserved (alpha kept)" $same
}
$dataAfter = [System.Windows.Forms.Clipboard]::GetDataObject()
Assert "T2 clipboard still offers PNG format" ($dataAfter -ne $null -and $dataAfter.GetDataPresent("PNG"))

# ---- 测试 3: 复制文件（非图像）不触发转换 ----
$beforeCount = @(Get-ChildItem -LiteralPath $cache -Filter *.png -ErrorAction SilentlyContinue).Count
$fl = New-Object System.Collections.Specialized.StringCollection
[void]$fl.Add($PSCommandPath)
[System.Windows.Forms.Clipboard]::SetFileDropList($fl)
Start-Sleep -Milliseconds 1200
$after = [System.Windows.Forms.Clipboard]::GetFileDropList()
Assert "T3 file copy untouched" (@($after).Count -eq 1 -and @($after)[0] -eq $PSCommandPath)
Assert "T3 no new file generated" (@(Get-ChildItem -LiteralPath $cache -Filter *.png -ErrorAction SilentlyContinue).Count -eq $beforeCount)

# ---- 测试 4: 无自我触发循环 ----
Start-Sleep -Milliseconds 1000
$stable = @(Get-ChildItem -LiteralPath $cache -Filter *.png -ErrorAction SilentlyContinue).Count
Start-Sleep -Milliseconds 2000
Assert "T4 no self-trigger loop" (@(Get-ChildItem -LiteralPath $cache -Filter *.png -ErrorAction SilentlyContinue).Count -eq $stable)

# ---- 恢复用户剪贴板（尽力而为） ----
try {
    if ($originalText -ne $null) {
        [System.Windows.Forms.Clipboard]::SetText($originalText)
    } elseif ($originalImage -ne $null) {
        [System.Windows.Forms.Clipboard]::SetImage($originalImage)
        $originalImage.Dispose()
    } elseif ($originalFiles -ne $null) {
        [System.Windows.Forms.Clipboard]::SetFileDropList($originalFiles)
    }
} catch { }

Write-Host ""
Write-Host ("RESULT: " + $script:pass + " passed, " + $script:fail + " failed")
if ($script:fail -gt 0) { exit 1 }
exit 0
