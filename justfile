set shell := ["bash", "-eu", "-o", "pipefail", "-c"]

dotnet := env_var_or_default("DOTNET", "dotnet")
project := "Source/AutoFacingChairs/AutoFacingChairs.fsproj"

fmt:
    {{dotnet}} tool restore
    {{dotnet}} fantomas Source

build:
    {{dotnet}} build {{project}} -c Release

test:
    {{dotnet}} test Source/AutoFacingChairs.Tests/AutoFacingChairs.Tests.fsproj -c Release

install: build
    python3 scripts/install.py

enable: install
    python3 scripts/enable.py

install-enable: enable
