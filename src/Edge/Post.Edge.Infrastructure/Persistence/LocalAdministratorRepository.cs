using Microsoft.EntityFrameworkCore;
using Post.Edge.Application.Ports;
using Post.Edge.Domain.Devices;
using Post.Edge.Domain.Common;

namespace Post.Edge.Infrastructure.Persistence;

internal sealed class LocalAdministratorRepository(EdgeDbContext db) : ILocalAdministratorRepository
{
    public Task<bool> ExistsAsync(CancellationToken cancellationToken) =>
        db.LocalAdministrators.AnyAsync(candidate => candidate.Id == 1, cancellationToken);

    public async Task<LocalAdministrator?> GetAsync(CancellationToken cancellationToken)
    {
        var row = await db.LocalAdministrators.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == 1, cancellationToken);
        if (row is null) return null;
        try
        {
            return ToDomain(row);
        }
        catch (DomainException)
        {
            return null;
        }
    }

    public async Task StoreAsync(LocalAdministrator administrator, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(administrator);
        var row = db.LocalAdministrators.Local.SingleOrDefault(candidate => candidate.Id == 1)
                  ?? await db.LocalAdministrators.SingleOrDefaultAsync(
                      candidate => candidate.Id == 1,
                      cancellationToken);
        if (row is null)
        {
            row = new LocalAdministratorRow { Id = 1 };
            db.LocalAdministrators.Add(row);
        }
        row.UserName = administrator.UserName;
        row.NormalizedUserName = administrator.NormalizedUserName;
        row.SecretAlgorithm = administrator.PasswordVerifier.Algorithm;
        row.SecretIterations = administrator.PasswordVerifier.Iterations;
        row.SecretSalt = administrator.PasswordVerifier.SaltBase64;
        row.SecretHash = administrator.PasswordVerifier.HashBase64;
        row.CredentialVersion = administrator.CredentialVersion;
        row.UpdatedAt = administrator.UpdatedAt;
    }

    public async Task<LocalAdministratorSession?> FindSessionAsync(
        string tokenHash,
        CancellationToken cancellationToken)
    {
        var row = await db.LocalAdministratorSessions.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.TokenHash == tokenHash, cancellationToken);
        return row is null
            ? null
            : new LocalAdministratorSession(
                row.TokenHash,
                row.UserName,
                row.CredentialVersion,
                row.CreatedAt,
                row.ExpiresAt);
    }

    public void AddSession(LocalAdministratorSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        db.LocalAdministratorSessions.Add(new LocalAdministratorSessionRow
        {
            TokenHash = session.TokenHash,
            UserName = session.UserName,
            CredentialVersion = session.CredentialVersion,
            CreatedAt = session.CreatedAt,
            ExpiresAt = session.ExpiresAt
        });
    }

    public async Task RemoveSessionAsync(string tokenHash, CancellationToken cancellationToken)
    {
        var row = await db.LocalAdministratorSessions
            .SingleOrDefaultAsync(candidate => candidate.TokenHash == tokenHash, cancellationToken);
        if (row is not null) db.LocalAdministratorSessions.Remove(row);
    }

    public async Task RemoveAllSessionsAsync(CancellationToken cancellationToken)
    {
        var rows = await db.LocalAdministratorSessions.ToListAsync(cancellationToken);
        db.LocalAdministratorSessions.RemoveRange(rows);
    }

    public async Task RemoveExpiredSessionsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var rows = await db.LocalAdministratorSessions
            .Where(session => session.ExpiresAt <= now)
            .ToListAsync(cancellationToken);
        db.LocalAdministratorSessions.RemoveRange(rows);
    }

    private static LocalAdministrator ToDomain(LocalAdministratorRow row) => new(
        row.UserName,
        new DeviceSecretVerifier(
            row.SecretAlgorithm,
            row.SecretIterations,
            row.SecretSalt,
            row.SecretHash),
        row.CredentialVersion,
        row.UpdatedAt);
}
