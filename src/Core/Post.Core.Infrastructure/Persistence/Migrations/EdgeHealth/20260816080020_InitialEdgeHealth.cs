using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Post.Core.Infrastructure.Persistence.Migrations.EdgeHealth
{
    /// <inheritdoc />
    public partial class InitialEdgeHealth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EdgeHealthStatuses",
                columns: table => new
                {
                    EdgeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExchangeCenterCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    LastHeartbeat = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SoftwareVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ConfigurationVersion = table.Column<long>(type: "bigint", nullable: false),
                    ConnectionState = table.Column<int>(type: "int", nullable: false),
                    PendingCount = table.Column<int>(type: "int", nullable: false),
                    FailedCount = table.Column<int>(type: "int", nullable: false),
                    DeadLetterCount = table.Column<int>(type: "int", nullable: false),
                    LastSuccessfulSync = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EdgeHealthStatuses", x => x.EdgeId);
                });

            migrationBuilder.CreateTable(
                name: "OperationalEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventType = table.Column<int>(type: "int", nullable: false),
                    EdgeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperationalEvents", x => x.Id);
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

            migrationBuilder.CreateIndex(
                name: "IX_EdgeHealthStatuses_LastHeartbeat",
                table: "EdgeHealthStatuses",
                column: "LastHeartbeat");

            migrationBuilder.CreateIndex(
                name: "IX_OperationalEvents_EdgeId_OccurredAt",
                table: "OperationalEvents",
                columns: new[] { "EdgeId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_Status_Sequence",
                table: "OutboxMessages",
                columns: new[] { "Status", "Sequence" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EdgeHealthStatuses");

            migrationBuilder.DropTable(
                name: "OperationalEvents");

            migrationBuilder.DropTable(
                name: "OutboxMessages");
        }
    }
}
