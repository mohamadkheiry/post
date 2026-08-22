using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Post.Edge.Application.Ports;

namespace Post.Edge.Infrastructure.Persistence;

public sealed class EdgeDbContext(DbContextOptions<EdgeDbContext> options) : DbContext(options), IEdgeUnitOfWork
{
    private readonly List<Action> _synchronizers = [];
    private readonly List<Action> _postSaveActions = [];

    internal DbSet<ParcelRow> Parcels => Set<ParcelRow>();
    internal DbSet<BagRow> Bags => Set<BagRow>();
    internal DbSet<PendingBagClosureRow> PendingBagClosures => Set<PendingBagClosureRow>();
    internal DbSet<DispatchRow> Dispatches => Set<DispatchRow>();
    internal DbSet<PendingInboundRow> PendingInbounds => Set<PendingInboundRow>();
    internal DbSet<InboundEvidenceRow> InboundEvidence => Set<InboundEvidenceRow>();
    internal DbSet<InboundImageRow> InboundImages => Set<InboundImageRow>();
    internal DbSet<ConfigurationRow> Configurations => Set<ConfigurationRow>();
    internal DbSet<DeviceSessionRow> DeviceSessions => Set<DeviceSessionRow>();
    internal DbSet<DeviceNetworkBindingRow> DeviceNetworkBindings => Set<DeviceNetworkBindingRow>();
    internal DbSet<LocalAdministratorRow> LocalAdministrators => Set<LocalAdministratorRow>();
    internal DbSet<LocalAdministratorSessionRow> LocalAdministratorSessions => Set<LocalAdministratorSessionRow>();
    internal DbSet<OperationJournalRow> OperationJournal => Set<OperationJournalRow>();
    internal DbSet<ExternalOperationSagaRow> ExternalOperationSagas => Set<ExternalOperationSagaRow>();
    internal DbSet<OutboxRow> Outbox => Set<OutboxRow>();
    internal DbSet<DeadLetterRow> DeadLetters => Set<DeadLetterRow>();

    internal void TrackSynchronizer(Action synchronizer) => _synchronizers.Add(synchronizer);
    internal void TrackPostSave(Action action) => _postSaveActions.Add(action);

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        SynchronizeAggregates();
        var result = base.SaveChanges(acceptAllChangesOnSuccess);
        RunPostSaveActions();
        return result;
    }

    public override async Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        SynchronizeAggregates();
        var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        RunPostSaveActions();
        return result;
    }

    Task<int> IEdgeUnitOfWork.SaveChangesAsync(CancellationToken cancellationToken) =>
        SaveChangesAsync(cancellationToken);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ParcelRow>(x =>
        {
            x.ToTable("edge_parcels");
            x.HasKey(y => y.ReadId);
            x.HasIndex(y => y.CanonicalBarcode);
            x.HasIndex(y => new { y.DeviceId, y.DestinationCode, y.State, y.ReadTimestamp });
        });
        modelBuilder.Entity<BagRow>(x =>
        {
            x.ToTable("edge_bags");
            x.HasKey(y => y.BagBarcode);
            x.HasIndex(y => y.LocalOperationId).IsUnique();
            x.HasIndex(y => new { y.DeviceId, y.DestinationCode, y.DispatchId });
        });
        modelBuilder.Entity<PendingBagClosureRow>(x =>
        {
            x.ToTable("pending_bag_closures");
            x.HasKey(y => y.LocalOperationId);
            x.HasIndex(y => new { y.State, y.NextAttemptAt });
        });
        modelBuilder.Entity<DispatchRow>(x =>
        {
            x.ToTable("edge_dispatches");
            x.HasKey(y => y.LocalOperationId);
            x.HasIndex(y => y.DispatchId).IsUnique();
        });
        modelBuilder.Entity<PendingInboundRow>(x =>
        {
            x.ToTable("pending_inbounds");
            x.HasKey(y => y.OperationId);
            x.HasIndex(y => y.NextAttemptAt);
        });
        modelBuilder.Entity<InboundEvidenceRow>(x =>
        {
            x.ToTable("inbound_evidence");
            x.HasKey(y => y.ReadId);
        });
        modelBuilder.Entity<InboundImageRow>(x =>
        {
            x.ToTable("inbound_images");
            x.HasKey(y => new { y.ReadId, y.ImageId });
        });
        modelBuilder.Entity<ConfigurationRow>(x =>
        {
            x.ToTable("edge_configuration");
            x.HasKey(y => y.Id);
        });
        modelBuilder.Entity<DeviceSessionRow>(x =>
        {
            x.ToTable("device_sessions");
            x.HasKey(y => y.ConnectionId);
            x.HasIndex(y => y.SessionId).IsUnique();
        });
        modelBuilder.Entity<DeviceNetworkBindingRow>(x =>
        {
            x.ToTable("device_network_bindings");
            x.HasKey(y => new { y.DeviceId, y.IpAddress });
        });
        modelBuilder.Entity<LocalAdministratorRow>(x =>
        {
            x.ToTable("local_administrators");
            x.HasKey(y => y.Id);
            x.HasIndex(y => y.NormalizedUserName).IsUnique();
        });
        modelBuilder.Entity<LocalAdministratorSessionRow>(x =>
        {
            x.ToTable("local_administrator_sessions");
            x.HasKey(y => y.TokenHash);
            x.HasIndex(y => y.ExpiresAt);
        });
        modelBuilder.Entity<OperationJournalRow>(x =>
        {
            x.ToTable("operation_journal");
            x.HasKey(y => new { y.OperationId, y.OperationType });
            x.Property(y => y.RequestHash).HasMaxLength(64);
        });
        modelBuilder.Entity<ExternalOperationSagaRow>(x =>
        {
            x.ToTable("external_operation_sagas");
            x.HasKey(y => new { y.OperationId, y.OperationType });
            x.Property(y => y.RequestHash).HasMaxLength(64);
            x.HasIndex(y => new { y.Finalized, y.UpdatedAt });
        });
        modelBuilder.Entity<OutboxRow>(x =>
        {
            x.ToTable("outbox");
            x.HasKey(y => y.MessageId);
            x.HasIndex(y => y.IdempotencyKey).IsUnique();
            x.HasIndex(y => new { y.ProcessedAt, y.NextAttemptAt, y.LockedUntil });
        });
        modelBuilder.Entity<DeadLetterRow>(x =>
        {
            x.ToTable("dead_letters");
            x.HasKey(y => y.MessageId);
            x.HasIndex(y => y.IdempotencyKey);
        });

        var dateTimeOffsetConverter = new ValueConverter<DateTimeOffset, long>(
            value => value.UtcTicks,
            value => new DateTimeOffset(value, TimeSpan.Zero));
        var nullableDateTimeOffsetConverter = new ValueConverter<DateTimeOffset?, long?>(
            value => value.HasValue ? value.Value.UtcTicks : null,
            value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTimeOffset)) property.SetValueConverter(dateTimeOffsetConverter);
                if (property.ClrType == typeof(DateTimeOffset?)) property.SetValueConverter(nullableDateTimeOffsetConverter);
            }
        }
    }

    private void SynchronizeAggregates()
    {
        foreach (var synchronizer in _synchronizers) synchronizer();
    }

    private void RunPostSaveActions()
    {
        if (_postSaveActions.Count == 0) return;
        var actions = _postSaveActions.ToArray();
        _postSaveActions.Clear();
        foreach (var action in actions) action();
    }
}
