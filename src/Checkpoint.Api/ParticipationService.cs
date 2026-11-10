using System.Security.Cryptography;
using Npgsql;

namespace Checkpoint.Api;

public sealed class ParticipationService(Store store)
{
    public async Task<ParticipationResponse> Participate(Guid visitorId, CancellationToken ct)
    {
        await using var connection = await store.Open(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await Store.LockVisitor(connection, visitorId, true, ct);
        var existing = (await Store.Query<Guid>(connection,
            "SELECT poll_id FROM checkpoint.poll_assignments WHERE visitor_id=@visitorId ORDER BY position", new { visitorId }, ct)).ToArray();
        var candidates = await Store.Query<Guid>(connection, """
            SELECT id FROM checkpoint.polls WHERE active AND id <> ALL(@existing)
            ORDER BY random() LIMIT @remaining
            """, new { existing, remaining = 3 - existing.Length }, ct);
        var position = existing.Length;
        foreach (var pollId in candidates)
            await Store.Execute(connection, "INSERT INTO checkpoint.poll_assignments VALUES(@visitorId,@pollId,@position)", new { visitorId, pollId, position = position++ }, ct);
        var polls = await Store.Query<PollRow>(connection, """
            SELECT p.id,p.title,v.option_id AS answer_option_id FROM checkpoint.poll_assignments a
            JOIN checkpoint.polls p ON p.id=a.poll_id
            LEFT JOIN checkpoint.votes v ON v.visitor_id=a.visitor_id AND v.poll_id=a.poll_id
            WHERE a.visitor_id=@visitorId ORDER BY a.position
            """, new { visitorId }, ct);
        var options = (await Store.Query<OptionRow>(connection, """
            SELECT o.id,o.poll_id,o.label FROM checkpoint.poll_options o JOIN checkpoint.poll_assignments a ON a.poll_id=o.poll_id
            WHERE a.visitor_id=@visitorId ORDER BY o.position
            """, new { visitorId }, ct)).ToArray();
        var assigned = polls.Select(p => new AssignedPoll(p.Id, p.Title,
            options.Where(o => o.PollId == p.Id).Select(o => new PollOption(o.Id, o.Label)).ToArray(),
            p.AnswerOptionId, p.AnswerOptionId is null)).ToArray();
        var answered = assigned.Count(p => p.AnswerOptionId is not null);
        var photo = await Store.One<bool>(connection, "SELECT EXISTS(SELECT 1 FROM checkpoint.photos WHERE visitor_id=@visitorId)", new { visitorId }, ct);
        await transaction.CommitAsync(ct);
        return new(answered == 3 ? "completed" : assigned.Length < 3 ? "waiting_for_polls" : "ready",
            3, assigned.Length, answered, answered == 3, photo ? "submitted" : "not_submitted", assigned);
    }

    public async Task<VoteResponse> Vote(Guid visitorId, VoteRequest request, CancellationToken ct)
    {
        await using var connection = await store.Open(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await Store.LockVisitor(connection, visitorId, false, ct);
        if (!await Store.One<bool>(connection, "SELECT EXISTS(SELECT 1 FROM checkpoint.polls WHERE id=@PollId)", request, ct))
            throw new ApiException(404, "POLL_NOT_FOUND", "Enquete não encontrada.");
        if (!await Store.One<bool>(connection, "SELECT EXISTS(SELECT 1 FROM checkpoint.poll_assignments WHERE visitor_id=@visitorId AND poll_id=@PollId)", new { visitorId, request.PollId }, ct))
            throw new ApiException(409, "POLL_NOT_ASSIGNED", "Esta enquete não foi atribuída a você.");
        if (!await Store.One<bool>(connection, "SELECT EXISTS(SELECT 1 FROM checkpoint.poll_options WHERE poll_id=@PollId AND id=@OptionId)", request, ct))
            throw new ApiException(400, "VALIDATION_ERROR", "Alternativa inválida para esta enquete.");
        if (await Store.One<bool>(connection, "SELECT EXISTS(SELECT 1 FROM checkpoint.votes WHERE visitor_id=@visitorId AND poll_id=@PollId)", new { visitorId, request.PollId }, ct))
            throw new ApiException(409, "ALREADY_VOTED", "Você já respondeu esta enquete.");
        await Store.Execute(connection, "INSERT INTO checkpoint.votes(visitor_id,poll_id,option_id) VALUES(@visitorId,@PollId,@OptionId)", new { visitorId, request.PollId, request.OptionId }, ct);
        var community = await Award(connection, 1, ct);
        var answered = await Store.One<int>(connection, "SELECT count(*)::integer FROM checkpoint.votes WHERE visitor_id=@visitorId", new { visitorId }, ct);
        await transaction.CommitAsync(ct);
        return new VoteResponse(request.PollId, request.OptionId, 1, answered, 3, answered == 3, community);
    }

    public async Task<PhotoResponse> Photo(Guid visitorId, byte[] original, ImageProcessor processor, CancellationToken ct)
    {
        // Check before encoding; the transaction rechecks under the visitor lock after encoding.
        await using (var preflight = await store.Open(ct))
        {
            if (!await Store.One<bool>(preflight, "SELECT EXISTS(SELECT 1 FROM checkpoint.visitors WHERE id=@visitorId)", new { visitorId }, ct))
                throw new ApiException(404, "PARTICIPATION_NOT_FOUND", "Inicie sua participação primeiro.");
            await CheckPhoto(preflight, visitorId, ct);
        }
        var processed = await processor.Process(original, ct);
        await using var connection = await store.Open(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await Store.LockVisitor(connection, visitorId, false, ct);
        await CheckPhoto(connection, visitorId, ct);
        var photoId = Guid.NewGuid();
        await Store.Execute(connection, """
            INSERT INTO checkpoint.photos(id,visitor_id,original,media_type,thumbnail,width,height)
            VALUES(@photoId,@visitorId,@original,@mediaType,@thumbnail,360,330)
            """, new { photoId, visitorId, original, processed.MediaType, processed.Thumbnail }, ct);
        var community = await Award(connection, 2, ct);
        await transaction.CommitAsync(ct);
        return new PhotoResponse(photoId, "submitted", 2, community);
    }

    private static async Task CheckPhoto(NpgsqlConnection connection, Guid visitorId, CancellationToken ct)
    {
        if (await Store.One<bool>(connection, "SELECT EXISTS(SELECT 1 FROM checkpoint.photos WHERE visitor_id=@visitorId)", new { visitorId }, ct))
            throw new ApiException(409, "PHOTO_ALREADY_SUBMITTED", "Sua foto já foi enviada.");
    }

    private static async Task<CommunityState> Award(NpgsqlConnection connection, int xp, CancellationToken ct) =>
        (await Store.One<CommunityRow>(connection, """
            UPDATE checkpoint.community_progress SET total_xp=total_xp+@xp, revision=revision+1,updated_at_utc=clock_timestamp()
            WHERE id=1 RETURNING *
            """, new { xp }, ct)).State;

    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
