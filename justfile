set shell := ["bash", "-eu", "-o", "pipefail", "-c"]

dotnet := env_var_or_default("DOTNET", "dotnet")
project := "Source/AutoFacingChairs/AutoFacingChairs.fsproj"

fmt:
    {{dotnet}} tool restore
    {{dotnet}} fantomas Source

legacy_ref := env("RIMWORLD_1_6_REF", "rimworld-1.6")
legacy_dir := "Source/obj/rimworld-1.6"

# Build the RimWorld 1.7 and frozen 1.6 assemblies.
build: build-17 build-16

# Build the RimWorld 1.7 assembly.
build-17:
    {{dotnet}} build {{project}} -c Release

# Build the RimWorld 1.6 assembly from the frozen 1.6 branch.
build-16:
    mkdir -p {{legacy_dir}}
    git archive {{legacy_ref}} Source | tar -x -C {{legacy_dir}}
    {{dotnet}} build {{legacy_dir}}/{{project}} -c Release -p:OutputPath="$PWD/1.6/Assemblies/"

# Zip the built mod for a release.
package version: build
    mkdir -p dist/Auto-Facing-Chairs
    cp -a About Languages LoadFolders.xml 1.6 1.7 dist/Auto-Facing-Chairs/
    cd dist && zip -qr Auto-Facing-Chairs-{{version}}.zip Auto-Facing-Chairs

test:
    {{dotnet}} test Source/AutoFacingChairs.Tests/AutoFacingChairs.Tests.fsproj -c Release

install: build
    python3 scripts/install.py

enable: install
    python3 scripts/enable.py

install-enable: enable
