set shell := ["bash", "-eu", "-o", "pipefail", "-c"]

dotnet := env_var_or_default("DOTNET", "dotnet")
project := "Source/AutoFacingChairs/AutoFacingChairs.fsproj"
source := "Source/AutoFacingChairs/AutoFacingChairs.fs"

fmt:
    {{dotnet}} tool restore
    {{dotnet}} fantomas {{source}}

build:
    {{dotnet}} build {{project}} -c Release

install: build
    python3 scripts/install.py

enable: install
    python3 scripts/enable.py

install-enable: enable
