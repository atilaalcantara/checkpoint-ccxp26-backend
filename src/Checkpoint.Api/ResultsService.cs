namespace Checkpoint.Api;

public sealed class ResultsService(Store store, TimeProvider clock)
{
    public async Task<ResultsResponse> Get(Guid visitorId, CancellationToken ct)
    {
        await using var connection = await store.Open(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await Store.LockVisitor(connection, visitorId, false, ct);
        var answers = await Store.One<int>(connection, "SELECT count(*)::integer FROM checkpoint.votes WHERE visitor_id=@visitorId", new { visitorId }, ct);
        if (answers != 3) throw new ApiException(403, "RESULTS_LOCKED", "Responda às três enquetes para ver os resultados.");

        var asOfUtc = clock.GetUtcNow().UtcDateTime;
        // A single statement reads all poll texts and counts from one PostgreSQL MVCC snapshot.
        var options = await Store.Query<ResultAggregate>(connection, """
            SELECT p.id AS poll_id,p.title,o.id,o.label,o.position,count(v.option_id) AS votes
            FROM checkpoint.polls p JOIN checkpoint.poll_options o ON o.poll_id=p.id
            LEFT JOIN checkpoint.votes v ON v.poll_id=p.id AND v.option_id=o.id
            WHERE p.visible GROUP BY p.id,p.title,o.id,o.label,o.position
            ORDER BY p.id,o.position
            """, null, ct);
        var results = options.GroupBy(o => o.PollId)
            .Select(g => new PollResult(g.Key, g.First().Title, g.Sum(o => o.Votes),
                g.Select(o => new ResultOption(o.Id, o.Label, o.Votes)).ToArray()))
            .Where(p => p.TotalVotes > 0)
            .OrderBy(p => p.Title, StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo("pt-BR"), ignoreCase: true))
            .ThenBy(p => p.PollId).ToArray();
        await transaction.CommitAsync(ct);
        return new(asOfUtc, results);
    }
}

public sealed class ResultAggregate
{
    public Guid PollId { get; set; }
    public string Title { get; set; } = "";
    public Guid Id { get; set; }
    public string Label { get; set; } = "";
    public long Votes { get; set; }
}
