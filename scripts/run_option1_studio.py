#!/usr/bin/env python3
"""Start the local validation Server and Studio; Ctrl+C stops both."""
import os
from pathlib import Path
import secrets
import socket
import subprocess
import time

root = Path(__file__).resolve().parents[1]
os.chdir(root)
for port in (5210, 5211):
    with socket.socket() as sock:
        if sock.connect_ex(('localhost', port)) == 0:
            raise SystemExit(f'Port {port} is already in use. The validation demo may already be running.')
for project in ('Demo.Validation.Server', 'Demo.Validation.Studio'):
    subprocess.run(['dotnet', 'build', f'src/{project}', '-v', 'quiet'], check=True)
env = os.environ.copy()
env.setdefault('Validation__SigningKey', secrets.token_hex(32))
env.setdefault('Validation__Password', secrets.token_urlsafe(18))
env.setdefault('Logging__LogLevel__Default', 'Warning')
logs = root / '.validation-state'
logs.mkdir(exist_ok=True)
children = []
try:
    for project, extra in [('Demo.Validation.Server', []), ('Demo.Validation.Studio', ['--urls', 'http://localhost:5211'])]:
        with (logs / f'{project}.log').open('w') as output:
            children.append(subprocess.Popen(['dotnet', 'run', '--no-build', '--project', f'src/{project}', *extra], env=env, stdout=output, stderr=subprocess.STDOUT))
    print('Studio: http://localhost:5211/workflows/definitions', flush=True)
    print('User: validation', flush=True)
    print('Local demo password: ' + env['Validation__Password'], flush=True)
    print('Logs: .validation-state/. Press Ctrl+C to stop both processes.', flush=True)
    while all(child.poll() is None for child in children):
        time.sleep(1)
    raise SystemExit('A demo process stopped. See the logs above.')
except KeyboardInterrupt:
    pass
finally:
    for child in children:
        if child.poll() is None:
            child.terminate()
    for child in children:
        try:
            child.wait(timeout=10)
        except subprocess.TimeoutExpired:
            child.kill()
            child.wait()
