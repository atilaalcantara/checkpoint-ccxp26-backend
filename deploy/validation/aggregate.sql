-- Run separately against checkpoint_hlg and personal_db over SSH.
-- No credentials or individual identifiers are returned.
BEGIN READ ONLY;
SELECT current_database() AS database,
  (SELECT count(*) FROM checkpoint.admin_users) AS admins,
  (SELECT count(*) FROM checkpoint.admin_passkeys) AS passkeys,
  (SELECT count(*) FROM checkpoint.polls) AS polls,
  (SELECT count(*) FROM checkpoint.votes) AS votes,
  (SELECT count(*) FROM checkpoint.photos) AS photos;
SELECT count(*) AS hlg_credential_owners
FROM checkpoint.admin_passkeys p JOIN checkpoint.admin_users u ON u.id=p.user_id
WHERE u.name LIKE 'HLG-%';
SELECT has_schema_privilege('checkpoint_hlg_app','checkpoint','USAGE') AS hlg_schema_usage,
       has_table_privilege('checkpoint_hlg_app','checkpoint.admin_passkeys','SELECT') AS hlg_passkeys_select;
SELECT datname,has_database_privilege('checkpoint_hlg_app',oid,'CONNECT') AS hlg_connect
FROM pg_database WHERE datname IN ('checkpoint_hlg','personal_db');
-- CONNECT can be inherited from PUBLIC; it is not an end-to-end connection test.
SELECT d.datname, EXISTS(SELECT 1
  FROM aclexplode(COALESCE(d.datacl,acldefault('d',d.datdba))) a
  WHERE a.grantee=0 AND a.privilege_type='CONNECT') AS public_connect
FROM pg_database d WHERE d.datname IN ('checkpoint_hlg','personal_db');
SELECT count(*) AS migration_seed_users_without_passkey
FROM checkpoint.admin_users u
WHERE ((u.id='5f3f0000-0000-4000-8000-000000000001' AND u.name='Átila')
    OR (u.id='5f3f0000-0000-4000-8000-000000000002' AND u.name='Juh'))
  AND NOT EXISTS(SELECT 1 FROM checkpoint.admin_passkeys p WHERE p.user_id=u.id);
SELECT "MigrationId" FROM checkpoint."__EFMigrationsHistory" ORDER BY "MigrationId";
COMMIT;
