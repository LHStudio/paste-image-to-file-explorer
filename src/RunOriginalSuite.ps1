$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class TestStation {
  [DllImport("user32.dll")] public static extern IntPtr GetProcessWindowStation();
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern bool GetUserObjectInformation(IntPtr h,int n,StringBuilder s,uint len,out uint need);
}
'@
$name = New-Object Text.StringBuilder 256
[uint32]$needed = 0
if (-not [TestStation]::GetUserObjectInformation([TestStation]::GetProcessWindowStation(), 2, $name, 512, [ref]$needed)) { throw 'Cannot verify station' }
if (-not $env:EXPECTED_TEST_STATION -or $name.ToString() -ne $env:EXPECTED_TEST_STATION -or $name.ToString() -eq 'WinSta0') { throw 'Refusing to test on user clipboard' }
Write-Output ('ISOLATION VERIFIED: ' + $name)
& (Join-Path $PSScriptRoot 'test_clipboard.ps1')
exit $LASTEXITCODE
