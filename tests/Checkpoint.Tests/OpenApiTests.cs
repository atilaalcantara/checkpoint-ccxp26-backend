using System.Net;
using System.Text.Json.Nodes;
using Xunit;

namespace Checkpoint.Tests;

[Collection("PostgreSQL")]
public sealed class OpenApiTests(ApiFixture fixture)
{
    [Fact]
    public async Task Development_document_exposes_interactive_contract_with_headers_upload_schemas_and_errors()
    {
        using var ui = await fixture.Client.GetAsync("/swagger/index.html");
        Assert.Equal(HttpStatusCode.OK, ui.StatusCode);
        Assert.Contains("swagger-ui", await ui.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        var document = JsonNode.Parse(await fixture.Client.GetStringAsync("/openapi/v1.json"))!;
        var paths = document["paths"]!;
        var vote = paths["/api/v1/votes"]!["post"]!;
        Assert.Contains(vote["parameters"]!.AsArray(), p => (string?)p!["name"] == "X-Visitor-Id" && (bool)p["required"]!);
        Assert.DoesNotContain(vote["parameters"]!.AsArray(), p => (string?)p!["name"] == "Idempotency-Key");
        foreach (var status in new[] { "201", "400", "404", "409", "503" })
            Assert.NotNull(vote["responses"]![status]);
        Assert.Null(vote["responses"]!["200"]);
        Assert.NotNull(paths["/api/v1/photos"]!["post"]!["requestBody"]!["content"]!["multipart/form-data"]);
        Assert.Equal("binary", (string?)document["components"]!["schemas"]!["IFormFile"]!["format"]);
        var results = paths["/api/v1/results"]!["get"]!;
        Assert.Null(results["responses"]!["410"]);
        Assert.DoesNotContain(results["parameters"]!.AsArray(), p => (string?)p!["name"] == "cursor");
        Assert.Null(document["components"]!["schemas"]!["ResultsResponse"]!["properties"]!["nextCursor"]);
        Assert.Null(document["components"]!["schemas"]!["PollOption"]!["properties"]!["iconKey"]);
        Assert.Null(document["components"]!["schemas"]!["PollOption"]!["properties"]!["tone"]);
        Assert.NotNull(document["components"]!["schemas"]!["ProgressResponse"]!["properties"]!["currentLevel"]);
        Assert.Null(paths["/api/v1/operations/{idempotencyKey}"]);
    }
}
