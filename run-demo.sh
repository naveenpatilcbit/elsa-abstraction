#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
dotnet run --project src/Demo.Generator -- src/Demo.Elsa/Generated/TaskActivities.g.cs
dotnet run --project src/Demo.Elsa
