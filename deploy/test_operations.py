"""HLG infrastructure regression tests; no real Docker or application database."""
import copy
import fcntl
import importlib.util
import subprocess
import tempfile
import threading
import time
import unittest
from concurrent.futures import ThreadPoolExecutor, TimeoutError
from pathlib import Path
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('ops', Path(__file__).with_name('operations.py'))
ops = importlib.util.module_from_spec(spec)
spec.loader.exec_module(ops)
API = 'ghcr.io/atilaalcantara/checkpoint-api:hlg-' + 'a' * 40
NEW = API.replace('a' * 40, 'b' * 40)
WEB = API.replace('checkpoint-api', 'checkpoint-web')


class LockTests(unittest.TestCase):
    def test_api_and_web_wait_on_same_real_lock_then_proceed(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'global.lock'
            entered = threading.Event()
            def web():
                with path.open('a') as lock:
                    entered.set()
                    ops.acquire_lock(lock, timeout=2)
                    return 'web deployed'
            with ThreadPoolExecutor(max_workers=1) as pool:
                with path.open('a') as api:
                    ops.acquire_lock(api, timeout=0)
                    result = pool.submit(web)
                    self.assertTrue(entered.wait(1))
                    with self.assertRaises(TimeoutError):
                        result.result(timeout=0.05)
                self.assertEqual(result.result(timeout=3), 'web deployed')
                with path.open('a') as next_release:
                    ops.acquire_lock(next_release, timeout=0)

    def test_timeout_is_bounded_and_preserves_holder_lock(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'global.lock'
            with path.open('a') as holder, path.open('a') as waiter:
                ops.acquire_lock(holder, timeout=0)
                started = time.monotonic()
                with self.assertRaisesRegex(RuntimeError, 'lock wait timed out'):
                    ops.acquire_lock(waiter, timeout=0.05)
                self.assertGreaterEqual(time.monotonic() - started, 0.05)
                self.assertLess(time.monotonic() - started, 1)
                with self.assertRaises(BlockingIOError):
                    fcntl.flock(waiter, fcntl.LOCK_EX | fcntl.LOCK_NB)

    def test_io_errors_are_not_retried(self):
        with patch.object(ops.fcntl, 'flock', side_effect=PermissionError), patch.object(ops.time, 'sleep') as sleep:
            with self.assertRaises(PermissionError):
                ops.acquire_lock(None)
            sleep.assert_not_called()

    def test_timeout_does_not_reach_guard_or_deploy(self):
        with tempfile.TemporaryDirectory() as directory:
            with patch.object(ops, 'LOCK_PATH', Path(directory) / 'lock'), \
                    patch.object(ops.os, 'getuid', return_value=0), \
                    patch.object(ops.sys, 'argv', ['operations.py', 'deploy-api', NEW]), \
                    patch.object(ops, 'acquire_lock', side_effect=RuntimeError('timeout')), \
                    patch.object(ops, 'guard') as guard, patch.object(ops, 'deploy') as deploy:
                with self.assertRaises(RuntimeError):
                    ops.main()
                guard.assert_not_called()
                deploy.assert_not_called()


def container(img, running=True):
    return {'Config': {'Image': img, 'Labels': ops.LABEL.copy()},
            'HostConfig': {'PortBindings': {}}, 'State': {'Running': running},
            'NetworkSettings': {'Networks': {ops.NET: {'IPAddress': '172.20.0.2'}}}}


class Transactions(unittest.TestCase):
    def setUp(self):
        self.state = {'checkpoint-hlg-api': container(API), 'checkpoint-hlg-web': container(WEB)}
        self.calls = []
        self.fail = None
        self.healthfail = None
        for p in (patch.object(ops, 'docker', self.docker),
                  patch.object(ops, 'inspect', lambda n: copy.deepcopy(self.state.get(n))),
                  patch.object(ops, 'health', self.health)):
            p.start()
            self.addCleanup(p.stop)

    def health(self, kind):
        if self.healthfail == kind:
            self.healthfail = None
            raise RuntimeError('injected readiness failure')

    def docker(self, *args, **kwargs):
        self.calls.append(args)
        if self.fail and self.fail(args):
            self.fail = None
            raise RuntimeError('injected Docker failure')
        if args[0] == 'run' and '--migrate-only' not in args:
            self.state[args[args.index('--name') + 1]] = container(args[-1])
        elif args[0] == 'rename':
            self.assertNotIn(args[2], self.state)
            self.state[args[2]] = self.state.pop(args[1])
        elif args[0] in ('start', 'stop'):
            self.state[args[-1]]['State']['Running'] = args[0] == 'start'
        elif args[0] == 'rm':
            self.state.pop(args[-1], None)
        elif args[:2] == ('network', 'disconnect'):
            self.state[args[-1]]['NetworkSettings']['Networks'] = {}
        elif args[:2] == ('network', 'connect'):
            self.state[args[-1]]['NetworkSettings']['Networks'] = {ops.NET: {'IPAddress': '172.20.0.3'}}
        return subprocess.CompletedProcess(args, 0, '', '')

    def test_api_migrates_new_image_before_stop_without_changing_web(self):
        web = copy.deepcopy(self.state['checkpoint-hlg-web'])
        ops.deploy('api', NEW)
        migration = next(c for c in self.calls if '--migrate-only' in c)
        self.assertIn(NEW, migration)
        self.assertLess(self.calls.index(migration), next(i for i, c in enumerate(self.calls) if c[0] == 'stop'))
        self.assertEqual(self.state['checkpoint-hlg-web'], web)
        self.assertEqual(self.state['checkpoint-hlg-api']['Config']['Image'], NEW)
        self.assertEqual(self.state['checkpoint-hlg-api-previous']['NetworkSettings']['Networks'], {})
        self.assertFalse(any('nginx-proxy' in c for c in self.calls))

    def test_migration_failure_preserves_current(self):
        before = copy.deepcopy(self.state)
        self.fail = lambda a: '--migrate-only' in a
        with self.assertRaises(RuntimeError):
            ops.deploy('api', NEW)
        self.assertEqual(self.state, before)
        self.assertFalse(any(c[0] == 'stop' for c in self.calls))

    def test_start_failure_restores_api(self):
        self.fail = lambda a: a[0] == 'run' and '--migrate-only' not in a
        with self.assertRaises(RuntimeError):
            ops.deploy('api', NEW)
        self.assertEqual(self.state['checkpoint-hlg-api']['Config']['Image'], API)
        self.assertTrue(self.state['checkpoint-hlg-api']['State']['Running'])

    def test_dns_smoke_failure_restores_api(self):
        self.healthfail = 'web'
        with self.assertRaises(RuntimeError):
            ops.deploy('api', NEW)
        self.assertEqual(self.state['checkpoint-hlg-api']['Config']['Image'], API)

    def test_web_failure_restores_only_web(self):
        api = copy.deepcopy(self.state['checkpoint-hlg-api'])
        self.healthfail = 'web'
        with self.assertRaises(RuntimeError):
            ops.deploy('web', WEB.replace('a' * 40, 'b' * 40))
        self.assertEqual(self.state['checkpoint-hlg-api'], api)
        self.assertEqual(self.state['checkpoint-hlg-web']['Config']['Image'], WEB)

    def test_manual_rollback_each_component(self):
        for kind in ('api', 'web'):
            name = 'checkpoint-hlg-' + kind
            original = self.state[name]['Config']['Image']
            ops.deploy(kind, original.replace('a' * 40, 'b' * 40))
            ops.rollback(kind)
            self.assertEqual(self.state[name]['Config']['Image'], original)
            self.assertNotIn(name + '-failed', self.state)


class Guards(unittest.TestCase):
    def test_real_connection_shape_and_prd_rejection(self):
        values = {'CHECKPOINT_DEPLOY_TARGET': 'homologation', 'ASPNETCORE_ENVIRONMENT': 'Homologation',
                  'DOTNET_ENVIRONMENT': 'Homologation', 'Database__MigrateOnStartup': 'false',
                  'Database__SeedDemo': 'false', 'Admin__EnableDevLogin': 'false',
                  'DataProtection__KeyRingPath': '/var/lib/checkpoint-data-protection',
                  'Fido2__ServerDomain': ops.DOMAIN, 'Fido2__Origins__0': 'https://' + ops.DOMAIN,
                  'Cors__Origins__0': 'https://' + ops.DOMAIN,
                  'ConnectionStrings__Checkpoint': 'Host=personal-postgres;Database=checkpoint_hlg;Username=checkpoint_hlg_app;Search Path=checkpoint;Password=test;Maximum Pool Size=5;Minimum Pool Size=0;Timeout=5;Command Timeout=15;Application Name=checkpoint-hlg'}
        with tempfile.TemporaryDirectory() as directory:
            base = Path(directory)
            (base / 'web-nginx.conf').write_text('resolver 127.0.0.11 valid=10s ipv6=off;\nset $api http://checkpoint-hlg-api:8080;\nproxy_pass $api;')
            def write(v):
                (base / '.env').write_text('\n'.join(k + '=' + val for k, val in v.items()))
            with patch.object(ops, 'BASE', base), patch.object(ops, 'trusted'), patch.object(ops, 'docker'):
                write(values)
                ops.guard()
                for key, bad in [('DOTNET_ENVIRONMENT', 'Production'),
                                 ('CHECKPOINT_DEPLOY_TARGET', 'production'),
                                 ('ConnectionStrings__Checkpoint', values['ConnectionStrings__Checkpoint'].replace('checkpoint_hlg;', 'personal_db;')),
                                 ('ConnectionStrings__Checkpoint', values['ConnectionStrings__Checkpoint'] + ';User Id=postgres'),
                                 ('ConnectionStrings__Checkpoint', values['ConnectionStrings__Checkpoint'] + ';Database=personal_db')]:
                    with self.subTest(key=key, value=bad):
                        write(dict(values, **{key: bad}))
                        with self.assertRaises(RuntimeError):
                            ops.guard()


if __name__ == '__main__':
    unittest.main()
