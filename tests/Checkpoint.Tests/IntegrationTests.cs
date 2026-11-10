using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Checkpoint.Api;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NetVips;
using Xunit;
using Xunit.Abstractions;

namespace Checkpoint.Tests;

[CollectionDefinition("PostgreSQL", DisableParallelization = true)]
public sealed class DatabaseCollection : ICollectionFixture<ApiFixture>;

[Collection("PostgreSQL")]
public sealed class IntegrationTests(ApiFixture fixture, ITestOutputHelper output)
{
    private async Task<(HttpStatusCode Status, JsonObject Body)> Send(HttpMethod method, string path, Guid? visitor = null, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        if (visitor is not null) request.Headers.Add("X-Visitor-Id", visitor.ToString());
        using var response = await fixture.Client.SendAsync(request);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        if (visitor is not null && path.StartsWith("/api/v1", StringComparison.Ordinal))
            Assert.True(response.Headers.CacheControl?.NoStore);
        return (response.StatusCode, body);
    }

    private async Task<JsonObject> Participate(Guid visitor)
    {
        var response = await Send(HttpMethod.Put, "/api/v1/participation", visitor);
        Assert.Equal(HttpStatusCode.OK, response.Status);
        return response.Body;
    }

    private static JsonContent Ballot(JsonNode poll, int option = 0) => JsonContent.Create(new
    {
        pollId = poll["id"]!.GetValue<Guid>(),
        optionId = poll["options"]![option]!["id"]!.GetValue<Guid>()
    });

    private async Task Complete(Guid visitor)
    {
        var participation = await Participate(visitor);
        foreach (var poll in participation["polls"]!.AsArray())
        {
            var vote = await Send(HttpMethod.Post, "/api/v1/votes", visitor, Ballot(poll!));
            Assert.Equal(HttpStatusCode.Created, vote.Status);
        }
    }

    private static void Error((HttpStatusCode Status, JsonObject Body) response, int status, string code)
    {
        Assert.Equal((HttpStatusCode)status, response.Status);
        Assert.Equal(code, (string?)response.Body["error"]?["code"]);
    }

    [Fact]
    public async Task Assignments_are_stable_under_concurrent_initialization_and_only_fill_missing_slots()
    {
        var visitor = Guid.NewGuid();
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Participate(visitor)));
        var expected = responses[0]["polls"]!.ToJsonString();
        Assert.All(responses, response => Assert.Equal(expected, response["polls"]!.ToJsonString()));
        Assert.Equal(3, responses[0]["polls"]!.AsArray().Select(p => (Guid)p!["id"]!).Distinct().Count());
        foreach (var poll in responses[0]["polls"]!.AsArray())
            foreach (var option in poll!["options"]!.AsArray())
            {
                Assert.Null(option!["iconKey"]);
                Assert.Null(option["tone"]);
            }

        await fixture.Execute("UPDATE checkpoint.polls SET active=false");
        try
        {
            var empty = await Participate(Guid.NewGuid());
            Assert.Empty(empty["polls"]!.AsArray());
            Assert.Equal("waiting_for_polls", (string?)empty["status"]);
            await fixture.Execute("UPDATE checkpoint.polls SET active=true WHERE id=(SELECT id FROM checkpoint.polls ORDER BY id LIMIT 1)");
            var waitingVisitor = Guid.NewGuid();
            var waiting = await Participate(waitingVisitor);
            Assert.Equal("waiting_for_polls", (string?)waiting["status"]);
            Assert.Single(waiting["polls"]!.AsArray());
            var first = waiting["polls"]![0]!["id"]!.GetValue<Guid>();
            Error(await Send(HttpMethod.Get, "/api/v1/results", waitingVisitor), 403, "RESULTS_LOCKED");
            await fixture.Execute("UPDATE checkpoint.polls SET active=true");
            var filled = await Participate(waitingVisitor);
            Assert.Equal(3, (int)filled["assignedPollCount"]!);
            Assert.Equal(first, filled["polls"]![0]!["id"]!.GetValue<Guid>());
        }
        finally { await fixture.Execute("UPDATE checkpoint.polls SET active=true"); }
    }

    [Fact]
    public async Task Votes_are_unique_under_concurrency_and_recoverable_through_participation()
    {
        var visitor = Guid.NewGuid();
        var before = await fixture.Query<long>("SELECT total_xp FROM checkpoint.community_progress");
        var participation = await Participate(visitor);
        var polls = participation["polls"]!.AsArray();
        Error(await Send(HttpMethod.Get, "/api/v1/results", visitor), 403, "RESULTS_LOCKED");
        // An assigned poll remains answerable even if it is no longer active.
        var pollId = polls[0]!["id"]!.GetValue<Guid>();
        await fixture.Execute("UPDATE checkpoint.polls SET active=false WHERE id=@pollId", new { pollId });
        try
        {
            var simultaneous = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Send(HttpMethod.Post, "/api/v1/votes", visitor, Ballot(polls[0]!))));
            Assert.Single(simultaneous, r => r.Status == HttpStatusCode.Created);
            Assert.Equal(7, simultaneous.Count(r => r.Status == HttpStatusCode.Conflict && (string?)r.Body["error"]!["code"] == "ALREADY_VOTED"));
            var recovered = await Participate(visitor);
            Assert.Equal(polls[0]!["options"]![0]!["id"]!.GetValue<Guid>(), recovered["polls"]![0]!["answerOptionId"]!.GetValue<Guid>());
            Assert.False((bool)recovered["polls"]![0]!["canVote"]!);
            Error(await Send(HttpMethod.Post, "/api/v1/votes", visitor, Ballot(polls[0]!, 1)), 409, "ALREADY_VOTED");
            var invalid = JsonContent.Create(new { pollId = polls[1]!["id"]!.GetValue<Guid>(), optionId = polls[0]!["options"]![0]!["id"]!.GetValue<Guid>() });
            Error(await Send(HttpMethod.Post, "/api/v1/votes", visitor, invalid), 400, "VALIDATION_ERROR");
            var remaining = await Task.WhenAll(polls.Skip(1).Select(p => Send(HttpMethod.Post, "/api/v1/votes", visitor, Ballot(p!))));
            Assert.All(remaining, r => Assert.Equal(HttpStatusCode.Created, r.Status));
            Assert.Equal(before + 3, await fixture.Query<long>("SELECT total_xp FROM checkpoint.community_progress"));
            Assert.Equal(3, await fixture.Query<int>("SELECT count(*)::integer FROM checkpoint.votes WHERE visitor_id=@visitor", new { visitor }));
            var resumed = await Participate(visitor);
            Assert.Equal("completed", (string?)resumed["status"]);
            Assert.True((bool)resumed["resultsUnlocked"]!);
            var foreignPoll = await fixture.Query<Guid>("SELECT id FROM checkpoint.polls WHERE id NOT IN (SELECT poll_id FROM checkpoint.poll_assignments WHERE visitor_id=@visitor) LIMIT 1", new { visitor });
            Error(await Send(HttpMethod.Post, "/api/v1/votes", visitor, JsonContent.Create(new { pollId = foreignPoll, optionId = Guid.NewGuid() })), 409, "POLL_NOT_ASSIGNED");
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() => fixture.Execute(
                "INSERT INTO checkpoint.votes(visitor_id,poll_id,option_id) VALUES(@visitor,@foreignPoll,@option)", new { visitor, foreignPoll, option = polls[0]!["options"]![0]!["id"]!.GetValue<Guid>() }));
        }
        finally { await fixture.Execute("UPDATE checkpoint.polls SET active=true WHERE id=@pollId", new { pollId }); }
    }

    [Fact]
    public async Task Results_return_all_visible_voted_polls_ordered_and_refresh_after_new_votes()
    {
        var visitor = Guid.NewGuid();
        Error(await Send(HttpMethod.Get, "/api/v1/results", visitor), 404, "PARTICIPATION_NOT_FOUND");
        await Participate(visitor);
        Error(await Send(HttpMethod.Get, "/api/v1/results", visitor), 403, "RESULTS_LOCKED");
        await Complete(visitor);

        var testPolls = new List<Guid>();
        Guid firstOption = default;
        for (var i = 0; i < 25; i++)
        {
            var pollId = Guid.NewGuid();
            var voter = Guid.NewGuid();
            var option = Guid.NewGuid();
            if (i == 0) firstOption = option;
            testPolls.Add(pollId);
            var title = i switch { 0 => "Zebra", 1 => "abacaxi", 2 => "Árvore", _ => "Enquete de integração" };
            await fixture.Execute("""
                BEGIN;
                INSERT INTO checkpoint.polls(id,title,active) VALUES(@pollId,@title,false);
                INSERT INTO checkpoint.poll_options(id,poll_id,label,position) VALUES(@option,@pollId,'A',0),(@other,@pollId,'B',1);
                INSERT INTO checkpoint.visitors(id) VALUES(@voter);
                INSERT INTO checkpoint.poll_assignments VALUES(@voter,@pollId,0);
                INSERT INTO checkpoint.votes(visitor_id,poll_id,option_id) VALUES(@voter,@pollId,@option);
                UPDATE checkpoint.community_progress SET total_xp=total_xp+1,revision=revision+1;
                COMMIT;
                """, new { pollId, title, voter, option, other = Guid.NewGuid() });
        }
        var zeroVotePoll = Guid.NewGuid();
        var hiddenPoll = testPolls[^1];
        await fixture.Execute("""
            BEGIN;
            INSERT INTO checkpoint.polls(id,title,active) VALUES(@zeroVotePoll,'No votes',false);
            INSERT INTO checkpoint.poll_options(id,poll_id,label,position) VALUES(@first,@zeroVotePoll,'A',0),(@second,@zeroVotePoll,'B',1);
            UPDATE checkpoint.polls SET visible=false WHERE id=@hiddenPoll;
            COMMIT;
            """, new { zeroVotePoll, first = Guid.NewGuid(), second = Guid.NewGuid(), hiddenPoll });
        var snapshotsBefore = await fixture.Query<int>("SELECT count(*)::integer FROM checkpoint.result_snapshots");
        var response = await Send(HttpMethod.Get, "/api/v1/results", visitor);
        Assert.Equal(HttpStatusCode.OK, response.Status);
        Assert.Null(response.Body["nextCursor"]);
        var results = response.Body["results"]!.AsArray();
        var ids = results.Select(r => r!["pollId"]!.GetValue<Guid>()).ToArray();
        Assert.True(results.Count > 20);
        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.DoesNotContain(zeroVotePoll, ids);
        Assert.DoesNotContain(hiddenPoll, ids);
        Assert.All(testPolls.Take(24), id => Assert.Contains(id, ids));
        Assert.All(results, r =>
        {
            Assert.Equal(2, r!["options"]!.AsArray().Count);
            Assert.Equal(r["totalVotes"]!.GetValue<long>(), r["options"]!.AsArray().Sum(o => o!["votes"]!.GetValue<long>()));
        });
        var expected = results.Select(r => (Id: r!["pollId"]!.GetValue<Guid>(), Title: r["title"]!.GetValue<string>()))
            .OrderBy(r => r.Title, StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo("pt-BR"), true)).ThenBy(r => r.Id).Select(r => r.Id);
        Assert.Equal(expected, ids);
        Assert.True(Array.IndexOf(ids, testPolls[1]) < Array.IndexOf(ids, testPolls[2]));
        Assert.True(Array.IndexOf(ids, testPolls[2]) < Array.IndexOf(ids, testPolls[0]));

        // Simulate votes arriving after a visitor's previous response, then request fresh results.
        for (var i = 0; i < 6; i++)
        {
            var voter = Guid.NewGuid();
            await fixture.Execute("""
                BEGIN;
                INSERT INTO checkpoint.visitors(id) VALUES(@voter);
                INSERT INTO checkpoint.poll_assignments VALUES(@voter,@pollId,0);
                INSERT INTO checkpoint.votes(visitor_id,poll_id,option_id) VALUES(@voter,@pollId,@firstOption);
                UPDATE checkpoint.community_progress SET total_xp=total_xp+1,revision=revision+1;
                COMMIT;
                """, new { voter, pollId = testPolls[0], firstOption });
        }
        var refreshed = await Send(HttpMethod.Get, "/api/v1/results", visitor);
        var updated = refreshed.Body["results"]!.AsArray();
        var changed = updated.Single(r => r!["pollId"]!.GetValue<Guid>() == testPolls[0])!;
        Assert.Equal(7, changed["totalVotes"]!.GetValue<long>());
        Assert.Equal(ids, updated.Select(r => r!["pollId"]!.GetValue<Guid>()));
        for (var i = 0; i < 6; i++)
            Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, "/api/v1/results", visitor)).Status);
        Assert.Equal(snapshotsBefore, await fixture.Query<int>("SELECT count(*)::integer FROM checkpoint.result_snapshots"));
        output.WriteLine($"Full results: {updated.Count} polls, {Encoding.UTF8.GetByteCount(refreshed.Body.ToJsonString())} UTF-8 JSON bytes (test data only).");
    }

    private static MultipartFormDataContent Upload(byte[] bytes)
    {
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(bytes), "file", "untrusted-filename.bin");
        return form;
    }

    public static byte[] JpegFixture()
    {
        using var image = Image.Black(640, 480, bands: 3);
        using var color = image.NewFromImage([70, 150, 220]);
        return color.WriteToBuffer(".jpg");
    }

    [Fact]
    public async Task Photos_validate_encode_and_award_once_even_after_invalid_input_and_concurrent_retry()
    {
        var visitor = Guid.NewGuid();
        await Participate(visitor);
        var before = await fixture.Query<long>("SELECT total_xp FROM checkpoint.community_progress");
        Error(await Send(HttpMethod.Post, "/api/v1/photos", visitor, Upload(Encoding.UTF8.GetBytes("invalid-photo-data"))), 415, "UNSUPPORTED_IMAGE_FORMAT");
        Error(await Send(HttpMethod.Post, "/api/v1/photos", visitor, Upload([0xff, 0xd8, 0xff, 0, 0, 0, 0, 0, 0, 0, 0, 0])), 422, "INVALID_IMAGE");
        Error(await Send(HttpMethod.Post, "/api/v1/photos", visitor, Upload(new byte[10 * 1024 * 1024 + 1])), 413, "FILE_TOO_LARGE");
        Assert.Equal(before, await fixture.Query<long>("SELECT total_xp FROM checkpoint.community_progress"));
        var bytes = JpegFixture();
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Send(HttpMethod.Post, "/api/v1/photos", visitor, Upload(bytes))));
        Assert.Single(concurrent, r => r.Status == HttpStatusCode.Created);
        Assert.Single(concurrent, r => r.Status == HttpStatusCode.Conflict && (string?)r.Body["error"]!["code"] == "PHOTO_ALREADY_SUBMITTED");
        Assert.Equal(before + 2, await fixture.Query<long>("SELECT total_xp FROM checkpoint.community_progress"));
        Error(await Send(HttpMethod.Post, "/api/v1/photos", visitor, Upload(bytes)), 409, "PHOTO_ALREADY_SUBMITTED");
        var recent = await Send(HttpMethod.Get, "/api/v1/photos/recent");
        Assert.InRange(recent.Body["photos"]!.AsArray().Count, 1, 3);
        var image = recent.Body["photos"]![0]!["image"]!;
        var thumbnail = Convert.FromBase64String(image["base64"]!.GetValue<string>());
        Assert.Contains("avif", Encoding.ASCII.GetString(thumbnail[..32]));
        using var decoded = Image.NewFromBuffer(thumbnail);
        Assert.Equal(360, decoded.Width);
        Assert.Equal(330, decoded.Height);
        Assert.Equal(0, decoded.GetTypeOf("exif-data"));
        Assert.Equal("submitted", (string?)(await Participate(visitor))["photoStatus"]);
        output.WriteLine($"Thumbnail bytes={thumbnail.Length}, runtime={System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}, libvips={NetVips.NetVips.Version(0)}.{NetVips.NetVips.Version(1)}.{NetVips.NetVips.Version(2)}");
    }

    [Fact]
    public async Task Actual_codecs_orientation_and_pixel_limits_are_verified_with_native_images()
    {
        var processor = fixture.Services.GetRequiredService<ImageProcessor>();
        using var baseImage = Image.Black(660, 720, bands: 3);
        // Horizontal red/blue bands become vertical after orientation 6 (90 degrees).
        using var top = baseImage.ExtractArea(0, 0, 660, 360);
        using var red = top.NewFromImage([255, 0, 0]);
        using var blue = top.NewFromImage([0, 0, 255]);
        using var stacked = red.Join(blue, Enums.Direction.Vertical);
        using var oriented = stacked.Mutate(image => image.Set(GValue.GIntType, "orientation", 6));
        var jpeg = oriented.WriteToBuffer(".jpg");
        using (var encoded = Image.NewFromBuffer(jpeg)) Assert.Equal(6, (int)encoded.Get("orientation"));
        var result = await processor.Process(jpeg, CancellationToken.None);
        using var decoded = Image.NewFromBuffer(result.Thumbnail);
        var left = decoded.Getpoint(30, 160);
        var right = decoded.Getpoint(330, 160);
        Assert.True(left[2] > left[0] + 100);
        Assert.True(right[0] > right[2] + 100);
        Assert.Equal(0, decoded.GetTypeOf("orientation"));
        foreach (var format in new[] { ".png", ".webp", ".avif" })
        {
            var processed = await processor.Process(stacked.WriteToBuffer(format), CancellationToken.None);
            using var check = Image.NewFromBuffer(processed.Thumbnail);
            Assert.Equal((360, 330), (check.Width, check.Height));
        }
        using var limited = new ImageProcessor(new ImageSettings { MaxPixels = 100 }, NullLogger<ImageProcessor>.Instance);
        limited.Probe();
        var error = await Assert.ThrowsAsync<ApiException>(() => limited.Process(jpeg, CancellationToken.None));
        Assert.Equal("IMAGE_DIMENSIONS_EXCEEDED", error.Code);
        // A corrupt HEIC is "unsupported" only when this build cannot read HEIC at all; otherwise it is simply invalid.
        var heic = Encoding.ASCII.GetBytes("\0\0\0\u0018ftypheic\0\0\0\0mif1heic");
        Assert.Equal(processor.SupportsHeic ? "INVALID_IMAGE" : "UNSUPPORTED_IMAGE_FORMAT",
            (await Assert.ThrowsAsync<ApiException>(() => processor.Process(heic, CancellationToken.None))).Code);
    }

    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public async Task Many_input_formats_become_360x330_avif_and_non_photos_are_refused()
    {
        var processor = fixture.Services.GetRequiredService<ImageProcessor>();
        output.WriteLine($"SupportsHeic={processor.SupportsHeic}, libvips={NetVips.NetVips.Version(0)}.{NetVips.NetVips.Version(1)}.{NetVips.NetVips.Version(2)}");
        using var source = Image.Black(640, 480, bands: 3).NewFromImage([200, 90, 40]);
        using var transparent = Image.Black(400, 400, bands: 4).NewFromImage([0, 0, 0, 0]);
        var universal = new Dictionary<string, byte[]>
        {
            ["jpeg"] = source.WriteToBuffer(".jpg"),
            ["png"] = source.WriteToBuffer(".png"),
            ["webp"] = source.WriteToBuffer(".webp"),
            ["avif"] = source.WriteToBuffer(".avif", new VOption { { "compression", Enums.ForeignHeifCompression.Av1 } }),
        };
        foreach (var (name, bytes) in universal)
        {
            var result = await processor.Process(bytes, CancellationToken.None);
            using var decoded = Image.NewFromBuffer(result.Thumbnail);
            Assert.True((360, 330) == (decoded.Width, decoded.Height), name);
            Assert.Contains("avif", Encoding.ASCII.GetString(result.Thumbnail[..32]));
        }

        // JPEG 2000 needs a libvips built with OpenJPEG: present in the Linux image, absent from the NuGet bundle used on the Mac.
        var jp2 = Fixture("photo.jp2");
        if (Image.FindLoadBuffer(jp2) == "VipsForeignLoadJp2kBuffer")
            Assert.Equal("image/jp2", (await processor.Process(jp2, CancellationToken.None)).MediaType);
        else
            Assert.Equal("UNSUPPORTED_IMAGE_FORMAT", (await Assert.ThrowsAsync<ApiException>(() => processor.Process(jp2, CancellationToken.None))).Code);
        output.WriteLine($"Jp2={Image.FindLoadBuffer(jp2) ?? "none"}");

        // Fully transparent PNG must be flattened onto white rather than turn black.
        var flattened = await processor.Process(transparent.WriteToBuffer(".png"), CancellationToken.None);
        using (var flat = Image.NewFromBuffer(flattened.Thumbnail)) Assert.True(flat.Getpoint(180, 165)[0] > 230);

        // HEIC (including a real 3840x2160 file with the nclx profile warning): converted when HEVC decoding exists, otherwise a clear 415.
        foreach (var name in new[] { "photo.heic", "large-4k.heic" })
        {
            if (processor.SupportsHeic)
            {
                var result = await processor.Process(Fixture(name), CancellationToken.None);
                Assert.Equal("image/heic", result.MediaType);
                using var decoded = Image.NewFromBuffer(result.Thumbnail);
                Assert.Equal((360, 330), (decoded.Width, decoded.Height));
            }
            else Assert.Equal("UNSUPPORTED_IMAGE_FORMAT", (await Assert.ThrowsAsync<ApiException>(() => processor.Process(Fixture(name), CancellationToken.None))).Code);
        }

        // Popular but deliberately unsupported formats are refused, even though libvips can read them.
        foreach (var name in new[] { "photo.tiff", "photo.gif", "photo.bmp" })
            Assert.Equal("UNSUPPORTED_IMAGE_FORMAT", (await Assert.ThrowsAsync<ApiException>(() => processor.Process(Fixture(name), CancellationToken.None))).Code);

        // Not photos: SVG and PDF are readable by libvips but refused; plain text too.
        foreach (var bytes in new[] { Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg' width='10' height='10'><rect width='10' height='10'/></svg>"),
                                      Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj<<>>endobj\ntrailer<<>>\n%%EOF"), Encoding.UTF8.GetBytes("just text, not an image") })
            Assert.Equal("UNSUPPORTED_IMAGE_FORMAT", (await Assert.ThrowsAsync<ApiException>(() => processor.Process(bytes, CancellationToken.None))).Code);
    }

    [Fact]
    public async Task Heic_upload_over_http_stores_avif_thumbnail_when_decoder_exists()
    {
        var processor = fixture.Services.GetRequiredService<ImageProcessor>();
        var visitor = Guid.NewGuid();
        await Participate(visitor);
        var response = await Send(HttpMethod.Post, "/api/v1/photos", visitor, Upload(Fixture("large-4k.heic")));
        if (!processor.SupportsHeic) { Error(response, 415, "UNSUPPORTED_IMAGE_FORMAT"); return; }
        Assert.Equal(HttpStatusCode.Created, response.Status);
        Assert.Equal("image/heic", await fixture.Query<string>("SELECT media_type FROM checkpoint.photos WHERE visitor_id=@visitor", new { visitor }));
        Assert.Equal("submitted", (string?)(await Participate(visitor))["photoStatus"]);
    }

    [Fact]
    public async Task Public_progress_has_real_private_artwork_no_future_levels_and_final_state()
    {
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, "/health/live")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, "/health/ready")).Status);
        var saved = await fixture.Query<long>("SELECT total_xp FROM checkpoint.community_progress");
        try
        {
            await fixture.Execute("UPDATE checkpoint.community_progress SET total_xp=0");
            var progress = await Send(HttpMethod.Get, "/api/v1/progress");
            Assert.Equal("strangers", (string?)progress.Body["currentLevel"]!["id"]);
            Assert.Empty(progress.Body["previousLevels"]!.AsArray());
            Assert.DoesNotContain("friends", progress.Body.ToJsonString());
            foreach (var character in progress.Body["currentLevel"]!["characters"]!.AsArray())
            {
                using var image = Image.NewFromBuffer(Convert.FromBase64String((string)character!["base64"]!));
                Assert.Equal((int)character["width"]!, image.Width);
                Assert.Equal((int)character["height"]!, image.Height);
                Assert.True(image.HasAlpha());
            }
            using var response = await fixture.Client.GetAsync("/PrivateAssets/Nivel5atilajuhCasamento.4f6042831a.avif");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            await fixture.Execute("UPDATE checkpoint.community_progress SET total_xp=9000");
            var final = (await Send(HttpMethod.Get, "/api/v1/progress")).Body["currentLevel"]!;
            Assert.Equal("married", (string?)final["id"]);
            Assert.True((bool)final["isFinal"]!);
            Assert.Null(final["nextLevelAtXp"]);
            Error(await Send(HttpMethod.Put, "/api/v1/participation"), 400, "INVALID_VISITOR_ID");
        }
        finally { await fixture.Execute("UPDATE checkpoint.community_progress SET total_xp=@saved", new { saved }); }
    }
}
