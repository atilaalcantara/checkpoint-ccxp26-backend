\set ON_ERROR_STOP on
\pset pager off
BEGIN ISOLATION LEVEL REPEATABLE READ READ ONLY;
SET LOCAL statement_timeout='30s';
DO $$
BEGIN
 IF EXISTS (SELECT 1 FROM checkpoint.polls) OR EXISTS (SELECT 1 FROM checkpoint.poll_options)
 OR EXISTS (SELECT 1 FROM checkpoint.poll_assignments) OR EXISTS (SELECT 1 FROM checkpoint.votes)
 OR EXISTS (SELECT 1 FROM checkpoint.photos) OR EXISTS (SELECT 1 FROM checkpoint.photo_submissions)
 OR EXISTS (SELECT 1 FROM checkpoint.idempotency_operations) OR EXISTS (SELECT 1 FROM checkpoint.result_snapshots)
 THEN RAISE EXCEPTION 'Target tables not empty'; END IF;
 IF (SELECT count(*) FROM checkpoint.community_progress)<>1 OR NOT EXISTS
 (SELECT 1 FROM checkpoint.community_progress WHERE id=1 AND total_xp=0)
 THEN RAISE EXCEPTION 'Expected singleton with zero XP'; END IF;
END $$;
-- Review against the immediately preceding maintenance snapshot, not an old baseline.
SELECT total_xp,revision FROM checkpoint.community_progress WHERE id=1;
SELECT 'admin_users' AS object,count(*) FROM checkpoint.admin_users
UNION ALL SELECT 'admin_passkeys',count(*) FROM checkpoint.admin_passkeys
UNION ALL SELECT 'visitors',count(*) FROM checkpoint.visitors
UNION ALL SELECT 'migrations',count(*) FROM checkpoint."__EFMigrationsHistory";
SELECT status FROM checkpoint.site_status WHERE id=1;
COMMIT;
