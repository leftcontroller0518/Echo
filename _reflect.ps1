$ymm = 'C:\YMM4'
$dlls = @(
  'SharpGen.Runtime.dll',
  'Vortice.Mathematics.dll',
  'Vortice.DirectX.dll',
  'Vortice.DXGI.dll',
  'Vortice.Direct2D1.dll',
  'YukkuriMovieMaker.Commons.dll',
  'YukkuriMovieMaker.Plugin.dll'
)
foreach ($d in $dlls) {
  $p = Join-Path $ymm $d
  if (Test-Path $p) {
    try { [void][Reflection.Assembly]::LoadFrom($p); Write-Output "loaded $d" } catch { Write-Output "fail $d $($_.Exception.Message)" }
  } else { Write-Output "missing $d" }
}

$asm = [AppDomain]::CurrentDomain.GetAssemblies() | Where-Object { $_.GetName().Name -eq 'YukkuriMovieMaker.Plugin' } | Select-Object -First 1
try { $types = $asm.GetTypes() } catch { $types = $_.Exception.Types | Where-Object { $_ } }
Write-Output ("typecount " + @($types).Count)
$types | Where-Object { $_.Name -match 'GraphicsDevices|IVideoEffectProcessor|EffectDescription|VideoEffectBase' } | ForEach-Object { $_.FullName }
