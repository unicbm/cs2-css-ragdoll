param(
    [Parameter(Mandatory=$true)][string]$GameRoot,
    [string]$Source2Viewer="Source2Viewer-CLI",
    [string]$Generator="Visual Studio 18 2026",
    [string]$DotNet="dotnet"
)
$ErrorActionPreference="Stop"

$work=Join-Path $PSScriptRoot "out"
$package=Join-Path $work "agent-package"
$managed=Join-Path $package "addons/counterstrikesharp/plugins/happy-wheel-ragdoll"
$native=Join-Path $package "addons/happy-wheel-ragdoll/bin/win64"
$addon="happy_wheel_ragdoll_dev"
$content=Join-Path $GameRoot "content/csgo_addons/$addon"
$models=Join-Path $work "out-agent"
function CheckExit { if($LASTEXITCODE -ne 0){throw "Build step failed ($LASTEXITCODE)"} }
New-Item -ItemType Directory -Force $managed,$native,"$package/addons/metamod" | Out-Null

cmake -S $PSScriptRoot -B "$work/pose-build" -G $Generator -A x64
CheckExit
cmake --build "$work/pose-build" --config Release
CheckExit
ctest --test-dir "$work/pose-build" -C Release --output-on-failure
CheckExit
& $DotNet build "$PSScriptRoot/managed" -c Release
CheckExit
$agents=Get-Content "$PSScriptRoot/assets/agents.txt"
$filter=($agents | ForEach-Object { "agents/models/$_.vmdl_c" }) -join ","
$vpk="$GameRoot/game/csgo/pak01_dir.vpk"
& $Source2Viewer -i $vpk -f $filter -o "$work/bind-src"
CheckExit
foreach($source in $agents){
    $agent=Split-Path $source -Leaf
    & $Source2Viewer -i "$work/bind-src/agents/models/$source.vmdl_c" -o "$work/bind-src/$agent.glb" -d --gltf_export_format glb --gltf_export_animations --gltf_animation_list __bind_only__ --gltf_mesh_list thirdperson_body,thirdperson_default_gloves
    CheckExit
}
& $Source2Viewer -i $vpk -f $filter -o "$work/agent-export" -d --gltf_export_format glb --gltf_export_materials --gltf_export_extras --gltf_mesh_list thirdperson_body,thirdperson_default_gloves
CheckExit
$materialDir=Join-Path $content "materials/happy_wheel_ragdoll"
New-Item -ItemType Directory -Force $materialDir | Out-Null
Copy-Item "$PSScriptRoot/assets/cut.png","$PSScriptRoot/assets/cut.vmat" $materialDir -Force
& "$GameRoot/game/bin/win64/resourcecompiler.exe" -game "$GameRoot/game/csgo" -addon $addon -nop4 -i "$materialDir/cut.vmat" -outroot $models
CheckExit
python "$PSScriptRoot/assets/build_models.py" $content $work $agents
CheckExit
python "$PSScriptRoot/assets/build_models.py" --bind-poses $work "$managed/bind-poses.json" $agents
CheckExit
python "$PSScriptRoot/assets/build_organs.py" $content
CheckExit
$modelInputs=@($agents | ForEach-Object { "agents/"+(Split-Path $_ -Leaf)+"/*.vmdl" }) + @("organs/porcine_liver.vmdl","organs/ligament.vmdl")
foreach($inputModel in $modelInputs){
    & "$GameRoot/game/bin/win64/resourcecompiler.exe" -game "$GameRoot/game/csgo" -addon $addon -nop4 -i "$content/models/happy_wheel_ragdoll/$inputModel" -outroot $models
    CheckExit
}
New-Item -ItemType Directory -Force "$package/materials" | Out-Null
Copy-Item "$models/csgo_addons/$addon/materials/happy_wheel_ragdoll" "$package/materials" -Recurse -Force
$compiled="$models/csgo_addons/$addon/models/happy_wheel_ragdoll"
$dest="$package/models/happy_wheel_ragdoll"
New-Item -ItemType Directory -Force "$dest/organs" | Out-Null
Copy-Item "$compiled/agents" $dest -Recurse -Force
Copy-Item "$compiled/organs/ligament.vmdl_c","$compiled/organs/porcine_liver.vmdl_c" "$dest/organs" -Force
$retired="$dest/organs/porcine_intestine.vmdl_c"
if(Test-Path -LiteralPath $retired){ Remove-Item -LiteralPath $retired }
Copy-Item "$PSScriptRoot/assets/agents.txt" $managed -Force
Copy-Item "$PSScriptRoot/managed/bin/Release/net10.0/happy-wheel-ragdoll.dll" $managed -Force
Copy-Item "$work/pose-build/Release/HappyWheelNative.dll" $native -Force
@'
"Metamod Plugin"
{
    "alias" "happy-wheel-ragdoll"
    "file" "addons/happy-wheel-ragdoll/bin/win64/HappyWheelNative"
}
'@ | Set-Content "$package/addons/metamod/happy-wheel-ragdoll.vdf" -Encoding ascii
Copy-Item "$PSScriptRoot/assets/pose.json" $managed -Force
Copy-Item "$PSScriptRoot/LICENSE" $package -Force
Copy-Item "$PSScriptRoot/README.md" $package -Force
Compress-Archive -Path "$package/*" -DestinationPath "$work/happy-wheel-ragdoll-win64.zip" -Force
Write-Output "$work/happy-wheel-ragdoll-win64.zip"
