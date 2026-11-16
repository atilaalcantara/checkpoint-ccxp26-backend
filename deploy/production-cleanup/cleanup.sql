-- DESTRUCTIVE, PREPARED ONLY. Run through remote.sh cleanup AFTER HLG and backup validation
-- and new explicit user authorization. Never execute as a dry-run.
-- Scope: ALL polls/votes/uploaded photos and derived participation state.
-- Preserve visitors, admins, passkeys, site_status, migrations and external config.
\set ON_ERROR_STOP on
\pset pager off
BEGIN;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '120s';
SET LOCAL idle_in_transaction_session_timeout = '60s';
-- Lock status first: RequireLive uses FOR SHARE. Fail rather than deadlock/hang.
LOCK TABLE checkpoint.site_status IN SHARE MODE;
LOCK TABLE checkpoint.visitors, checkpoint.admin_users, checkpoint.admin_passkeys,
 checkpoint."__EFMigrationsHistory" IN SHARE MODE;
LOCK TABLE checkpoint.community_progress, checkpoint.polls, checkpoint.poll_options,
 checkpoint.poll_assignments, checkpoint.votes, checkpoint.photos,
 checkpoint.photo_submissions, checkpoint.idempotency_operations,
 checkpoint.result_snapshots IN EXCLUSIVE MODE;
SET CONSTRAINTS ALL DEFERRED;
DO $$
BEGIN
 IF current_database()<>'personal_db' THEN RAISE EXCEPTION 'Wrong database'; END IF;
 IF (SELECT count(*) FROM checkpoint.site_status)<>1 OR
    NOT EXISTS (SELECT 1 FROM checkpoint.site_status WHERE id=1 AND status IN ('before','paused','ended'))
 THEN RAISE EXCEPTION 'Site must already be non-live; this script does not change config'; END IF;
 IF (SELECT count(*) FROM checkpoint.community_progress)<>1 OR
    NOT EXISTS (SELECT 1 FROM checkpoint.community_progress WHERE id=1)
 THEN RAISE EXCEPTION 'Invalid community singleton'; END IF;
 IF EXISTS (SELECT 1 FROM checkpoint.photos p WHERE NOT EXISTS
    (SELECT 1 FROM checkpoint.photo_submissions s WHERE s.visitor_id=p.visitor_id))
 THEN RAISE EXCEPTION 'Photos without XP marker: review required'; END IF;
 IF (SELECT total_xp FROM checkpoint.community_progress WHERE id=1)<>
    (SELECT count(*) FROM checkpoint.votes)+2*(SELECT count(*) FROM checkpoint.photo_submissions)
 THEN RAISE EXCEPTION 'Unexplained XP: do not reset or guess'; END IF;
 -- External FK dependencies require a renewed review; never use CASCADE.
 IF EXISTS (SELECT 1 FROM pg_constraint c WHERE c.contype='f'
   AND c.confrelid IN ('checkpoint.polls'::regclass,'checkpoint.poll_options'::regclass,
    'checkpoint.poll_assignments'::regclass,'checkpoint.votes'::regclass,
    'checkpoint.photos'::regclass,'checkpoint.photo_submissions'::regclass,
    'checkpoint.idempotency_operations'::regclass,'checkpoint.result_snapshots'::regclass)
   AND c.conrelid NOT IN ('checkpoint.polls'::regclass,'checkpoint.poll_options'::regclass,
    'checkpoint.poll_assignments'::regclass,'checkpoint.votes'::regclass,
    'checkpoint.photos'::regclass,'checkpoint.photo_submissions'::regclass,
    'checkpoint.idempotency_operations'::regclass,'checkpoint.result_snapshots'::regclass))
 THEN RAISE EXCEPTION 'Unexpected inbound FK'; END IF;
END $$;
-- Transaction-local equality snapshots; never returned to client/log.
CREATE TEMP TABLE preserved_admin_users ON COMMIT DROP AS TABLE checkpoint.admin_users;
CREATE TEMP TABLE preserved_admin_passkeys ON COMMIT DROP AS TABLE checkpoint.admin_passkeys;
CREATE TEMP TABLE preserved_status ON COMMIT DROP AS TABLE checkpoint.site_status;
CREATE TEMP TABLE preserved_migrations ON COMMIT DROP AS TABLE checkpoint."__EFMigrationsHistory";
CREATE TEMP TABLE preserved_visitors ON COMMIT DROP AS TABLE checkpoint.visitors;
CREATE TEMP TABLE previous_progress ON COMMIT DROP AS TABLE checkpoint.community_progress;
DELETE FROM checkpoint.result_snapshots;
DELETE FROM checkpoint.idempotency_operations;
DELETE FROM checkpoint.votes;
DELETE FROM checkpoint.poll_assignments;
DELETE FROM checkpoint.poll_options;
DELETE FROM checkpoint.polls;
DELETE FROM checkpoint.photos;
DELETE FROM checkpoint.photo_submissions;
-- At this point reconciled vote XP and historical submission XP have all been removed.
UPDATE checkpoint.community_progress
 SET total_xp=0,revision=revision+1,updated_at_utc=clock_timestamp() WHERE id=1;
SET CONSTRAINTS ALL IMMEDIATE;
DO $$
DECLARE pair text[]; found_difference boolean;
BEGIN
 FOREACH pair SLICE 1 IN ARRAY ARRAY[
  ['checkpoint.admin_users','preserved_admin_users'],
  ['checkpoint.admin_passkeys','preserved_admin_passkeys'],
  ['checkpoint.site_status','preserved_status'],
  ['checkpoint."__EFMigrationsHistory"','preserved_migrations'],
  ['checkpoint.visitors','preserved_visitors']]
 LOOP
  EXECUTE format('SELECT EXISTS ((TABLE %s EXCEPT ALL TABLE %I) UNION ALL (TABLE %I EXCEPT ALL TABLE %s))',pair[1],pair[2],pair[2],pair[1]) INTO STRICT found_difference;
  IF found_difference THEN RAISE EXCEPTION 'Preservation failed: %',pair[1]; END IF;
 END LOOP;
END $$;
-- Final assertions below are deliberately evaluated before COMMIT.
DO $$
BEGIN
 IF EXISTS (SELECT 1 FROM checkpoint.votes) OR EXISTS (SELECT 1 FROM checkpoint.poll_assignments)
 OR EXISTS (SELECT 1 FROM checkpoint.poll_options) OR EXISTS (SELECT 1 FROM checkpoint.polls)
 OR EXISTS (SELECT 1 FROM checkpoint.photos) OR EXISTS (SELECT 1 FROM checkpoint.photo_submissions)
 OR EXISTS (SELECT 1 FROM checkpoint.idempotency_operations) OR EXISTS (SELECT 1 FROM checkpoint.result_snapshots)
 THEN RAISE EXCEPTION 'Cleanup incomplete'; END IF;
 IF NOT EXISTS (SELECT 1 FROM checkpoint.community_progress c CROSS JOIN previous_progress p
  WHERE c.id=1 AND c.total_xp=0 AND c.revision=p.revision+1)
 THEN RAISE EXCEPTION 'Progress postcondition failed'; END IF;
END $$;
SELECT total_xp,revision FROM checkpoint.community_progress WHERE id=1;
COMMIT;
