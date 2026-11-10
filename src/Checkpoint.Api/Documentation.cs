using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Checkpoint.Api;

public static class Documentation
{
    public static Task AddHeaders(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        var path = context.Description.RelativePath?.Split('?')[0] ?? "";
        var personalized = path is "api/v1/participation" or "api/v1/votes" or "api/v1/results" or "api/v1/photos";
        if (personalized)
            AddHeader(operation, "X-Visitor-Id", "Browser-generated canonical UUID v4. This is an anonymous identifier, not authentication.");
        if (operation.Responses is not null)
        {
            foreach (var (status, response) in operation.Responses)
            {
                response.Description = (path, status) switch
                {
                    ("api/v1/votes" or "api/v1/photos", "201") => "Created and XP awarded once.",
                    (_, "400") => "Invalid UUID or input. Error codes include INVALID_VISITOR_ID and VALIDATION_ERROR.",
                    (_, "403") => "RESULTS_LOCKED: three confirmed votes are required.",
                    (_, "404") => "Resource not found in this visitor's context (PARTICIPATION_NOT_FOUND or POLL_NOT_FOUND).",
                    (_, "409") => "Conflict: ALREADY_VOTED, POLL_NOT_ASSIGNED or PHOTO_ALREADY_SUBMITTED.",
                    (_, "413") => "FILE_TOO_LARGE: the upload exceeds the configured limit.",
                    (_, "415") => "UNSUPPORTED_IMAGE_FORMAT: the image format or decoder is not supported.",
                    (_, "422") => "INVALID_IMAGE or IMAGE_DIMENSIONS_EXCEEDED.",
                    (_, "503") => "SERVICE_UNAVAILABLE or PROCESSING_BUSY. Retry-After is provided.",
                    _ => response.Description
                };
            }
        }
        return Task.CompletedTask;
    }

    private static void AddHeader(OpenApiOperation operation, string name, string description)
    {
        operation.Parameters ??= [];
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = name,
            In = ParameterLocation.Header,
            Required = true,
            Description = description,
            Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" }
        });
    }
}
