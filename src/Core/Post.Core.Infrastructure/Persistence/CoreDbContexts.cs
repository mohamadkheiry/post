using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Post.Core.Domain.Devices;
using Post.Core.Domain.Messaging;
using Post.Core.Domain.Operations;
using Post.Core.Domain.Parcels;
using Post.Core.Domain.Security;
using Post.Core.Domain.ValueObjects;

namespace Post.Core.Infrastructure.Persistence;

public sealed class ParcelLifecycleDbContext(DbContextOptions<ParcelLifecycleDbContext> options) : DbContext(options)
{
    public DbSet<ParcelDossier> ParcelDossiers => Set<ParcelDossier>();

    public DbSet<ReadingRecord> ReadingRecords => Set<ReadingRecord>();

    public DbSet<DiscrepancyRecord> DiscrepancyRecords => Set<DiscrepancyRecord>();

    public DbSet<SupplementaryAttachment> SupplementaryAttachments => Set<SupplementaryAttachment>();

    public DbSet<PendingAttachment> PendingAttachments => Set<PendingAttachment>();

    public DbSet<Bag> Bags => Set<Bag>();

    public DbSet<Dispatch> Dispatches => Set<Dispatch>();

    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureParcel(modelBuilder.Entity<ParcelDossier>());
        ConfigureReading(modelBuilder.Entity<ReadingRecord>());
        ConfigureDiscrepancy(modelBuilder.Entity<DiscrepancyRecord>());
        ConfigureAttachment(modelBuilder.Entity<SupplementaryAttachment>());
        ConfigurePendingAttachment(modelBuilder.Entity<PendingAttachment>());
        ConfigureBag(modelBuilder.Entity<Bag>());
        ConfigureDispatch(modelBuilder.Entity<Dispatch>());
        ReliabilityConfiguration.ConfigureIdempotency(modelBuilder.Entity<IdempotencyRecord>());
        ReliabilityConfiguration.ConfigureInbox(modelBuilder.Entity<InboxMessage>());
        ReliabilityConfiguration.ConfigureOutbox(modelBuilder.Entity<OutboxMessage>());
    }

    private static void ConfigureParcel(EntityTypeBuilder<ParcelDossier> entity)
    {
        entity.ToTable("ParcelDossiers");
        entity.HasKey(x => x.Barcode);
        entity.Property(x => x.Barcode).HasConversion(x => x.Value, x => new ParcelBarcode(x)).HasMaxLength(128);
        entity.Property(x => x.ParcelType).HasMaxLength(64).IsRequired();
        entity.Property(x => x.ServiceType).HasMaxLength(64).IsRequired();
        entity.Property(x => x.OriginCode).HasConversion(x => x.Value, x => new ExchangeCenterCode(x)).HasMaxLength(32);
        entity.Property(x => x.DestinationCode).HasConversion(x => x.Value, x => new ExchangeCenterCode(x)).HasMaxLength(32);
        entity.Property(x => x.OperationalDestinationCode)
            .HasConversion(x => x!.Value.Value, x => new ExchangeCenterCode(x))
            .HasMaxLength(32);
        entity.Property(x => x.PhysicalBagReference)
            .HasConversion(x => x!.Value.Value, x => new BagBarcode(x))
            .HasMaxLength(128);
        entity.HasMany(x => x.Readings)
            .WithOne()
            .HasForeignKey(x => x.ParcelBarcode)
            .OnDelete(DeleteBehavior.Restrict);
        entity.Navigation(x => x.Readings).UsePropertyAccessMode(PropertyAccessMode.Field);
    }

    private static void ConfigureReading(EntityTypeBuilder<ReadingRecord> entity)
    {
        entity.ToTable("ReadingRecords");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.ParcelBarcode).HasConversion(x => x.Value, x => new ParcelBarcode(x)).HasMaxLength(128);
        entity.Property(x => x.IdempotencyKey).HasConversion(x => x.Value, x => new IdempotencyKey(x)).HasMaxLength(128);
        entity.Property(x => x.DeviceId).HasConversion(x => x.Value, x => new DeviceId(x));
        entity.Property(x => x.ExchangeCenterCode).HasConversion(x => x.Value, x => new ExchangeCenterCode(x)).HasMaxLength(32);
        entity.OwnsOne(x => x.PhysicalAttributes, owned =>
        {
            owned.Property(x => x.WeightKg).HasColumnName("WeightKg").HasPrecision(18, 4);
            owned.Property(x => x.LengthCm).HasColumnName("LengthCm").HasPrecision(18, 4);
            owned.Property(x => x.WidthCm).HasColumnName("WidthCm").HasPrecision(18, 4);
            owned.Property(x => x.HeightCm).HasColumnName("HeightCm").HasPrecision(18, 4);
        });
        entity.HasIndex(x => new { x.ParcelBarcode, x.Timestamp });
        entity.HasIndex(x => x.IdempotencyKey).IsUnique();
        entity.HasIndex(x => x.DeviceId);
        entity.HasIndex(x => x.ExchangeCenterCode);
        entity.HasMany(x => x.Discrepancies)
            .WithOne()
            .HasForeignKey(x => x.ReadingRecordId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasMany(x => x.Attachments)
            .WithOne()
            .HasForeignKey(x => x.ReadingRecordId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.Navigation(x => x.Discrepancies).UsePropertyAccessMode(PropertyAccessMode.Field);
        entity.Navigation(x => x.Attachments).UsePropertyAccessMode(PropertyAccessMode.Field);
    }

    private static void ConfigureDiscrepancy(EntityTypeBuilder<DiscrepancyRecord> entity)
    {
        entity.ToTable("DiscrepancyRecords");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.ReferenceValue).HasPrecision(18, 4);
        entity.Property(x => x.ObservedValue).HasPrecision(18, 4);
        entity.HasIndex(x => x.ReadingRecordId);
    }

    private static void ConfigureAttachment(EntityTypeBuilder<SupplementaryAttachment> entity)
    {
        entity.ToTable("SupplementaryAttachments");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.ObjectKey).HasMaxLength(512).IsRequired();
        entity.Property(x => x.ContentType).HasMaxLength(128).IsRequired();
        entity.Property(x => x.AttachmentType).HasMaxLength(64).IsRequired();
        entity.HasIndex(x => new { x.ReadingRecordId, x.ObjectKey }).IsUnique();
    }

    private static void ConfigurePendingAttachment(EntityTypeBuilder<PendingAttachment> entity)
    {
        entity.ToTable("PendingAttachments");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.RequestHash).HasMaxLength(128).IsRequired();
        entity.Property(x => x.PayloadJson).HasMaxLength(32_768).IsRequired();
        entity.Property(x => x.ParcelBarcode).HasConversion(x => x.Value, x => new ParcelBarcode(x)).HasMaxLength(128);
        entity.Property(x => x.ObjectKey).HasMaxLength(512).IsRequired();
        entity.Property(x => x.ContentType).HasMaxLength(128).IsRequired();
        entity.Property(x => x.AttachmentType).HasMaxLength(64).IsRequired();
        entity.Property(x => x.CorrelationId).HasMaxLength(256).IsRequired();
        entity.Property(x => x.LastError).HasMaxLength(2_048);
        entity.HasIndex(x => x.EventId).IsUnique();
        entity.HasIndex(x => new { x.ReadingRecordId, x.ObjectKey }).IsUnique();
        entity.HasIndex(x => new
        {
            x.DeadLetteredAtUtcTicks,
            x.NextAttemptAtUtcTicks,
            x.CreatedAtUtcTicks
        });
    }

    private static void ConfigureBag(EntityTypeBuilder<Bag> entity)
    {
        entity.ToTable("Bags");
        entity.HasKey(x => x.Barcode);
        entity.Property(x => x.Barcode).HasConversion(x => x.Value, x => new BagBarcode(x)).HasMaxLength(128);
        entity.Property(x => x.OriginCenter).HasConversion(x => x.Value, x => new ExchangeCenterCode(x)).HasMaxLength(32);
        entity.Property(x => x.DestinationCenter).HasConversion(x => x.Value, x => new ExchangeCenterCode(x)).HasMaxLength(32);
        entity.Property(x => x.CreatedByDeviceId).HasConversion(x => x.Value, x => new DeviceId(x));
        entity.Property(x => x.AssignedDispatchId)
            .HasConversion(x => x!.Value.Value, x => new DispatchId(x))
            .HasMaxLength(128);
        entity.Property(x => x.SealNumber).HasMaxLength(128).IsRequired();
        entity.Property(x => x.TransportType).HasMaxLength(64).IsRequired();
        entity.Property(x => x.RequestHash).HasMaxLength(128).IsRequired();
        entity.Navigation(x => x.Members).UsePropertyAccessMode(PropertyAccessMode.Field);

        var member = entity.OwnsMany(x => x.Members);
        member.ToTable("BagMembers");
        member.WithOwner().HasForeignKey(x => x.BagBarcode);
        member.HasKey(x => new { x.BagBarcode, x.ParcelBarcode });
        member.Property(x => x.BagBarcode).HasConversion(x => x.Value, x => new BagBarcode(x)).HasMaxLength(128);
        member.Property(x => x.ParcelBarcode).HasConversion(x => x.Value, x => new ParcelBarcode(x)).HasMaxLength(128);
    }

    private static void ConfigureDispatch(EntityTypeBuilder<Dispatch> entity)
    {
        entity.ToTable("Dispatches");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).HasConversion(x => x.Value, x => new DispatchId(x)).HasMaxLength(128);
        entity.Property(x => x.DestinationCenter).HasConversion(x => x.Value, x => new ExchangeCenterCode(x)).HasMaxLength(32);
        entity.Property(x => x.RequestHash).HasMaxLength(128).IsRequired();
        entity.Navigation(x => x.Bags).UsePropertyAccessMode(PropertyAccessMode.Field);
        var bag = entity.OwnsMany(x => x.Bags);
        bag.ToTable("DispatchBags");
        bag.WithOwner().HasForeignKey(x => x.DispatchId);
        bag.HasKey(x => new { x.DispatchId, x.BagBarcode });
        bag.Property(x => x.DispatchId).HasConversion(x => x.Value, x => new DispatchId(x)).HasMaxLength(128);
        bag.Property(x => x.BagBarcode).HasConversion(x => x.Value, x => new BagBarcode(x)).HasMaxLength(128);
    }
}

public sealed class DeviceConfigDbContext(DbContextOptions<DeviceConfigDbContext> options) : DbContext(options)
{
    public DbSet<SortingDevice> SortingDevices => Set<SortingDevice>();

    public DbSet<ConfigurationSnapshot> ConfigurationSnapshots => Set<ConfigurationSnapshot>();

    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var device = modelBuilder.Entity<SortingDevice>();
        device.ToTable("SortingDevices", table => table.HasCheckConstraint(
            "CK_SortingDevices_PreviousSecret_AllOrNone",
            "(PreviousSecretHash IS NULL AND PreviousSecretSalt IS NULL AND " +
            "PreviousSecretIterations IS NULL AND PreviousSecretAlgorithm IS NULL AND " +
            "PreviousSecretValidUntil IS NULL) OR " +
            "(PreviousSecretHash IS NOT NULL AND PreviousSecretSalt IS NOT NULL AND " +
            "PreviousSecretIterations IS NOT NULL AND PreviousSecretAlgorithm IS NOT NULL AND " +
            "PreviousSecretValidUntil IS NOT NULL)"));
        device.HasKey(x => x.Id);
        device.Property(x => x.Id).HasConversion(x => x.Value, x => new DeviceId(x));
        device.Property(x => x.LogicalCode).HasConversion(x => x.Value, x => new LogicalDeviceCode(x)).HasMaxLength(64);
        device.Property(x => x.ExchangeCenterCode).HasConversion(x => x.Value, x => new ExchangeCenterCode(x)).HasMaxLength(32);
        device.Property(x => x.Name).HasMaxLength(128).IsRequired();
        device.Property(x => x.DeviceType).HasMaxLength(64).IsRequired();
        device.Property(x => x.Owner).HasMaxLength(128).IsRequired();
        device.Property(x => x.Description).HasMaxLength(512);
        device.Property(x => x.PreviousSecretHash).HasMaxLength(512);
        device.Property(x => x.PreviousSecretSalt).HasMaxLength(256);
        device.Property(x => x.PreviousSecretAlgorithm).HasMaxLength(64);
        device.HasIndex(x => x.LogicalCode).IsUnique();
        device.HasIndex(x => x.ExchangeCenterCode);
        device.OwnsOne(x => x.SecretVerifier, owned =>
        {
            owned.Property(x => x.Hash).HasColumnName("SecretHash").HasMaxLength(512).IsRequired();
            owned.Property(x => x.Salt).HasColumnName("SecretSalt").HasMaxLength(256).IsRequired();
            owned.Property(x => x.Iterations).HasColumnName("SecretIterations");
            owned.Property(x => x.Algorithm).HasColumnName("SecretAlgorithm").HasMaxLength(64).IsRequired();
        });

        var snapshot = modelBuilder.Entity<ConfigurationSnapshot>();
        snapshot.ToTable("ConfigurationSnapshots", table => table.HasCheckConstraint(
            "CK_ConfigurationSnapshots_MaximumOpenBagDurationTicks",
            $"MaximumOpenBagDurationTicks BETWEEN {TimeSpan.TicksPerMinute} AND " +
            $"{TimeSpan.TicksPerDay * 7}"));
        snapshot.HasKey(x => new { x.ExchangeCenterCode, x.Version });
        snapshot.Property(x => x.ExchangeCenterCode).HasConversion(x => x.Value, x => new ExchangeCenterCode(x)).HasMaxLength(32);
        snapshot.Property(x => x.Version).HasConversion(x => x.Value, x => new ConfigurationVersion(x));
        snapshot.HasIndex(x => new { x.ExchangeCenterCode, x.GeneratedAt });
        snapshot.OwnsOne(x => x.Settings, owned =>
        {
            owned.Property(x => x.ShortReadingThreshold)
                .HasConversion(value => value.Ticks, value => TimeSpan.FromTicks(value))
                .HasColumnName("ShortReadingThresholdTicks");
            owned.Property(x => x.ReturnThreshold)
                .HasConversion(value => value.Ticks, value => TimeSpan.FromTicks(value))
                .HasColumnName("ReturnThresholdTicks");
            owned.Property(x => x.AutoDispatchEnabled).HasColumnName("AutoDispatchEnabled");
            owned.Property(x => x.MaximumOpenBagDuration)
                .HasConversion(value => value.Ticks, value => TimeSpan.FromTicks(value))
                .HasColumnName("MaximumOpenBagDurationTicks");
            owned.OwnsOne(x => x.DiscrepancyTolerance, tolerance =>
            {
                tolerance.Property(x => x.WeightKg).HasColumnName("WeightToleranceKg").HasPrecision(18, 4);
                tolerance.Property(x => x.DimensionCm).HasColumnName("DimensionToleranceCm").HasPrecision(18, 4);
            });
        });
        snapshot.Navigation(x => x.Devices).UsePropertyAccessMode(PropertyAccessMode.Field);
        snapshot.Navigation(x => x.PostalCodes).UsePropertyAccessMode(PropertyAccessMode.Field);

        var snapshotDevices = snapshot.OwnsMany(x => x.Devices);
        snapshotDevices.ToTable("ConfigurationDevices", table => table.HasCheckConstraint(
            "CK_ConfigurationDevices_PreviousSecret_AllOrNone",
            "(PreviousSecretHash IS NULL AND PreviousSecretSalt IS NULL AND " +
            "PreviousSecretIterations IS NULL AND PreviousSecretAlgorithm IS NULL AND " +
            "PreviousSecretValidUntil IS NULL) OR " +
            "(PreviousSecretHash IS NOT NULL AND PreviousSecretSalt IS NOT NULL AND " +
            "PreviousSecretIterations IS NOT NULL AND PreviousSecretAlgorithm IS NOT NULL AND " +
            "PreviousSecretValidUntil IS NOT NULL)"));
        snapshotDevices.WithOwner().HasForeignKey(x => new { x.ExchangeCenterCode, x.Version });
        snapshotDevices.HasKey(x => new { x.ExchangeCenterCode, x.Version, x.DeviceId });
        snapshotDevices.Property(x => x.ExchangeCenterCode).HasConversion(x => x.Value, x => new ExchangeCenterCode(x)).HasMaxLength(32);
        snapshotDevices.Property(x => x.Version).HasConversion(x => x.Value, x => new ConfigurationVersion(x));
        snapshotDevices.Property(x => x.DeviceId).HasConversion(x => x.Value, x => new DeviceId(x));
        snapshotDevices.Property(x => x.LogicalCode).HasConversion(x => x.Value, x => new LogicalDeviceCode(x)).HasMaxLength(64);
        snapshotDevices.Property(x => x.Name).HasMaxLength(128).IsRequired();
        snapshotDevices.Property(x => x.DeviceType).HasMaxLength(64).IsRequired();
        snapshotDevices.Property(x => x.SecretHash).HasMaxLength(512).IsRequired();
        snapshotDevices.Property(x => x.SecretSalt).HasMaxLength(256).IsRequired();
        snapshotDevices.Property(x => x.SecretAlgorithm).HasMaxLength(64).IsRequired();
        snapshotDevices.Property(x => x.PreviousSecretHash).HasMaxLength(512);
        snapshotDevices.Property(x => x.PreviousSecretSalt).HasMaxLength(256);
        snapshotDevices.Property(x => x.PreviousSecretAlgorithm).HasMaxLength(64);

        var postalCodes = snapshot.OwnsMany(x => x.PostalCodes);
        postalCodes.ToTable("ConfigurationPostalCodes");
        postalCodes.WithOwner().HasForeignKey(x => new { x.ExchangeCenterCode, x.Version });
        postalCodes.HasKey(x => new { x.ExchangeCenterCode, x.Version, x.PostalCode });
        postalCodes.Property(x => x.ExchangeCenterCode).HasConversion(x => x.Value, x => new ExchangeCenterCode(x)).HasMaxLength(32);
        postalCodes.Property(x => x.Version).HasConversion(x => x.Value, x => new ConfigurationVersion(x));
        postalCodes.Property(x => x.PostalCode).HasConversion(x => x.Value, x => new ExchangeCenterCode(x)).HasMaxLength(32);

        ReliabilityConfiguration.ConfigureIdempotency(modelBuilder.Entity<IdempotencyRecord>());
        ReliabilityConfiguration.ConfigureOutbox(modelBuilder.Entity<OutboxMessage>());
    }
}

public sealed class EdgeHealthDbContext(DbContextOptions<EdgeHealthDbContext> options) : DbContext(options)
{
    public DbSet<EdgeHealthStatus> EdgeHealthStatuses => Set<EdgeHealthStatus>();

    public DbSet<OperationalEvent> OperationalEvents => Set<OperationalEvent>();

    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var status = modelBuilder.Entity<EdgeHealthStatus>();
        status.ToTable("EdgeHealthStatuses");
        status.HasKey(x => x.EdgeId);
        status.Property(x => x.EdgeId).HasConversion(x => x.Value, x => new EdgeId(x));
        status.Property(x => x.ExchangeCenterCode).HasConversion(x => x.Value, x => new ExchangeCenterCode(x)).HasMaxLength(32);
        status.Property(x => x.SoftwareVersion).HasMaxLength(64).IsRequired();
        status.Property(x => x.AvailableDiskBytes).IsRequired();
        status.HasIndex(x => x.LastHeartbeat);

        var operationalEvent = modelBuilder.Entity<OperationalEvent>();
        operationalEvent.ToTable("OperationalEvents");
        operationalEvent.HasKey(x => x.Id);
        operationalEvent.Property(x => x.EdgeId).HasConversion(x => x.Value, x => new EdgeId(x));
        operationalEvent.Property(x => x.DeviceId).HasConversion(x => x!.Value.Value, x => new DeviceId(x));
        operationalEvent.Property(x => x.CorrelationId).HasConversion(x => x.Value, x => new CorrelationId(x)).HasMaxLength(128);
        operationalEvent.HasIndex(x => new { x.EdgeId, x.OccurredAt });

        ReliabilityConfiguration.ConfigureIdempotency(modelBuilder.Entity<IdempotencyRecord>());
        ReliabilityConfiguration.ConfigureOutbox(modelBuilder.Entity<OutboxMessage>());
    }
}

public sealed class OperationalResultDbContext(DbContextOptions<OperationalResultDbContext> options) : DbContext(options)
{
    public DbSet<OperationalResult> OperationalResults => Set<OperationalResult>();

    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var result = modelBuilder.Entity<OperationalResult>();
        result.ToTable("OperationalResults", table =>
        {
            table.HasCheckConstraint(
                "CK_OperationalResults_EdgeId_NotEmpty",
                "[EdgeId] <> '00000000-0000-0000-0000-000000000000'");
            table.HasCheckConstraint(
                "CK_OperationalResults_ExchangeCenterCode_NotEmpty",
                "[ExchangeCenterCode] <> ''");
        });
        result.HasKey(x => x.Id);
        result.Property(x => x.EdgeId).HasConversion(x => x.Value, x => new EdgeId(x)).IsRequired();
        result.Property(x => x.ExchangeCenterCode)
            .HasConversion(x => x.Value, x => new ExchangeCenterCode(x))
            .HasMaxLength(32)
            .IsRequired();
        result.Property(x => x.IdempotencyKey).HasConversion(x => x.Value, x => new IdempotencyKey(x)).HasMaxLength(128);
        result.Property(x => x.CorrelationId).HasConversion(x => x.Value, x => new CorrelationId(x)).HasMaxLength(128);
        result.Property(x => x.ParcelBarcode).HasConversion(x => x!.Value.Value, x => new ParcelBarcode(x)).HasMaxLength(128);
        result.Property(x => x.RequestHash).HasMaxLength(128).IsRequired();
        result.Property(x => x.CallResult).HasMaxLength(128).IsRequired();
        result.Property(x => x.ErrorCode).HasMaxLength(128);
        result.Property(x => x.ErrorMessage).HasMaxLength(2048);
        result.HasIndex(x => x.IdempotencyKey).IsUnique();
        result.HasIndex(x => x.CorrelationId);
        result.HasIndex(x => x.ParcelBarcode);
        result.HasIndex(x => x.EdgeId);
        result.HasIndex(x => x.ExchangeCenterCode);

        ReliabilityConfiguration.ConfigureIdempotency(modelBuilder.Entity<IdempotencyRecord>());
        ReliabilityConfiguration.ConfigureOutbox(modelBuilder.Entity<OutboxMessage>());
    }
}

public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options)
    : IdentityDbContext<AppUser, AppRole, Guid>(options)
{
    public DbSet<UserAllowedCenter> UserAllowedCenters => Set<UserAllowedCenter>();

    public DbSet<IdentityBootstrapLock> IdentityBootstrapLocks => Set<IdentityBootstrapLock>();

    public DbSet<EdgeCredential> EdgeCredentials => Set<EdgeCredential>();

    public DbSet<AuditRecord> AuditRecords => Set<AuditRecord>();

    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        var user = builder.Entity<AppUser>();
        user.ToTable("AspNetUsers");
        user.Property(x => x.UserName).HasMaxLength(128);
        user.Property(x => x.NormalizedUserName).HasMaxLength(128);
        user.Property(x => x.PasswordHash).HasMaxLength(1024);
        user.Property(x => x.IsActive).IsRequired();

        var role = builder.Entity<AppRole>();
        role.ToTable("AspNetRoles");
        role.Property(x => x.Name).HasMaxLength(128);
        role.Property(x => x.NormalizedName).HasMaxLength(128);

        var center = builder.Entity<UserAllowedCenter>();
        center.ToTable("UserAllowedCenters");
        center.HasKey(x => new { x.UserId, x.ExchangeCenterCode });
        center.Property(x => x.ExchangeCenterCode).HasMaxLength(32).IsRequired();
        center.HasOne(x => x.User)
            .WithMany(x => x.AllowedCenters)
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        var bootstrapLock = builder.Entity<IdentityBootstrapLock>();
        bootstrapLock.ToTable("IdentityBootstrapLocks", table =>
            table.HasCheckConstraint("CK_IdentityBootstrapLocks_Singleton", "[Id] = 1"));
        bootstrapLock.HasKey(x => x.Id);
        bootstrapLock.Property(x => x.Id).ValueGeneratedNever();
        bootstrapLock.HasIndex(x => x.UserId).IsUnique();

        var credential = builder.Entity<EdgeCredential>();
        credential.ToTable("EdgeCredentials");
        credential.HasKey(x => x.DeviceId);
        credential.Property(x => x.DeviceId).HasConversion(x => x.Value, x => new DeviceId(x));
        credential.Property(x => x.LogicalDeviceCode).HasConversion(x => x.Value, x => new LogicalDeviceCode(x)).HasMaxLength(64);
        credential.Property(x => x.ExchangeCenterCode).HasConversion(x => x.Value, x => new ExchangeCenterCode(x)).HasMaxLength(32);
        credential.HasIndex(x => x.LogicalDeviceCode).IsUnique();
        credential.OwnsOne(x => x.SecretVerifier, owned =>
        {
            owned.Property(x => x.Hash).HasColumnName("SecretHash").HasMaxLength(512).IsRequired();
            owned.Property(x => x.Salt).HasColumnName("SecretSalt").HasMaxLength(256).IsRequired();
            owned.Property(x => x.Iterations).HasColumnName("SecretIterations");
            owned.Property(x => x.Algorithm).HasColumnName("SecretAlgorithm").HasMaxLength(64).IsRequired();
        });

        var audit = builder.Entity<AuditRecord>();
        audit.ToTable("AuditRecords");
        audit.HasKey(x => x.Id);
        audit.Property(x => x.Actor).HasMaxLength(256).IsRequired();
        audit.Property(x => x.Action).HasMaxLength(128).IsRequired();
        audit.Property(x => x.ResourceType).HasMaxLength(128).IsRequired();
        audit.Property(x => x.ResourceId).HasMaxLength(256).IsRequired();
        audit.Property(x => x.Details).HasMaxLength(4096);
        audit.Property(x => x.CorrelationId).HasConversion(x => x.Value, x => new CorrelationId(x)).HasMaxLength(128);
        audit.HasIndex(x => new { x.OccurredAt, x.Action });
        audit.HasIndex(x => x.Actor);

        ReliabilityConfiguration.ConfigureIdempotency(builder.Entity<IdempotencyRecord>());
        ReliabilityConfiguration.ConfigureOutbox(builder.Entity<OutboxMessage>());
    }
}

internal static class ReliabilityConfiguration
{
    public static void ConfigureIdempotency(EntityTypeBuilder<IdempotencyRecord> entity)
    {
        entity.ToTable("IdempotencyRecords");
        entity.HasKey(x => x.Key);
        entity.Property(x => x.Key).HasConversion(x => x.Value, x => new IdempotencyKey(x)).HasMaxLength(128);
        entity.Property(x => x.RequestHash).HasMaxLength(128).IsRequired();
        entity.Property(x => x.ResponseSnapshot).HasMaxLength(1_000_000);
        entity.HasIndex(x => x.Key).IsUnique();
    }

    public static void ConfigureInbox(EntityTypeBuilder<InboxMessage> entity)
    {
        entity.ToTable("InboxMessages");
        entity.HasKey(x => x.EventId);
        entity.Property(x => x.PayloadHash).HasMaxLength(128).IsRequired();
    }

    public static void ConfigureOutbox(EntityTypeBuilder<OutboxMessage> entity)
    {
        entity.ToTable("OutboxMessages");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Topic).HasMaxLength(256).IsRequired();
        entity.Property(x => x.Payload).HasMaxLength(1_000_000).IsRequired();
        entity.Property(x => x.CorrelationId).HasConversion(x => x.Value, x => new CorrelationId(x)).HasMaxLength(128);
        entity.Property(x => x.LastError).HasMaxLength(2048);
        entity.HasIndex(x => new { x.Status, x.Sequence });
    }
}
