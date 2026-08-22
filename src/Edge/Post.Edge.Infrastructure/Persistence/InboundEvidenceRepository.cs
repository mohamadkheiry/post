using Microsoft.EntityFrameworkCore;
using Post.Edge.Application.Ports;

namespace Post.Edge.Infrastructure.Persistence;

internal sealed class InboundEvidenceRepository(EdgeDbContext db) : IInboundEvidenceRepository
{
    public void Add(InboundEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        db.InboundEvidence.Add(new InboundEvidenceRow
        {
            ReadId = evidence.ReadId,
            SupplementaryDataJson = evidence.SupplementaryDataJson,
            CreatedAt = evidence.CreatedAt
        });
        db.InboundImages.AddRange(evidence.Images.Select(x => new InboundImageRow
        {
            ReadId = evidence.ReadId,
            ImageId = x.ImageId,
            Content = x.Content.ToArray(),
            MimeType = x.MimeType,
            Description = x.Description
        }));
    }

    public async Task<InboundImageContent?> FindImageAsync(
        Guid readId,
        string imageId,
        CancellationToken cancellationToken)
    {
        var row = await db.InboundImages.AsNoTracking().SingleOrDefaultAsync(
            x => x.ReadId == readId && x.ImageId == imageId,
            cancellationToken);
        return row is null ? null : new InboundImageContent(
            row.ImageId,
            row.Content.ToArray(),
            row.MimeType,
            row.Description);
    }

    public async Task<string?> FindSupplementaryDataAsync(
        Guid readId,
        CancellationToken cancellationToken) =>
        await db.InboundEvidence.AsNoTracking()
            .Where(row => row.ReadId == readId)
            .Select(row => row.SupplementaryDataJson)
            .SingleOrDefaultAsync(cancellationToken);
}
