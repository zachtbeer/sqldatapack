#!/usr/bin/env bash
# Reformats C# with JetBrains `jb cleanupcode`, driven entirely by the root .editorconfig.
#
# The detour through a generated .sln is not optional. `jb cleanupcode` cannot read
# SqlDataPack.slnx: pointed at the .slnx it loads zero files and still exits 0, and pointed at
# a .csproj it treats the project file itself as the only thing to clean. Only the classic .sln
# format enumerates the C# sources. See https://youtrack.jetbrains.com/issue/RSRP-500988.
#
# Usage:
#   build/cleanup.sh                # clean the whole solution
#   build/cleanup.sh a.cs b.cs      # clean only these paths (repo-relative)
set -euo pipefail

repo_root=$(git rev-parse --show-toplevel)
cd "$repo_root"

sln=jb-cleanup.sln
trap 'rm -f "$repo_root/$sln"' EXIT
rm -f "$sln"

dotnet new sln -n "${sln%.sln}" -o . --format sln >/dev/null
# `dotnet sln list` prints a "Project(s)" header and a rule before the paths.
projects=()
while IFS= read -r project; do
  project=${project%$'\r'}
  projects+=("$project")
done < <(dotnet sln SqlDataPack.slnx list | tail -n +3)
dotnet sln "$sln" add "${projects[@]}" >/dev/null

args=(cleanupcode "$sln" --profile="Built-in: Reformat Code" --verbosity=WARN)
if [ "$#" -gt 0 ]; then
  args+=("--include=$(IFS=';'; echo "$*")")
fi

dotnet tool run jb "${args[@]}"
