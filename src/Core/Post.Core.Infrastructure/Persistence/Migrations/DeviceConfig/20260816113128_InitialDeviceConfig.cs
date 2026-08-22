using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Post.Core.Infrastructure.Persistence.Migrations.DeviceConfig
{
    /// <inheritdoc />
    public partial class InitialDeviceConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConfigurationSnapshots",
                columns: table => new
                {
                    ExchangeCenterCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    GeneratedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ShortReadingThresholdTicks = table.Column<long>(type: "bigint", nullable: false),
                    ReturnThresholdTicks = table.Column<long>(type: "bigint", nullable: false),
                    WeightToleranceKg = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    DimensionToleranceCm = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    AutoDispatchEnabled = table.Column<bool>(type: "bit", nullable: false),
                    MaximumOpenBagDurationTicks = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfigurationSnapshots", x => new { x.ExchangeCenterCode, x.Version });
                    table.CheckConstraint("CK_ConfigurationSnapshots_MaximumOpenBagDurationTicks", "MaximumOpenBagDurationTicks BETWEEN 600000000 AND 6048000000000");
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
                name: "SortingDevices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    DeviceType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    LogicalCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Owner = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ExchangeCenterCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    SecretHash = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    SecretSalt = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    SecretIterations = table.Column<int>(type: "int", nullable: false),
                    SecretAlgorithm = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PreviousSecretHash = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    PreviousSecretSalt = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    PreviousSecretIterations = table.Column<int>(type: "int", nullable: true),
                    PreviousSecretAlgorithm = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    PreviousSecretValidUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ActivationStatus = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SortingDevices", x => x.Id);
                    table.CheckConstraint("CK_SortingDevices_PreviousSecret_AllOrNone", "(PreviousSecretHash IS NULL AND PreviousSecretSalt IS NULL AND PreviousSecretIterations IS NULL AND PreviousSecretAlgorithm IS NULL AND PreviousSecretValidUntil IS NULL) OR (PreviousSecretHash IS NOT NULL AND PreviousSecretSalt IS NOT NULL AND PreviousSecretIterations IS NOT NULL AND PreviousSecretAlgorithm IS NOT NULL AND PreviousSecretValidUntil IS NOT NULL)");
                });

            migrationBuilder.CreateTable(
                name: "ConfigurationDevices",
                columns: table => new
                {
                    ExchangeCenterCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LogicalCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    DeviceType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ActivationStatus = table.Column<int>(type: "int", nullable: false),
                    SecretHash = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    SecretSalt = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    SecretIterations = table.Column<int>(type: "int", nullable: false),
                    SecretAlgorithm = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PreviousSecretHash = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    PreviousSecretSalt = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    PreviousSecretIterations = table.Column<int>(type: "int", nullable: true),
                    PreviousSecretAlgorithm = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    PreviousSecretValidUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfigurationDevices", x => new { x.ExchangeCenterCode, x.Version, x.DeviceId });
                    table.CheckConstraint("CK_ConfigurationDevices_PreviousSecret_AllOrNone", "(PreviousSecretHash IS NULL AND PreviousSecretSalt IS NULL AND PreviousSecretIterations IS NULL AND PreviousSecretAlgorithm IS NULL AND PreviousSecretValidUntil IS NULL) OR (PreviousSecretHash IS NOT NULL AND PreviousSecretSalt IS NOT NULL AND PreviousSecretIterations IS NOT NULL AND PreviousSecretAlgorithm IS NOT NULL AND PreviousSecretValidUntil IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_ConfigurationDevices_ConfigurationSnapshots_ExchangeCenterCode_Version",
                        columns: x => new { x.ExchangeCenterCode, x.Version },
                        principalTable: "ConfigurationSnapshots",
                        principalColumns: new[] { "ExchangeCenterCode", "Version" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ConfigurationPostalCodes",
                columns: table => new
                {
                    ExchangeCenterCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    PostalCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfigurationPostalCodes", x => new { x.ExchangeCenterCode, x.Version, x.PostalCode });
                    table.ForeignKey(
                        name: "FK_ConfigurationPostalCodes_ConfigurationSnapshots_ExchangeCenterCode_Version",
                        columns: x => new { x.ExchangeCenterCode, x.Version },
                        principalTable: "ConfigurationSnapshots",
                        principalColumns: new[] { "ExchangeCenterCode", "Version" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConfigurationSnapshots_ExchangeCenterCode_GeneratedAt",
                table: "ConfigurationSnapshots",
                columns: new[] { "ExchangeCenterCode", "GeneratedAt" });

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
                name: "IX_SortingDevices_ExchangeCenterCode",
                table: "SortingDevices",
                column: "ExchangeCenterCode");

            migrationBuilder.CreateIndex(
                name: "IX_SortingDevices_LogicalCode",
                table: "SortingDevices",
                column: "LogicalCode",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConfigurationDevices");

            migrationBuilder.DropTable(
                name: "ConfigurationPostalCodes");

            migrationBuilder.DropTable(
                name: "IdempotencyRecords");

            migrationBuilder.DropTable(
                name: "OutboxMessages");

            migrationBuilder.DropTable(
                name: "SortingDevices");

            migrationBuilder.DropTable(
                name: "ConfigurationSnapshots");
        }
    }
}
