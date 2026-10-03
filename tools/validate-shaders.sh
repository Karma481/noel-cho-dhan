#!/usr/bin/env bash
# Compiles every HLSL entry point with the real FXC (cs_5_0, the profile used at runtime) and with DXC
# (cs_6_0, as a second opinion), treating every warning as an error.
#
# Windows: run from a Developer Command Prompt / Git Bash where fxc.exe is on PATH.
# Linux:   FXC_EXE=/path/to/fxc.exe WINE=wine64 tools/validate-shaders.sh
#          fxc.exe and d3dcompiler_47.dll ship in the Microsoft.Windows.SDK.CPP NuGet package
#          (c/bin/<sdk-version>/x64/). DXC is optional: DXC_EXE=/path/to/dxc.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
shader="$repo_root/src/AmbientLight.Capture/Shaders/ZoneReduce.hlsl"
entry_points=(CSMain CSEdgeProfile)
output_dir="$(mktemp -d)"
trap 'rm -rf "$output_dir"' EXIT

fxc_exe="${FXC_EXE:-fxc.exe}"
wine="${WINE:-}"

run_fxc() {
    local entry="$1"
    if [[ -n "$wine" ]]; then
        # FXC under Wine needs Windows paths; copy next to the compiler and use relative names.
        local work_dir
        work_dir="$(dirname "$fxc_exe")"
        cp "$shader" "$work_dir/ZoneReduce.hlsl"
        (cd "$work_dir" && WINEDEBUG=-all "$wine" "$(basename "$fxc_exe")" /nologo /T cs_5_0 /E "$entry" /O3 /Ges /WX \
            /Fo "$entry.cso" ZoneReduce.hlsl)
    else
        "$fxc_exe" /nologo /T cs_5_0 /E "$entry" /O3 /Ges /WX /Fo "$output_dir/$entry.cso" "$shader"
    fi
}

for entry in "${entry_points[@]}"; do
    echo "FXC cs_5_0  $entry"
    run_fxc "$entry"
    if [[ -n "${DXC_EXE:-}" ]]; then
        echo "DXC cs_6_0  $entry"
        "$DXC_EXE" -T cs_6_0 -E "$entry" -HV 2018 -WX -Fo "$output_dir/$entry.dxil" "$shader"
    fi
done

echo "All shader entry points compiled without warnings."
