using System.Text.Json;
using System.Text.Json.Serialization;

namespace Post.BuildingBlocks.Serialization;

/// <summary>Creates isolated System.Text.Json options suitable for versioned Post wire contracts.</summary>
public static class PostJsonDefaults
{
    /// <summary>Creates a fresh configured options instance.</summary>
    /// <param name="strict">When true, rejects unknown properties, comments, and trailing commas.</param>
    public static JsonSerializerOptions Create(bool strict = true)
    {
        var options = new JsonSerializerOptions();
        Apply(options, strict);
        return options;
    }

    /// <summary>Applies project defaults to an existing, mutable options instance.</summary>
    public static void Apply(JsonSerializerOptions options, bool strict = true)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.IsReadOnly)
        {
            throw new InvalidOperationException("JSON options are read-only after first use.");
        }

        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.PropertyNameCaseInsensitive = false;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        options.NumberHandling = JsonNumberHandling.Strict;
        options.ReadCommentHandling = strict ? JsonCommentHandling.Disallow : JsonCommentHandling.Skip;
        options.AllowTrailingCommas = !strict;
        options.UnmappedMemberHandling = strict ? JsonUnmappedMemberHandling.Disallow : JsonUnmappedMemberHandling.Skip;
        options.WriteIndented = false;
        options.MaxDepth = 64;
    }
}
