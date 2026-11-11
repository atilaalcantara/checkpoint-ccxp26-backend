using System.Text.RegularExpressions;
using Dapper;

namespace Checkpoint.Api;

public sealed record AdminPollInput(string? Question, string? A, string? B, bool Active);
public sealed record SiteStatusInput(string? Status);
public sealed class AdminPollRow { public Guid Id { get; set; } public string Question { get; set; } = ""; public string A { get; set; } = ""; public string B { get; set; } = ""; public bool Active { get; set; } public bool Assigned { get; set; } public long Votes { get; set; } }

public static class AdminManagement
{
    public static void MapAdminManagement(this WebApplication app)
    {
        var admin = app.MapGroup("/api/admin").RequireAuthorization().RequireCors("admin");
        app.MapGet("/api/v1/site-status", async (Store store, CancellationToken ct) => {
            await using var db = await store.Open(ct);
            return Results.Ok(new { status = await Store.One<string>(db, "SELECT status FROM checkpoint.site_status WHERE id=1", null, ct) });
        });
        admin.MapPut("/site-status", async (SiteStatusInput input, Store store, CancellationToken ct) => {
            if (input.Status is not ("live" or "before" or "paused" or "ended")) throw new ApiException(400, "VALIDATION_ERROR", "Estado inválido.");
            await using var db = await store.Open(ct);
            await Store.Execute(db, "UPDATE checkpoint.site_status SET status=@Status WHERE id=1", input, ct);
            return Results.Ok(new { status = input.Status });
        });
        admin.MapGet("/polls", async (Store store, CancellationToken ct) => {
            await using var db = await store.Open(ct);
            var polls = await Store.Query<AdminPollRow>(db, """
                SELECT p.id,p.title AS question,p.active,
                  (SELECT label FROM checkpoint.poll_options WHERE poll_id=p.id AND position=0) AS a,
                  (SELECT label FROM checkpoint.poll_options WHERE poll_id=p.id AND position=1) AS b,
                  EXISTS(SELECT 1 FROM checkpoint.poll_assignments WHERE poll_id=p.id) AS assigned,
                  (SELECT count(*) FROM checkpoint.votes WHERE poll_id=p.id) AS votes
                FROM checkpoint.polls p ORDER BY p.title,p.id
                """, null, ct);
            return Results.Ok(new { polls });
        });
        admin.MapPost("/polls", async (AdminPollInput input, Store store, CancellationToken ct) => {
            var (question, a, b) = Validate(input);
            await using var db = await store.Open(ct); await using var tx = await db.BeginTransactionAsync(ct);
            var id = Guid.NewGuid();
            await Store.Execute(db, "INSERT INTO checkpoint.polls(id,title,active) VALUES(@id,@question,@Active)", new { id, question, input.Active }, ct);
            await Store.Execute(db, "INSERT INTO checkpoint.poll_options(id,poll_id,label,position) VALUES(@aId,@id,@a,0),(@bId,@id,@b,1)", new { id, a, b, aId = Guid.NewGuid(), bId = Guid.NewGuid() }, ct);
            await tx.CommitAsync(ct); return Results.Json(new { id }, statusCode: 201);
        });
        admin.MapPut("/polls/{id:guid}", async (Guid id, AdminPollInput input, Store store, CancellationToken ct) => {
            var (question, a, b) = Validate(input);
            await using var db = await store.Open(ct); await using var tx = await db.BeginTransactionAsync(ct);
            await Store.Execute(db, "SELECT pg_advisory_xact_lock(734619)", null, ct);
            var current = await db.QuerySingleOrDefaultAsync<AdminPollRow>(new CommandDefinition("""
                SELECT p.id,p.title AS question,p.active,
                  (SELECT label FROM checkpoint.poll_options WHERE poll_id=p.id AND position=0) AS a,
                  (SELECT label FROM checkpoint.poll_options WHERE poll_id=p.id AND position=1) AS b,
                  EXISTS(SELECT 1 FROM checkpoint.poll_assignments WHERE poll_id=p.id) AS assigned
                FROM checkpoint.polls p WHERE p.id=@id FOR UPDATE
                """, new { id }, cancellationToken: ct));
            if (current is null) throw new ApiException(404, "POLL_NOT_FOUND", "Enquete não encontrada.");
            if (current.Assigned && (current.Question != question || current.A != a || current.B != b)) throw new ApiException(409, "POLL_ASSIGNED", "Uma enquete já atribuída não pode ter seus textos alterados.");
            await Store.Execute(db, "UPDATE checkpoint.polls SET title=@question,active=@Active WHERE id=@id", new { id, question, input.Active }, ct);
            await Store.Execute(db, "UPDATE checkpoint.poll_options SET label=CASE position WHEN 0 THEN @a ELSE @b END WHERE poll_id=@id", new { id, a, b }, ct);
            await tx.CommitAsync(ct); return Results.NoContent();
        });
        admin.MapDelete("/polls/{id:guid}", async (Guid id, Store store, CancellationToken ct) => {
            await using var db = await store.Open(ct); await using var tx = await db.BeginTransactionAsync(ct);
            await Store.Execute(db, "SELECT pg_advisory_xact_lock(734619)", null, ct);
            if (!await Store.One<bool>(db, "SELECT EXISTS(SELECT 1 FROM checkpoint.polls WHERE id=@id)", new { id }, ct)) throw new ApiException(404, "POLL_NOT_FOUND", "Enquete não encontrada.");
            if (await Store.One<bool>(db, "SELECT EXISTS(SELECT 1 FROM checkpoint.poll_assignments WHERE poll_id=@id)", new { id }, ct)) throw new ApiException(409, "POLL_ASSIGNED", "Uma enquete já atribuída não pode ser excluída.");
            await Store.Execute(db, "DELETE FROM checkpoint.poll_options WHERE poll_id=@id", new { id }, ct);
            await Store.Execute(db, "DELETE FROM checkpoint.polls WHERE id=@id", new { id }, ct);
            await tx.CommitAsync(ct); return Results.NoContent();
        });
        admin.MapGet("/photos", async (int? page, Store store, CancellationToken ct) => {
            var number = page ?? 1; if (number < 1 || number > 100000) throw new ApiException(400, "VALIDATION_ERROR", "Página inválida.");
            await using var db = await store.Open(ct);
            var rows = (await Store.Query<PhotoRow>(db, "SELECT id,thumbnail,created_at_utc FROM checkpoint.photos ORDER BY created_at_utc DESC,id DESC LIMIT 7 OFFSET @offset", new { offset = (number - 1) * 6 }, ct)).ToArray();
            return Results.Ok(new { page = number, hasNext = rows.Length > 6, photos = rows.Take(6).Select(p => new { p.Id, p.CreatedAtUtc, image = Convert.ToBase64String(p.Thumbnail) }) });
        });
        admin.MapDelete("/photos/{id:guid}", async (Guid id, Store store, CancellationToken ct) => {
            await using var db = await store.Open(ct);
            var count = await Store.Execute(db, "DELETE FROM checkpoint.photos WHERE id=@id", new { id }, ct);
            if (count == 0) throw new ApiException(404, "PHOTO_NOT_FOUND", "Foto não encontrada.");
            return Results.NoContent();
        });
    }
    private static (string, string, string) Validate(AdminPollInput input)
    {
        static string Clean(string? s) => Regex.Replace((s ?? "").Trim(), @"\s+", " ");
        var q = Clean(input.Question); var a = Clean(input.A); var b = Clean(input.B);
        if (q.Length is < 1 or > 60 || a.Length is < 1 or > 24 || b.Length is < 1 or > 24 || new[] { q, a, b }.Any(v => v.Split(' ').Any(w => w.EnumerateRunes().Count() > 14)) || string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            throw new ApiException(400, "VALIDATION_ERROR", "Confira os limites e use duas opções diferentes.");
        return (q, a, b);
    }
}
