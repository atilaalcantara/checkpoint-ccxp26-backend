\set ON_ERROR_STOP on
\pset pager off
BEGIN ISOLATION LEVEL REPEATABLE READ READ ONLY;
SET LOCAL statement_timeout = '30s';
SELECT now() AS audited_at, current_database(), current_user,
       current_setting('transaction_read_only') AS read_only;
SELECT 'polls' AS object, count(*) AS rows FROM checkpoint.polls
UNION ALL SELECT 'poll_options',count(*) FROM checkpoint.poll_options
UNION ALL SELECT 'poll_assignments',count(*) FROM checkpoint.poll_assignments
UNION ALL SELECT 'votes',count(*) FROM checkpoint.votes
UNION ALL SELECT 'photos',count(*) FROM checkpoint.photos
UNION ALL SELECT 'photo_submissions',count(*) FROM checkpoint.photo_submissions
UNION ALL SELECT 'idempotency_operations',count(*) FROM checkpoint.idempotency_operations
UNION ALL SELECT 'result_snapshots',count(*) FROM checkpoint.result_snapshots
UNION ALL SELECT 'visitors (preserve)',count(*) FROM checkpoint.visitors
UNION ALL SELECT 'admin_users (preserve)',count(*) FROM checkpoint.admin_users
UNION ALL SELECT 'admin_passkeys (preserve)',count(*) FROM checkpoint.admin_passkeys
UNION ALL SELECT 'site_status (preserve)',count(*) FROM checkpoint.site_status
UNION ALL SELECT '__EFMigrationsHistory (preserve)',count(*) FROM checkpoint."__EFMigrationsHistory"
UNION ALL SELECT 'community_progress (update only)',count(*) FROM checkpoint.community_progress
ORDER BY 1;
WITH xp AS (
 SELECT (SELECT count(*) FROM checkpoint.votes) AS vote_xp,
        2*(SELECT count(*) FROM checkpoint.photo_submissions) AS photo_xp,
        2*(SELECT count(*) FROM checkpoint.photos) AS visible_photo_xp
)
SELECT total_xp,revision,vote_xp,photo_xp,visible_photo_xp,
       total_xp-vote_xp-photo_xp AS unexplained_xp,
       total_xp-vote_xp-photo_xp AS projected_xp_after_cleanup,
       revision+1 AS projected_revision
FROM checkpoint.community_progress CROSS JOIN xp WHERE id=1;
SELECT status FROM checkpoint.site_status WHERE id=1;
SELECT active,visible,is_demo,count(*) AS polls FROM checkpoint.polls GROUP BY 1,2,3 ORDER BY 1,2,3;
SELECT operation_type,count(*) FROM checkpoint.idempotency_operations GROUP BY 1;
SELECT count(*) AS photos,coalesce(sum(octet_length(original)),0) AS original_bytes,
       coalesce(sum(octet_length(thumbnail)),0) AS thumbnail_bytes FROM checkpoint.photos;
SELECT 'photos_without_submission' AS check_name,count(*) AS violations
 FROM checkpoint.photos p WHERE NOT EXISTS (SELECT 1 FROM checkpoint.photo_submissions s WHERE s.visitor_id=p.visitor_id)
UNION ALL SELECT 'submissions_without_photo',count(*) FROM checkpoint.photo_submissions s
 WHERE NOT EXISTS (SELECT 1 FROM checkpoint.photos p WHERE p.visitor_id=s.visitor_id)
UNION ALL SELECT 'polls_without_exactly_two_options',count(*) FROM checkpoint.polls p
 WHERE (SELECT count(*) FROM checkpoint.poll_options o WHERE o.poll_id=p.id)<>2
UNION ALL SELECT 'votes_without_assignment',count(*) FROM checkpoint.votes v
 WHERE NOT EXISTS (SELECT 1 FROM checkpoint.poll_assignments a WHERE a.visitor_id=v.visitor_id AND a.poll_id=v.poll_id)
UNION ALL SELECT 'votes_without_option',count(*) FROM checkpoint.votes v
 WHERE NOT EXISTS (SELECT 1 FROM checkpoint.poll_options o WHERE o.poll_id=v.poll_id AND o.id=v.option_id);
SELECT conrelid::regclass AS source,confrelid::regclass AS target,conname,
       pg_get_constraintdef(oid) AS definition,convalidated
FROM pg_constraint WHERE contype='f' AND
 (connamespace='checkpoint'::regnamespace OR confrelid IN
  (SELECT oid FROM pg_class WHERE relnamespace='checkpoint'::regnamespace)) ORDER BY 1,3;
SELECT c.relname,t.tgname,pg_get_triggerdef(t.oid) AS definition
FROM pg_trigger t JOIN pg_class c ON c.oid=t.tgrelid
WHERE c.relnamespace='checkpoint'::regnamespace AND NOT t.tgisinternal;
COMMIT;
