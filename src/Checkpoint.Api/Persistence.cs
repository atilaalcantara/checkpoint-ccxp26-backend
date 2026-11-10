using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Checkpoint.Api;

public sealed class MigrationDb(DbContextOptions<MigrationDb> options) : DbContext(options);

[DbContext(typeof(MigrationDb))]
[Migration("202611100001_InitialSchema")]
public sealed class InitialSchema : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE SCHEMA IF NOT EXISTS checkpoint;
        CREATE TABLE checkpoint.visitors (id uuid PRIMARY KEY, created_at_utc timestamptz NOT NULL DEFAULT now());
        CREATE TABLE checkpoint.polls (
            id uuid PRIMARY KEY, title text NOT NULL, active boolean NOT NULL DEFAULT true,
            visible boolean NOT NULL DEFAULT true, is_demo boolean NOT NULL DEFAULT false);
        CREATE TABLE checkpoint.poll_options (
            id uuid PRIMARY KEY, poll_id uuid NOT NULL REFERENCES checkpoint.polls(id),
            label text NOT NULL, position integer NOT NULL CHECK(position BETWEEN 0 AND 1),
            icon_key text, tone text, UNIQUE(poll_id,id), UNIQUE(poll_id,position));
        CREATE FUNCTION checkpoint.check_poll_options() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE target uuid;
        BEGIN
            IF TG_TABLE_NAME = 'polls' THEN target := COALESCE(NEW.id, OLD.id);
            ELSE target := COALESCE(NEW.poll_id, OLD.poll_id); END IF;
            IF EXISTS(SELECT 1 FROM checkpoint.polls WHERE id=target) AND
                (SELECT count(*) FROM checkpoint.poll_options WHERE poll_id=target) <> 2 THEN
                RAISE EXCEPTION 'A poll must have exactly two options' USING ERRCODE='23514';
            END IF;
            RETURN NULL;
        END $$;
        CREATE CONSTRAINT TRIGGER poll_has_two_options AFTER INSERT OR UPDATE ON checkpoint.polls
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION checkpoint.check_poll_options();
        CREATE CONSTRAINT TRIGGER options_remain_two AFTER INSERT OR UPDATE OR DELETE ON checkpoint.poll_options
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION checkpoint.check_poll_options();
        CREATE TABLE checkpoint.poll_assignments (
            visitor_id uuid NOT NULL REFERENCES checkpoint.visitors(id),
            poll_id uuid NOT NULL REFERENCES checkpoint.polls(id),
            position integer NOT NULL CHECK(position BETWEEN 0 AND 2),
            PRIMARY KEY(visitor_id,poll_id), UNIQUE(visitor_id,position));
        CREATE TABLE checkpoint.votes (
            visitor_id uuid NOT NULL, poll_id uuid NOT NULL, option_id uuid NOT NULL,
            created_at_utc timestamptz NOT NULL DEFAULT now(), PRIMARY KEY(visitor_id,poll_id),
            FOREIGN KEY(visitor_id,poll_id) REFERENCES checkpoint.poll_assignments(visitor_id,poll_id),
            FOREIGN KEY(poll_id,option_id) REFERENCES checkpoint.poll_options(poll_id,id));
        CREATE INDEX votes_poll_option ON checkpoint.votes(poll_id,option_id);
        CREATE TABLE checkpoint.photos (
            id uuid PRIMARY KEY, visitor_id uuid NOT NULL UNIQUE REFERENCES checkpoint.visitors(id),
            original bytea NOT NULL, media_type text NOT NULL, thumbnail bytea NOT NULL,
            width integer NOT NULL CHECK(width=360), height integer NOT NULL CHECK(height=330),
            created_at_utc timestamptz NOT NULL DEFAULT now());
        CREATE INDEX photos_recent ON checkpoint.photos(created_at_utc DESC,id);
        CREATE TABLE checkpoint.community_progress (
            id integer PRIMARY KEY CHECK(id=1), total_xp bigint NOT NULL CHECK(total_xp BETWEEN 0 AND 9007199254740991),
            revision bigint NOT NULL CHECK(revision>=0), updated_at_utc timestamptz NOT NULL);
        INSERT INTO checkpoint.community_progress VALUES(1,0,0,now());
        CREATE TABLE checkpoint.idempotency_operations (
            id uuid PRIMARY KEY, visitor_id uuid NOT NULL REFERENCES checkpoint.visitors(id),
            key uuid NOT NULL, operation_type text NOT NULL CHECK(operation_type IN ('vote','photo')),
            content_hash text NOT NULL, result_json text NOT NULL, created_at_utc timestamptz NOT NULL DEFAULT now(),
            UNIQUE(visitor_id,key));
        CREATE TABLE checkpoint.result_snapshots (
            id uuid PRIMARY KEY, visitor_id uuid NOT NULL REFERENCES checkpoint.visitors(id),
            created_at_utc timestamptz NOT NULL, expires_at_utc timestamptz NOT NULL, results_json text NOT NULL);
        CREATE INDEX snapshots_expiration ON checkpoint.result_snapshots(expires_at_utc);
        CREATE INDEX snapshots_visitor ON checkpoint.result_snapshots(visitor_id,created_at_utc);
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException("Use an explicit reviewed migration; schema deletion is not automatic.");
}

public sealed class Store(NpgsqlDataSource source)
{
    public async Task<NpgsqlConnection> Open(CancellationToken ct) => await source.OpenConnectionAsync(ct);

    public static Task<int> Execute(NpgsqlConnection connection, string sql, object? args, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition(sql, args, cancellationToken: ct));

    public static async Task LockVisitor(NpgsqlConnection connection, Guid visitorId, bool create, CancellationToken ct)
    {
        // Transaction-scoped advisory locks also serialize first initialization before a row exists.
        // A hash collision only adds serialization; it cannot weaken isolation.
        await Execute(connection, "SELECT pg_advisory_xact_lock(hashtextextended(CAST(@visitorId AS text),0))", new { visitorId }, ct);
        if (create)
            await Execute(connection, "INSERT INTO checkpoint.visitors(id) VALUES(@visitorId) ON CONFLICT DO NOTHING", new { visitorId }, ct);
        else if (!await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM checkpoint.visitors WHERE id=@visitorId)", new { visitorId }, cancellationToken: ct)))
            throw new ApiException(404, "PARTICIPATION_NOT_FOUND", "Inicie sua participação primeiro.");
    }

    public static Task<T> One<T>(NpgsqlConnection connection, string sql, object? args, CancellationToken ct) =>
        connection.QuerySingleAsync<T>(new CommandDefinition(sql, args, cancellationToken: ct));

    public static Task<IEnumerable<T>> Query<T>(NpgsqlConnection connection, string sql, object? args, CancellationToken ct) =>
        connection.QueryAsync<T>(new CommandDefinition(sql, args, cancellationToken: ct));
}

public sealed class CommunityRow
{
    public long TotalXp { get; set; }
    public long Revision { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public CommunityState State => new(TotalXp, Revision.ToString(System.Globalization.CultureInfo.InvariantCulture));
}
public sealed class PollRow
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public Guid? AnswerOptionId { get; set; }
}
public sealed class OptionRow
{
    public Guid Id { get; set; }
    public Guid PollId { get; set; }
    public string Label { get; set; } = "";
    public long Votes { get; set; }
}
public sealed class PhotoRow
{
    public Guid Id { get; set; }
    public byte[] Thumbnail { get; set; } = [];
    public DateTime CreatedAtUtc { get; set; }
}
