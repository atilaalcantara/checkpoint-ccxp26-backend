#!/usr/bin/python3
"""HLG operations installed by root at /usr/local/lib/checkpoint-hlg/operations.py.

Independent releases/previous containers, new-image migration before API stop,
automatic compensation on error, no database downgrade, no PRD proxy reload.
"""
import fcntl
import json
import os
import re
import stat
import subprocess
import sys
import time
import urllib.request
from pathlib import Path

BASE = Path('/home/ubuntu/apps/checkpoint-hlg')
NET = 'checkpoint-hlg-net'
VOLUME = 'checkpoint-hlg-data-protection'
DOMAIN = 'ccxp26-hlg.atilaalcantara.com'
LABEL = {'checkpoint.environment': 'homologation'}
LOCK_PATH = Path('/run/lock/checkpoint-hlg-deploy.lock')
LOCK_WAIT_SECONDS = 600

def acquire_lock(lock, timeout=LOCK_WAIT_SECONDS):
    """Serialize independent API/web callers with a monotonic, bounded wait."""
    deadline = time.monotonic() + timeout
    while True:
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
            return
        except BlockingIOError:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise RuntimeError('HLG global deploy lock wait timed out; retry operation') from None
            time.sleep(min(0.25, remaining))

def docker(*args, check=True):
    p = subprocess.run(['/usr/bin/docker', *args], stdin=subprocess.DEVNULL,
                       capture_output=True, text=True, timeout=300)
    if check and p.returncode:
        raise RuntimeError('Docker operation failed: ' + args[0])
    return p

def inspect(name):
    p = docker('container', 'inspect', name, check=False)
    if p.returncode:
        # Do not mistake a daemon failure for a missing container.
        docker('info', '--format', '{{.ServerVersion}}')
        return None
    return json.loads(p.stdout)[0]

def image(kind, value):
    if not re.fullmatch(r'ghcr\.io/atilaalcantara/checkpoint-' + kind + r':hlg-[0-9a-f]{40}', value):
        raise ValueError('Image outside HLG allowlist')
    return value

def trusted(path, private=False):
    s = path.lstat()
    if not stat.S_ISREG(s.st_mode) or s.st_uid != 0 or s.st_mode & (0o077 if private else 0o022):
        raise RuntimeError('Unsafe root-owned configuration')

def parse_pairs(lines, separator):
    result = {}
    for line in lines:
        if not line or line.startswith('#'):
            continue
        if separator not in line or '\r' in line or '\n' in line:
            raise RuntimeError('Noncanonical HLG configuration')
        key, value = line.split(separator, 1)
        folded = key.lower()
        if folded in result or key != key.strip():
            raise RuntimeError('Duplicate/noncanonical HLG configuration')
        result[folded] = value
    return result

def guard():
    trusted(BASE / '.env', private=True)
    raw = (BASE / '.env').read_text()
    if '\r' in raw:
        raise RuntimeError('Noncanonical env-file')
    values = parse_pairs(raw.splitlines(), '=')
    canonical = ('CHECKPOINT_DEPLOY_TARGET', 'ASPNETCORE_ENVIRONMENT', 'DOTNET_ENVIRONMENT',
                 'ConnectionStrings__Checkpoint', 'Database__MigrateOnStartup', 'Database__SeedDemo',
                 'Admin__EnableDevLogin', 'DataProtection__KeyRingPath', 'Fido2__ServerDomain',
                 'Fido2__Origins__0', 'Cors__Origins__0')
    original = dict(line.split('=', 1) for line in raw.splitlines() if line and not line.startswith('#'))
    if any(key not in original for key in canonical):
        raise RuntimeError('Missing canonical runtime env key')
    if any(not re.fullmatch(r'[a-z_][a-z0-9_]*', k) for k in values):
        raise RuntimeError('Noncanonical env-file key')
    required = {
        'checkpoint_deploy_target': 'homologation',
        'aspnetcore_environment': 'Homologation', 'dotnet_environment': 'Homologation',
        'database__migrateonstartup': 'false', 'database__seeddemo': 'false',
        'admin__enabledevlogin': 'false',
        'dataprotection__keyringpath': '/var/lib/checkpoint-data-protection',
        'fido2__serverdomain': DOMAIN, 'fido2__origins__0': 'https://' + DOMAIN,
        'cors__origins__0': 'https://' + DOMAIN,
    }
    if any(values.get(k) != v for k, v in required.items()):
        raise RuntimeError('HLG environment guard rejected configuration')
    conn = parse_pairs(values.get('connectionstrings__checkpoint', '').split(';'), '=')
    required_conn = {'host': 'personal-postgres', 'database': 'checkpoint_hlg',
                     'username': 'checkpoint_hlg_app', 'search path': 'checkpoint'}
    allowed = set(required_conn) | {'password', 'port', 'maximum pool size', 'minimum pool size',
                                  'timeout', 'command timeout', 'application name', 'pooling', 'ssl mode', 'trust server certificate'}
    if (any(conn.get(k) != v for k, v in required_conn.items()) or set(conn) - allowed
            or not conn.get('password') or conn.get('port', '5432') != '5432'
            or conn.get('application name', 'checkpoint-hlg') != 'checkpoint-hlg'):
        raise RuntimeError('HLG database guard rejected configuration')
    docker('network', 'inspect', NET)
    docker('volume', 'inspect', VOLUME)
    # Runtime-variable proxy_pass re-resolves Docker DNS, including after rollback.
    trusted(BASE / 'web-nginx.conf')
    config = (BASE / 'web-nginx.conf').read_text()
    if ('resolver 127.0.0.11 valid=10s ipv6=off;' not in config
            or 'set $api http://checkpoint-hlg-api:8080;' not in config
            or 'proxy_pass $api;' not in config or 'checkpoint-api:' in config):
        raise RuntimeError('HLG DNS configuration guard rejected configuration')

def managed(kind, name):
    c = inspect(name)
    if c:
        image(kind, c['Config']['Image'])
        if (c['Config'].get('Labels') or {}).get('checkpoint.environment') != 'homologation':
            raise RuntimeError('Unmanaged container: refusing operation')
        if c['HostConfig'].get('PortBindings'):
            raise RuntimeError('HLG container has published ports')
        if set(c['NetworkSettings']['Networks']) - {NET}:
            raise RuntimeError('HLG container on unexpected network')
    return c

def health(kind):
    name = 'checkpoint-hlg-' + kind
    paths = ['/health/ready'] if kind == 'api' else ['/', '/api/v1/site-status']
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    for _ in range(60):
        c = inspect(name)
        if c and c['State']['Running']:
            ip = c['NetworkSettings']['Networks'][NET]['IPAddress']
            try:
                for path in paths:
                    req = urllib.request.Request('http://' + ip + (':8080' if kind == 'api' else ':80') + path,
                            headers={'X-Forwarded-Proto': 'https', 'Host': DOMAIN})
                    with opener.open(req, timeout=3) as response:
                        if response.status != 200:
                            raise RuntimeError('Bad readiness status')
                if kind == 'web':
                    docker('exec', name, 'nginx', '-t')
                return
            except Exception:
                pass
        time.sleep(2)
    raise RuntimeError('HLG readiness check failed: ' + name)

def api_args():
    return ['--network', NET, '--env-file', str(BASE / '.env'), '--user', '1654:1654',
            '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges:true', '--pids-limit', '128',
            '--memory', '768m', '--cpus', '1',
            '--mount', 'type=volume,src=' + VOLUME + ',dst=/var/lib/checkpoint-data-protection']

def launch(kind, img):
    args = ['run', '-d', '--name', 'checkpoint-hlg-' + kind, '--restart', 'unless-stopped',
            '--label', 'checkpoint.environment=homologation']
    if kind == 'api':
        args += api_args()
    else:
        args += ['--network', NET, '--security-opt', 'no-new-privileges:true', '--pids-limit', '128',
                 '--memory', '128m', '--cpus', '0.25', '--mount',
                 'type=bind,src=' + str(BASE / 'web-nginx.conf') + ',dst=/etc/nginx/conf.d/default.conf,readonly']
    docker(*args, img)

def detach(name):
    c = inspect(name)
    if c and NET in c['NetworkSettings']['Networks']:
        docker('network', 'disconnect', NET, name)

def restore(kind, source, target):
    docker('rename', source, target)
    c = inspect(target)
    if NET not in c['NetworkSettings']['Networks']:
        docker('network', 'connect', NET, target)
    docker('start', target)
    health(kind)

def check_web_after_api():
    if managed('web', 'checkpoint-hlg-web'):
        # Wait through DNS's 10s cache. No shared nginx-proxy reload/restart.
        health('web')

def deploy(kind, img):
    name = 'checkpoint-hlg-' + kind
    previous = name + '-previous'
    old = managed(kind, name)
    prior = managed(kind, previous)
    if inspect(name + '-failed'):
        raise RuntimeError('Previous failed rollback requires operator review')
    if prior and prior['State']['Running']:
        raise RuntimeError('Previous container unexpectedly running')
    if kind == 'api':
        managed('web', 'checkpoint-hlg-web')
        if inspect(name + '-migrate'):
            raise RuntimeError('Migration container already exists')
    docker('pull', img)
    if kind == 'api':
        # NEW release image and dedicated temporary name; current API stays up.
        docker('run', '--rm', '--name', name + '-migrate', '--label', 'checkpoint.environment=homologation',
               *api_args(), '--entrypoint', 'dotnet', img, 'Checkpoint.Api.dll', '--migrate-only')
    moved = False
    attempted = False
    stopped = False
    try:
        if old:
            if prior:
                docker('rm', previous)
            stopped = True
            docker('stop', '--time', '30', name)
            docker('rename', name, previous)
            moved = True
            detach(previous)
        attempted = True
        launch(kind, img)
        health(kind)
        if kind == 'api':
            check_web_after_api()
    except Exception:
        if attempted and inspect(name):
            docker('rm', '-f', name)
        if moved:
            restore(kind, previous, name)
            if kind == 'api':
                check_web_after_api()
        elif stopped:
            docker('start', name)
            health(kind)
        raise RuntimeError('Deploy failed; previous HLG container restored or bootstrap candidate removed') from None
    print('HLG ' + kind + ' deploy healthy; previous version retained. Schema is not downgraded.')

def rollback(kind):
    name = 'checkpoint-hlg-' + kind
    previous, failed = name + '-previous', name + '-failed'
    old = managed(kind, name)
    prior = managed(kind, previous)
    if not prior or prior['State']['Running'] or inspect(failed):
        raise RuntimeError('No stopped managed previous version or rollback needs operator review')
    if kind == 'api':
        managed('web', 'checkpoint-hlg-web')
    moved = False
    stopped = False
    try:
        if old:
            stopped = True
            docker('stop', '--time', '30', name)
            docker('rename', name, failed)
            moved = True
            detach(failed)
        restore(kind, previous, name)
        if kind == 'api':
            check_web_after_api()
    except Exception:
        if not inspect(previous) and inspect(name):
            docker('stop', '--time', '30', name)
            docker('rename', name, previous)
            detach(previous)
        if moved:
            restore(kind, failed, name)
            if kind == 'api':
                check_web_after_api()
        elif stopped:
            docker('start', name)
            health(kind)
        raise RuntimeError('Rollback failed; current HLG version restored') from None
    if moved:
        docker('rm', failed)
    print('HLG ' + kind + ' rollback healthy. Database schema unchanged.')

def main():
    if os.getuid() != 0:
        raise ValueError('Root-only operations')
    args = sys.argv[1:]
    # Defense in depth: direct root entry is also strictly validated.
    if args != ['status'] and args not in (['rollback-api'], ['rollback-web']):
        if len(args) != 2 or args[0] not in ('deploy-api', 'deploy-web'):
            raise ValueError('Invalid operation')
        image(args[0][7:], args[1])
    with open(LOCK_PATH, 'a') as lock:
        acquire_lock(lock)
        if args == ['status']:
            for kind in ('api', 'web'):
                for suffix in ('', '-previous'):
                    name = 'checkpoint-hlg-' + kind + suffix
                    c = inspect(name)
                    print(json.dumps({'name': name, 'image': c['Config']['Image'] if c else None,
                                      'state': c['State']['Status'] if c else 'absent'}))
            return
        guard()
        kind = args[0].split('-')[1]
        if args[0].startswith('deploy-'):
            deploy(kind, args[1])
        else:
            rollback(kind)

if __name__ == '__main__':
    try:
        main()
    except Exception as exc:
        # Never print env, credentials, application output or Docker stderr.
        print('HLG operation rejected/failed: ' + (str(exc) if isinstance(exc, (ValueError, RuntimeError))
              else 'operation failed; operator review required'), file=sys.stderr)
        sys.exit(1)
