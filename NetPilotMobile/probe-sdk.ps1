$ErrorActionPreference = 'SilentlyContinue'
$ProgressPreference = 'SilentlyContinue'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$targets = @(
  @{g='androidx/core'; a='core'; vs=@('1.15.0','1.16.0','1.17.0','1.18.0'); pat='core-{0}.aar'},
  @{g='androidx/compose/ui'; a='ui-android'; vs=@('1.9.0','1.10.4','1.11.0','1.11.4'); pat='ui-android-{0}.aar'},
  @{g='androidx/compose/foundation'; a='foundation-android'; vs=@('1.10.4','1.11.0','1.11.4'); pat='foundation-android-{0}.aar'},
  @{g='androidx/compose/material3'; a='material3-android'; vs=@('1.3.2','1.4.0'); pat='material3-android-{0}.aar'},
  @{g='androidx/lifecycle'; a='lifecycle-runtime-compose-android'; vs=@('2.9.4','2.10.0','2.11.0'); pat='lifecycle-runtime-compose-android-{0}.aar'},
  @{g='androidx/navigation'; a='navigation-compose-android'; vs=@('2.9.4','2.10.0','2.10.2'); pat='navigation-compose-android-{0}.aar'},
  @{g='androidx/activity'; a='activity-compose'; vs=@('1.11.0','1.12.0','1.13.0'); pat='activity-compose-{0}.aar'},
  @{g='androidx/datastore'; a='datastore-preferences-android'; vs=@('1.1.7','1.2.1'); pat='datastore-preferences-android-{0}.aar'},
  @{g='androidx/lifecycle'; a='lifecycle-viewmodel-compose-android'; vs=@('2.9.4','2.10.0'); pat='lifecycle-viewmodel-compose-android-{0}.aar'}
)

$base = 'https://maven.aliyun.com/repository/google'
$work = Join-Path $env:TEMP 'np_probe'
New-Item -ItemType Directory -Force -Path $work | Out-Null

foreach ($t in $targets) {
  foreach ($v in $t.vs) {
    $fn = ($t.pat -f $v)
    $url = "$base/$($t.g)/$($t.a)/$v/$fn"
    $zip = Join-Path $work "$fn.zip"
    try {
      Invoke-WebRequest -Uri $url -OutFile $zip -TimeoutSec 45 -UseBasicParsing -ErrorAction Stop
      $z = [System.IO.Compression.ZipFile]::OpenRead($zip)
      $e = $z.Entries | Where-Object { $_.FullName -eq 'AndroidManifest.xml' } | Select-Object -First 1
      $sdk = 'nomanifest'
      if ($e) {
        $sr = New-Object System.IO.StreamReader($e.Open())
        $txt = $sr.ReadToEnd(); $sr.Close()
        $m = [regex]::Match($txt, 'compileSdkVersion(?:Preview)?="(\d+)"')
        if ($m.Success) { $sdk = $m.Groups[1].Value }
        else {
          $m2 = [regex]::Match($txt, 'targetSdkVersion(?:Preview)?="(\d+)"')
          if ($m2.Success) { $sdk = 'target=' + $m2.Groups[1].Value }
        }
      }
      $z.Dispose()
      Write-Output ("{0,-38} {1,-9} compileSdk={2}" -f $t.a, $v, $sdk)
      Remove-Item $zip -Force
    } catch {
      $msg = $_.Exception.Message
      if ($msg.Length -gt 60) { $msg = $msg.Substring(0, 60) }
      Write-Output ("{0,-38} {1,-9} FAIL {2}" -f $t.a, $v, $msg)
    }
  }
}
