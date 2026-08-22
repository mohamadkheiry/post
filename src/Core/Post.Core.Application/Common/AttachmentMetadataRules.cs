using System.Net.Http.Headers;
using Post.Core.Domain.Parcels;

namespace Post.Core.Application.Common;

public static class AttachmentMetadataRules
{
    public static string NormalizeContentType(string contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            throw new CoreValidationException("Content type is required.");
        }

        var candidate = contentType.Trim();
        if (candidate.Length > SupplementaryAttachment.MaximumContentTypeLength ||
            candidate.Any(char.IsControl) ||
            !MediaTypeHeaderValue.TryParse(candidate, out var parsed) ||
            string.IsNullOrWhiteSpace(parsed.MediaType) ||
            parsed.Parameters.Count != 0)
        {
            throw new CoreValidationException(
                "Content type must be a parameter-free media type of at most 128 characters.");
        }

        return parsed.MediaType.ToLowerInvariant();
    }

    public static string NormalizeAttachmentType(string attachmentType)
    {
        if (string.IsNullOrWhiteSpace(attachmentType))
        {
            throw new CoreValidationException("Attachment type is required.");
        }

        var candidate = attachmentType.Trim();
        if (candidate.Length > SupplementaryAttachment.MaximumAttachmentTypeLength ||
            candidate.Any(character =>
                character > 0x7f ||
                !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')))
        {
            throw new CoreValidationException(
                "Attachment type may contain only ASCII letters, digits, dot, underscore, and hyphen, up to 64 characters.");
        }

        return candidate;
    }
}
