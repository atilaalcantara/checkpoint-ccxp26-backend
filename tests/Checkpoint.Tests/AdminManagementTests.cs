using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Checkpoint.Api;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Checkpoint.Tests;

[Collection("PostgreSQL")]
public sealed class AdminManagementTests(ApiFixture fixture)
{
    [Fact]
    public async Task Management_requires_session_and_csrf_and_enforces_assigned_poll_and_site_status()
    {
        var unauthenticated = await fixture.Client.GetAsync("/api/admin/polls");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        var options = fixture.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim(ClaimTypes.Name, "Teste"), new Claim(ClaimTypes.Role, "admin")], CookieAuthenticationDefaults.AuthenticationScheme));
        var cookie = options.TicketDataFormat.Protect(new AuthenticationTicket(principal, CookieAuthenticationDefaults.AuthenticationScheme));
        using var client = fixture.Server.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"checkpoint.admin={cookie}");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/admin/polls", new { question = "Qual jogo?", a = "A", b = "B", active = true })).StatusCode);
        var csrf = await client.GetAsync("/api/admin/auth/csrf");
        csrf.EnsureSuccessStatusCode();
        var token = (await csrf.Content.ReadFromJsonAsync<TokenResponse>())!.Token;
        var antiforgeryCookie = csrf.Headers.GetValues("Set-Cookie").First(v => v.StartsWith("checkpoint.antiforgery=")).Split(';')[0];
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", $"checkpoint.admin={cookie}; {antiforgeryCookie}");
        client.DefaultRequestHeaders.Add("X-CSRF-Token", token);
        var create = await client.PostAsJsonAsync("/api/admin/polls", new { question = "Qual jogo?", a = "A", b = "B", active = true });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var id = (await create.Content.ReadFromJsonAsync<IdResponse>())!.Id;
        try
        {
            var visitor = Guid.NewGuid();
            await fixture.Execute("INSERT INTO checkpoint.visitors(id) VALUES(@visitor); INSERT INTO checkpoint.poll_assignments(visitor_id,poll_id,position) VALUES(@visitor,@id,0)", new { visitor, id });
            var edit = await client.PutAsJsonAsync($"/api/admin/polls/{id}", new { question = "Outra pergunta?", a = "A", b = "B", active = true });
            Assert.Equal(HttpStatusCode.Conflict, edit.StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/api/admin/polls/{id}", new { question = "Qual jogo?", a = "A", b = "B", active = false })).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/admin/polls/{id}")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/admin/site-status", new { status = "paused" })).StatusCode);
            using var publicClient = fixture.Server.CreateClient();
            publicClient.DefaultRequestHeaders.Add("X-Visitor-Id", Guid.NewGuid().ToString());
            Assert.Equal(HttpStatusCode.Conflict, (await publicClient.PutAsync("/api/v1/participation", null)).StatusCode);
            await fixture.Execute("UPDATE checkpoint.site_status SET status='live' WHERE id=1");
            var photoId = Guid.NewGuid();
            await fixture.Execute("INSERT INTO checkpoint.photo_submissions(visitor_id) VALUES(@visitor); INSERT INTO checkpoint.photos(id,visitor_id,original,media_type,thumbnail,width,height) VALUES(@photoId,@visitor,@bytes,'image/jpeg',@bytes,360,330)", new { visitor, photoId, bytes = new byte[] { 1, 2, 3 } });
            Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/admin/photos/{photoId}")).StatusCode);
            Assert.True(await fixture.Query<bool>("SELECT EXISTS(SELECT 1 FROM checkpoint.photo_submissions WHERE visitor_id=@visitor)", new { visitor }));
            Assert.False(await fixture.Query<bool>("SELECT EXISTS(SELECT 1 FROM checkpoint.photos WHERE id=@photoId)", new { photoId }));
        }
        finally { await fixture.Execute("UPDATE checkpoint.site_status SET status='live' WHERE id=1"); }
    }
    private sealed record TokenResponse(string Token);
    private sealed record IdResponse(Guid Id);
}
