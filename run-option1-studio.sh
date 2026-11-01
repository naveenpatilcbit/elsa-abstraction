#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
dotnet run --project src/Demo.Generator -- src/Demo.Elsa/Generated/TaskActivities.g.cs
python3 scripts/run_option1_studio.py
