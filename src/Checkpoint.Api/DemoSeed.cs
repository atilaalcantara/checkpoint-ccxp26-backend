namespace Checkpoint.Api;

public static class DemoSeed
{
    public static async Task Run(Store store, CancellationToken ct)
    {
        string[][] polls =
        [
            ["Quem venceria numa luta?", "Harry Potter", "Arthur Morgan"],
            ["Pizza preferida?", "Calabresa", "Portuguesa"],
            ["Praia ou montanha?", "Praia", "Montanha"],
            ["Café ou chá?", "Café", "Chá"],
            ["Doce ou salgado?", "Doce", "Salgado"]
        ];
        await using var connection = await store.Open(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await Store.Execute(connection, "SELECT pg_advisory_xact_lock(26001110)", null, ct);
        for (var i = 0; i < polls.Length; i++)
        {
            var poll = polls[i];
            var pollId = Guid.Parse($"26000000-0000-4000-8000-{i + 1:000000000000}");
            await Store.Execute(connection, "INSERT INTO checkpoint.polls(id,title,is_demo) VALUES(@pollId,@title,true) ON CONFLICT DO NOTHING", new { pollId, title = poll[0] }, ct);
            for (var j = 0; j < 2; j++)
            {
                var id = Guid.Parse($"26000001-0000-4000-8000-{i * 2 + j + 1:000000000000}");
                await Store.Execute(connection, """
                    INSERT INTO checkpoint.poll_options(id,poll_id,label,position)
                    VALUES(@id,@pollId,@label,@position) ON CONFLICT DO NOTHING
                    """, new { id, pollId, label = poll[1 + j], position = j }, ct);
            }
        }
        await transaction.CommitAsync(ct);
    }
}
