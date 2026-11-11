using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Checkpoint.Api;
using Xunit;

namespace Checkpoint.Tests;

[Collection("PostgreSQL")]
public sealed class AdminAuthTests(ApiFixture fixture)
{
    [Fact]
    public async Task Registration_endpoints_are_removed_and_session_requires_authentication()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.Client.PostAsync("/api/admin/passkeys/register/options", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.Client.PostAsync("/api/admin/passkeys/register/verify", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.Client.GetAsync("/api/admin/auth/bootstrap-status")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Client.GetAsync("/api/admin/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Challenge_can_be_consumed_only_once_under_concurrency()
    {
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var challenges = new DistributedCacheChallenge(cache);
        var initial = new DefaultHttpContext();
        await challenges.Put(initial, new { challenge = "test" }, "", CancellationToken.None);
        var cookie = initial.Response.Headers.SetCookie.ToString().Split(';')[0];
        var calls = Enumerable.Range(0, 16).Select(async _ =>
        {
            var context = new DefaultHttpContext();
            context.Request.Headers.Cookie = cookie;
            return await challenges.Take(context, CancellationToken.None);
        });
        Assert.Single(await Task.WhenAll(calls), result => result is not null);
    }
}
