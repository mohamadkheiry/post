using Microsoft.EntityFrameworkCore.Migrations;

#pragma warning disable CA1861

#nullable disable

namespace Post.Edge.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class InitialEdge : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "dead_letters",
            columns: table => new
            {
                MessageId = table.Column<Guid>(type: "TEXT", nullable: false),
                Target = table.Column<string>(type: "TEXT", nullable: false),
                MessageType = table.Column<string>(type: "TEXT", nullable: false),
                PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                IdempotencyKey = table.Column<string>(type: "TEXT", nullable: false),
                OccurredAt = table.Column<long>(type: "INTEGER", nullable: false),
                Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                FailedAt = table.Column<long>(type: "INTEGER", nullable: false),
                Error = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_dead_letters", x => x.MessageId);
            });

        migrationBuilder.CreateTable(
            name: "device_network_bindings",
            columns: table => new
            {
                DeviceId = table.Column<string>(type: "TEXT", nullable: false),
                IpAddress = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_device_network_bindings", x => new { x.DeviceId, x.IpAddress });
            });

        migrationBuilder.CreateTable(
            name: "device_sessions",
            columns: table => new
            {
                ConnectionId = table.Column<string>(type: "TEXT", nullable: false),
                SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                SourceIp = table.Column<string>(type: "TEXT", nullable: false),
                DeviceId = table.Column<string>(type: "TEXT", nullable: true),
                ConnectedAt = table.Column<long>(type: "INTEGER", nullable: false),
                DisconnectedAt = table.Column<long>(type: "INTEGER", nullable: true),
                State = table.Column<int>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_device_sessions", x => x.ConnectionId);
            });

        migrationBuilder.CreateTable(
            name: "edge_bags",
            columns: table => new
            {
                BagBarcode = table.Column<string>(type: "TEXT", nullable: false),
                LocalOperationId = table.Column<Guid>(type: "TEXT", nullable: false),
                DeviceId = table.Column<string>(type: "TEXT", nullable: false),
                DestinationCode = table.Column<string>(type: "TEXT", nullable: false),
                MemberBarcodesJson = table.Column<string>(type: "TEXT", nullable: false),
                Label = table.Column<byte[]>(type: "BLOB", nullable: false),
                SealNumber = table.Column<string>(type: "TEXT", nullable: false),
                TransportType = table.Column<string>(type: "TEXT", nullable: false),
                N = table.Column<int>(type: "INTEGER", nullable: false),
                P = table.Column<int>(type: "INTEGER", nullable: false),
                M = table.Column<int>(type: "INTEGER", nullable: false),
                Q = table.Column<int>(type: "INTEGER", nullable: false),
                ErrorsJson = table.Column<string>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                DispatchId = table.Column<string>(type: "TEXT", nullable: true),
                CoreConfirmed = table.Column<bool>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_edge_bags", x => x.BagBarcode);
            });

        migrationBuilder.CreateTable(
            name: "edge_configuration",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Version = table.Column<long>(type: "INTEGER", nullable: false),
                PayloadJson = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_edge_configuration", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "edge_dispatches",
            columns: table => new
            {
                LocalOperationId = table.Column<Guid>(type: "TEXT", nullable: false),
                DispatchId = table.Column<string>(type: "TEXT", nullable: false),
                DeviceId = table.Column<string>(type: "TEXT", nullable: false),
                DestinationCode = table.Column<string>(type: "TEXT", nullable: false),
                BagBarcodesJson = table.Column<string>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                CoreConfirmed = table.Column<bool>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_edge_dispatches", x => x.LocalOperationId);
            });

        migrationBuilder.CreateTable(
            name: "edge_parcels",
            columns: table => new
            {
                ReadId = table.Column<Guid>(type: "TEXT", nullable: false),
                CanonicalBarcode = table.Column<string>(type: "TEXT", nullable: false),
                AliasesJson = table.Column<string>(type: "TEXT", nullable: false),
                DeviceId = table.Column<string>(type: "TEXT", nullable: false),
                ReadTimestamp = table.Column<long>(type: "INTEGER", nullable: false),
                WeightGrams = table.Column<int>(type: "INTEGER", nullable: true),
                LengthMm = table.Column<int>(type: "INTEGER", nullable: true),
                WidthMm = table.Column<int>(type: "INTEGER", nullable: true),
                HeightMm = table.Column<int>(type: "INTEGER", nullable: true),
                ParcelType = table.Column<string>(type: "TEXT", nullable: true),
                ServiceType = table.Column<string>(type: "TEXT", nullable: true),
                State = table.Column<int>(type: "INTEGER", nullable: false),
                DestinationCode = table.Column<string>(type: "TEXT", nullable: true),
                ChuteId = table.Column<string>(type: "TEXT", nullable: true),
                PreviousReadId = table.Column<Guid>(type: "TEXT", nullable: true),
                BagBarcode = table.Column<string>(type: "TEXT", nullable: true),
                ErrorsJson = table.Column<string>(type: "TEXT", nullable: false),
                CoreSynced = table.Column<bool>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_edge_parcels", x => x.ReadId);
            });

        migrationBuilder.CreateTable(
            name: "inbound_evidence",
            columns: table => new
            {
                ReadId = table.Column<Guid>(type: "TEXT", nullable: false),
                SupplementaryDataJson = table.Column<string>(type: "TEXT", nullable: true),
                CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_inbound_evidence", x => x.ReadId);
            });

        migrationBuilder.CreateTable(
            name: "inbound_images",
            columns: table => new
            {
                ReadId = table.Column<Guid>(type: "TEXT", nullable: false),
                ImageId = table.Column<string>(type: "TEXT", nullable: false),
                Content = table.Column<byte[]>(type: "BLOB", nullable: false),
                MimeType = table.Column<string>(type: "TEXT", nullable: true),
                Description = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_inbound_images", x => new { x.ReadId, x.ImageId });
            });

        migrationBuilder.CreateTable(
            name: "operation_journal",
            columns: table => new
            {
                OperationId = table.Column<Guid>(type: "TEXT", nullable: false),
                OperationType = table.Column<string>(type: "TEXT", nullable: false),
                ResponseJson = table.Column<string>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_operation_journal", x => new { x.OperationId, x.OperationType });
            });

        migrationBuilder.CreateTable(
            name: "outbox",
            columns: table => new
            {
                MessageId = table.Column<Guid>(type: "TEXT", nullable: false),
                Target = table.Column<string>(type: "TEXT", nullable: false),
                MessageType = table.Column<string>(type: "TEXT", nullable: false),
                PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                IdempotencyKey = table.Column<string>(type: "TEXT", nullable: false),
                OccurredAt = table.Column<long>(type: "INTEGER", nullable: false),
                Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                NextAttemptAt = table.Column<long>(type: "INTEGER", nullable: false),
                LockedUntil = table.Column<long>(type: "INTEGER", nullable: true),
                ProcessedAt = table.Column<long>(type: "INTEGER", nullable: true),
                LastError = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_outbox", x => x.MessageId);
            });

        migrationBuilder.CreateTable(
            name: "pending_bag_closures",
            columns: table => new
            {
                LocalOperationId = table.Column<Guid>(type: "TEXT", nullable: false),
                DeviceId = table.Column<string>(type: "TEXT", nullable: false),
                DestinationCode = table.Column<string>(type: "TEXT", nullable: false),
                ParcelReadIdsJson = table.Column<string>(type: "TEXT", nullable: false),
                SealNumber = table.Column<string>(type: "TEXT", nullable: false),
                TransportType = table.Column<string>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                State = table.Column<int>(type: "INTEGER", nullable: false),
                Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                NextAttemptAt = table.Column<long>(type: "INTEGER", nullable: true),
                LastError = table.Column<string>(type: "TEXT", nullable: true),
                FinalBagBarcode = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_pending_bag_closures", x => x.LocalOperationId);
            });

        migrationBuilder.CreateTable(
            name: "pending_inbounds",
            columns: table => new
            {
                OperationId = table.Column<Guid>(type: "TEXT", nullable: false),
                ReadId = table.Column<Guid>(type: "TEXT", nullable: false),
                PostalTrackingId = table.Column<string>(type: "TEXT", nullable: false),
                Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                NextAttemptAt = table.Column<long>(type: "INTEGER", nullable: false),
                LastError = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_pending_inbounds", x => x.OperationId);
            });

        migrationBuilder.CreateIndex(
            name: "IX_dead_letters_IdempotencyKey",
            table: "dead_letters",
            column: "IdempotencyKey");

        migrationBuilder.CreateIndex(
            name: "IX_device_sessions_SessionId",
            table: "device_sessions",
            column: "SessionId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_edge_bags_DeviceId_DestinationCode_DispatchId",
            table: "edge_bags",
            columns: ["DeviceId", "DestinationCode", "DispatchId"]);

        migrationBuilder.CreateIndex(
            name: "IX_edge_bags_LocalOperationId",
            table: "edge_bags",
            column: "LocalOperationId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_edge_dispatches_DispatchId",
            table: "edge_dispatches",
            column: "DispatchId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_edge_parcels_CanonicalBarcode",
            table: "edge_parcels",
            column: "CanonicalBarcode");

        migrationBuilder.CreateIndex(
            name: "IX_edge_parcels_DeviceId_DestinationCode_State_ReadTimestamp",
            table: "edge_parcels",
            columns: ["DeviceId", "DestinationCode", "State", "ReadTimestamp"]);

        migrationBuilder.CreateIndex(
            name: "IX_outbox_IdempotencyKey",
            table: "outbox",
            column: "IdempotencyKey",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_outbox_ProcessedAt_NextAttemptAt_LockedUntil",
            table: "outbox",
            columns: ["ProcessedAt", "NextAttemptAt", "LockedUntil"]);

        migrationBuilder.CreateIndex(
            name: "IX_pending_bag_closures_State_NextAttemptAt",
            table: "pending_bag_closures",
            columns: ["State", "NextAttemptAt"]);

        migrationBuilder.CreateIndex(
            name: "IX_pending_inbounds_NextAttemptAt",
            table: "pending_inbounds",
            column: "NextAttemptAt");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "dead_letters");

        migrationBuilder.DropTable(
            name: "device_network_bindings");

        migrationBuilder.DropTable(
            name: "device_sessions");

        migrationBuilder.DropTable(
            name: "edge_bags");

        migrationBuilder.DropTable(
            name: "edge_configuration");

        migrationBuilder.DropTable(
            name: "edge_dispatches");

        migrationBuilder.DropTable(
            name: "edge_parcels");

        migrationBuilder.DropTable(
            name: "inbound_evidence");

        migrationBuilder.DropTable(
            name: "inbound_images");

        migrationBuilder.DropTable(
            name: "operation_journal");

        migrationBuilder.DropTable(
            name: "outbox");

        migrationBuilder.DropTable(
            name: "pending_bag_closures");

        migrationBuilder.DropTable(
            name: "pending_inbounds");
    }
}
