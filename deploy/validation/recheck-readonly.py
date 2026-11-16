"""Run via SSH stdin as ubuntu. Never outputs environment, passwords or keys.

Only reads configuration/DB; starts one disposable PostgreSQL client on HLG's
network (no mounts/ports). PRD connection must fail with an explicit HBA reject.
"""
import io
import json
import subprocess
import tarfile
import hashlib


def run(args, **kwargs):
    return subprocess.run(args, capture_output=True, text=True, **kwargs)


def sql(database, query):
    result = run([
        'docker', 'exec', '-e',
        'PGOPTIONS=-c default_transaction_read_only=on -c statement_timeout=10000',
        'personal-postgres', 'psql', '-X', '-U', 'postgres', '-d', database,
        '-v', 'ON_ERROR_STOP=1', '-Atc', query])
    if result.returncode:
        raise RuntimeError('Read-only SQL failed (details suppressed)')
    print(database + '|' + result.stdout.strip())


# Root-owned configuration is consumed only on the server, never printed.
raw = subprocess.check_output(['sudo', 'cat', '/home/ubuntu/apps/checkpoint-hlg/.env'], text=True)
env = dict(line.split('=', 1) for line in raw.splitlines()
           if line and not line.startswith('#'))
connection = dict((key.strip().lower(), value.strip()) for key, value in
                  (part.split('=', 1) for part in env['ConnectionStrings__Checkpoint'].split(';') if part))
assert connection['host'] == 'personal-postgres'
assert connection['database'] == 'checkpoint_hlg'
assert connection['username'] == 'checkpoint_hlg_app'
pg_image = json.loads(subprocess.check_output([
    'docker', 'inspect', 'personal-postgres'], text=True))[0]['Image']
client = ['docker', 'run', '--rm', '-i', '--network', 'checkpoint-hlg-net',
          '--read-only', '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges',
          '--user', 'postgres', '--entrypoint', 'sh', pg_image, '-c',
          'IFS= read -r PGPASSWORD; export PGPASSWORD; '
          'export PGCONNECT_TIMEOUT=5 PGOPTIONS="-c default_transaction_read_only=on -c statement_timeout=10000"; '
          'exec psql -X -w -h personal-postgres -p 5432 -U checkpoint_hlg_app -d "$1" -Atc "SELECT current_database(),current_user"',
          'sh']
connection_checks_ok = True
for database in ('checkpoint_hlg', 'personal_db'):
    result = run(client + [database], input=connection['password'] + '\n')
    rejected = result.returncode != 0 and 'pg_hba.conf rejects connection' in result.stderr
    ok = result.returncode == 0 if database == 'checkpoint_hlg' else rejected
    connection_checks_ok = connection_checks_ok and ok
    classification = 'PASS' if ok else ('FAIL' if result.returncode == 0 else 'INCONCLUSIVE')
    print(json.dumps({'test': 'real_role_connection', 'database': database,
                      'result': classification,
                      'exit': result.returncode, 'explicit_hba_reject': rejected,
                      'connection_ok': result.returncode == 0}))

for database in ('checkpoint_hlg', 'personal_db'):
    sql(database, """SELECT 'admins='||count(*) FROM checkpoint.admin_users;
        SELECT 'passkeys='||count(*) FROM checkpoint.admin_passkeys;
        SELECT 'seed_users_without_key='||count(*) FROM checkpoint.admin_users u
        WHERE (u.id='5f3f0000-0000-4000-8000-000000000001' AND u.name='Átila'
            OR u.id='5f3f0000-0000-4000-8000-000000000002' AND u.name='Juh')
        AND NOT EXISTS(SELECT 1 FROM checkpoint.admin_passkeys p WHERE p.user_id=u.id);
        SELECT 'migration='||"MigrationId" FROM checkpoint."__EFMigrationsHistory" ORDER BY "MigrationId";
        SELECT 'hlg_schema_usage='||has_schema_privilege('checkpoint_hlg_app','checkpoint','USAGE')
            ||'|hlg_passkeys_select='||has_table_privilege('checkpoint_hlg_app','checkpoint.admin_passkeys','SELECT');
        SELECT 'hlg_connect='||has_database_privilege('checkpoint_hlg_app',current_database(),'CONNECT')
            ||'|public_connect='||EXISTS(SELECT 1 FROM pg_database d,
            LATERAL aclexplode(COALESCE(d.datacl,acldefault('d',d.datdba))) a
            WHERE d.datname=current_database() AND a.grantee=0 AND a.privilege_type='CONNECT');
        SELECT 'hlg_credential_owners='||count(DISTINCT u.id) FROM checkpoint.admin_users u
            JOIN checkpoint.admin_passkeys p ON p.user_id=u.id WHERE u.name LIKE 'HLG-%';""")

# Inspect only relevant assembly literals, never dump configuration or binaries.
info = json.loads(subprocess.check_output(['docker', 'inspect', 'checkpoint-api'], text=True))[0]
print('PRD_IMAGE|' + info['Config']['Image'])
print('PRD_IMAGE_ID|' + info['Image'])
blob = subprocess.check_output(['docker', 'cp', 'checkpoint-api:/app/Checkpoint.Api.dll', '-'])
with tarfile.open(fileobj=io.BytesIO(blob)) as archive:
    assembly = archive.extractfile(next(m for m in archive.getmembers() if m.isfile())).read()
print('PRD_ASSEMBLY_SHA256|' + hashlib.sha256(assembly).hexdigest())
for literal in ('/api/admin/passkeys/invite', '/api/admin/passkeys/register/options',
                '/api/admin/passkeys/register/verify', 'passkey_verified_at',
                'admin-invite:', 'Convite expirado. Solicite outro.', 'register:5f3f0000-0000-4000-8000-000000000001'):
    print('PRD_LITERAL|' + literal + '|' + str(literal.encode('utf-16le') in assembly))
if not connection_checks_ok:
    raise SystemExit(1)
