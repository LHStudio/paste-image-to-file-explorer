# Keeps a clipboard image available to File Explorer as a pasteable PNG file.
# Run with: powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -File .\PasteImageToExplorer.ps1

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$cacheDirectory = Join-Path $env:TEMP 'ExplorerClipboardImages'
New-Item -ItemType Directory -Path $cacheDirectory -Force | Out-Null

function Remove-OldCacheImages {
    $cutoff = (Get-Date).AddDays(-1)
    Get-ChildItem -LiteralPath $cacheDirectory -Filter '*.png' -File -ErrorAction SilentlyContinue |
        Where-Object LastWriteTime -lt $cutoff |
        Remove-Item -Force -ErrorAction SilentlyContinue
}

function Convert-ClipboardImageToFile {
    try {
        if (-not [Windows.Forms.Clipboard]::ContainsImage()) { return }

        $clipboardImage = [Windows.Forms.Clipboard]::GetImage()
        if ($null -eq $clipboardImage) { return }

        $fileName = 'Screenshot-{0:yyyyMMdd-HHmmssfff}.png' -f (Get-Date)
        $imagePath = Join-Path $cacheDirectory $fileName
        $pngCopy = New-Object System.Drawing.Bitmap $clipboardImage
        try {
            $pngCopy.Save($imagePath, [System.Drawing.Imaging.ImageFormat]::Png)
        }
        finally {
            $pngCopy.Dispose()
        }

        $files = New-Object System.Collections.Specialized.StringCollection
        [void] $files.Add($imagePath)
        [Windows.Forms.Clipboard]::SetFileDropList($files)
        Remove-OldCacheImages
        $script:lastConverted = Get-Date
    }
    catch {
        # Clipboard can be locked briefly by the screenshot application; the next timer tick retries.
    }
}

$script:lastConverted = [datetime]::MinValue
$timer = New-Object Windows.Forms.Timer
$timer.Interval = 250
$timer.Add_Tick({ Convert-ClipboardImageToFile })
$timer.Start()

$trayIcon = New-Object Windows.Forms.NotifyIcon
$trayIcon.Icon = [System.Drawing.SystemIcons]::Information
$trayIcon.Text = 'Paste image to File Explorer'
$trayIcon.Visible = $true
$menu = New-Object Windows.Forms.ContextMenuStrip
$exitItem = $menu.Items.Add('Exit')
$exitItem.Add_Click({
    $timer.Stop()
    $trayIcon.Visible = $false
    [Windows.Forms.Application]::Exit()
})
$trayIcon.ContextMenuStrip = $menu

[Windows.Forms.Application]::Run()
