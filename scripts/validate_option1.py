#!/usr/bin/env python3
"""Run each check in a separate process and keep a readable evidence log."""
from pathlib import Path
import os
import subprocess
import tempfile
from xml.sax.saxutils import escape

root = Path(__file__).resolve().parents[1]
os.chdir(root)
log = []
temporary_root = root / '.validation-state'
temporary_root.mkdir(exist_ok=True)

def run(*args):
    result = subprocess.run(args, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    output = '\n'.join(line for line in result.stdout.splitlines() if line.strip())
    print(output, flush=True)
    log.append('$ ' + ' '.join(args) + '\n' + output)
    if result.returncode:
        raise SystemExit(result.returncode)

try:
    # Build the plain task contracts before asking the generator to inspect them.
    run('dotnet', 'build', 'src/Demo.Validation.Tasks', '-v', 'quiet')
    run('dotnet', 'run', '--project', 'src/Demo.Generator', '--', 'src/Demo.Elsa/Generated/TaskActivities.g.cs')
    run('dotnet', 'run', '--project', 'src/Demo.Generator', '--',
        'src/Demo.Validation/Generated/ValidationActivities.g.cs',
        'src/Demo.Validation.Tasks/bin/Debug/net9.0/Demo.Validation.Tasks.dll', 'Demo.Validation.Generated')
    run('dotnet', 'build', 'src/Demo.Validation', '-v', 'quiet')
    run('dotnet', 'run', '--project', 'src/Demo.Validation', '--no-build', '--', 'checks')
    with tempfile.TemporaryDirectory(prefix='state-', dir=temporary_root) as state:
        run('dotnet', 'run', '--project', 'src/Demo.Validation', '--no-build', '--', 'prepare', state)
        run('dotnet', 'run', '--project', 'src/Demo.Validation', '--no-build', '--', 'resume', state)
    # This deliberately wrong C# mapping must fail compilation, not just runtime validation.
    with tempfile.TemporaryDirectory(prefix='type-check-', dir=temporary_root) as directory:
        target = Path(directory)
        reference = escape(str(root / 'src/Demo.Elsa/Demo.Elsa.csproj'))
        (target / 'WrongType.csproj').write_text(f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include="{reference}" /></ItemGroup></Project>')
        (target / 'WrongType.cs').write_text('using Demo.Elsa.Generated; using Elsa.Workflows.Models; public class WrongType { object Build() => new CreateInvoiceActivity { Tax = new Input<string>("wrong") }; }')
        result = subprocess.run(['dotnet', 'build', str(target / 'WrongType.csproj'), '-v', 'quiet'], text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        if result.returncode == 0 or 'CS0029' not in result.stdout or 'Input<string>' not in result.stdout:
            log.append(result.stdout)
            raise SystemExit('FAIL: compiler did not reject the wrong input type as expected.')
        message = 'PASS: Compiler rejects Input<string> for the typed Tax input (CS0029).'
        print(message)
        log.append(message)
    run('dotnet', 'run', '--project', 'src/Demo.Elsa', '--no-build')
    print('OPTION 1 AUTOMATED VALIDATION PASSED. Studio evidence is documented separately.')
    log.append('OPTION 1 AUTOMATED VALIDATION PASSED. Studio evidence is documented separately.')
finally:
    (root / 'docs/validation/option-1-results.txt').write_text('\n\n'.join(log) + '\n')
