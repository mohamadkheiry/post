using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Post.Core.Infrastructure.Persistence.Migrations.ParcelLifecycle
{
    /// <inheritdoc />
    public partial class InitialParcelLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Bags",
                columns: table => new
                {
                    Barcode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    OriginCenter = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    DestinationCenter = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CreatedByDeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SealNumber = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    TransportType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ClosedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    AssignedDispatchId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    RequestHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Bags", x => x.Barcode);
                });

            migrationBuilder.CreateTable(
                name: "Dispatches",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    DestinationCenter = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ClosedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RequestHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dispatches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IdempotencyRecords",
                columns: table => new
                {
                    Key = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    RequestHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ResponseSnapshot = table.Column<string>(type: "nvarchar(max)", maxLength: 1000000, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdempotencyRecords", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "InboxMessages",
                columns: table => new
                {
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PayloadHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboxMessages", x => x.EventId);
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Topic = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Payload = table.Column<string>(type: "nvarchar(max)", maxLength: 1000000, nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ParcelDossiers",
                columns: table => new
                {
                    Barcode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ParcelType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ServiceType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    OriginCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    DestinationCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    OperationalDestinationCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    InboundFinalStatus = table.Column<int>(type: "int", nullable: true),
                    ExportStatus = table.Column<int>(type: "int", nullable: false),
                    PhysicalBagReference = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ParcelDossiers", x => x.Barcode);
                });

            migrationBuilder.CreateTable(
                name: "PendingAttachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ParcelBarcode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ReadingRecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ObjectKey = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    AttachmentType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingAttachments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BagMembers",
                columns: table => new
                {
                    BagBarcode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ParcelBarcode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BagMembers", x => new { x.BagBarcode, x.ParcelBarcode });
                    table.ForeignKey(
                        name: "FK_BagMembers_Bags_BagBarcode",
                        column: x => x.BagBarcode,
                        principalTable: "Bags",
                        principalColumn: "Barcode",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DispatchBags",
                columns: table => new
                {
                    DispatchId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    BagBarcode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DispatchBags", x => new { x.DispatchId, x.BagBarcode });
                    table.ForeignKey(
                        name: "FK_DispatchBags_Dispatches_DispatchId",
                        column: x => x.DispatchId,
                        principalTable: "Dispatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReadingRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ParcelBarcode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExchangeCenterCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    WeightKg = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    LengthCm = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    WidthCm = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    HeightCm = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    Classification = table.Column<int>(type: "int", nullable: false),
                    PreviousReadingId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EdgeEventTime = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CoreReceiveTime = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReadingRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReadingRecords_ParcelDossiers_ParcelBarcode",
                        column: x => x.ParcelBarcode,
                        principalTable: "ParcelDossiers",
                        principalColumn: "Barcode",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DiscrepancyRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReadingRecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Attribute = table.Column<int>(type: "int", nullable: false),
                    ReferenceValue = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    ObservedValue = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscrepancyRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DiscrepancyRecords_ReadingRecords_ReadingRecordId",
                        column: x => x.ReadingRecordId,
                        principalTable: "ReadingRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SupplementaryAttachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReadingRecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ObjectKey = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    AttachmentType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplementaryAttachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SupplementaryAttachments_ReadingRecords_ReadingRecordId",
                        column: x => x.ReadingRecordId,
                        principalTable: "ReadingRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DiscrepancyRecords_ReadingRecordId",
                table: "DiscrepancyRecords",
                column: "ReadingRecordId");

            migrationBuilder.CreateIndex(
                name: "IX_IdempotencyRecords_Key",
                table: "IdempotencyRecords",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_Status_Sequence",
                table: "OutboxMessages",
                columns: new[] { "Status", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_PendingAttachments_ReadingRecordId_ObjectKey",
                table: "PendingAttachments",
                columns: new[] { "ReadingRecordId", "ObjectKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReadingRecords_DeviceId",
                table: "ReadingRecords",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_ReadingRecords_ExchangeCenterCode",
                table: "ReadingRecords",
                column: "ExchangeCenterCode");

            migrationBuilder.CreateIndex(
                name: "IX_ReadingRecords_IdempotencyKey",
                table: "ReadingRecords",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReadingRecords_ParcelBarcode_Timestamp",
                table: "ReadingRecords",
                columns: new[] { "ParcelBarcode", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplementaryAttachments_ReadingRecordId_ObjectKey",
                table: "SupplementaryAttachments",
                columns: new[] { "ReadingRecordId", "ObjectKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BagMembers");

            migrationBuilder.DropTable(
                name: "DiscrepancyRecords");

            migrationBuilder.DropTable(
                name: "DispatchBags");

            migrationBuilder.DropTable(
                name: "IdempotencyRecords");

            migrationBuilder.DropTable(
                name: "InboxMessages");

            migrationBuilder.DropTable(
                name: "OutboxMessages");

            migrationBuilder.DropTable(
                name: "PendingAttachments");

            migrationBuilder.DropTable(
                name: "SupplementaryAttachments");

            migrationBuilder.DropTable(
                name: "Bags");

            migrationBuilder.DropTable(
                name: "Dispatches");

            migrationBuilder.DropTable(
                name: "ReadingRecords");

            migrationBuilder.DropTable(
                name: "ParcelDossiers");
        }
    }
}
