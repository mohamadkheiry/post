namespace Post.Contracts.V1.Common;

/// <summary>A transport-safe error returned to a caller.</summary>
/// <param name="Code">Stable machine-readable code.</param>
/// <param name="Message">Safe human-readable message.</param>
/// <param name="Retryable">Whether retrying the same operation can succeed.</param>
/// <param name="Details">Optional field-level errors. Values must not contain secrets.</param>
public sealed record ContractErrorV1(
    string Code,
    string Message,
    bool Retryable = false,
    IReadOnlyDictionary<string, string[]>? Details = null);
