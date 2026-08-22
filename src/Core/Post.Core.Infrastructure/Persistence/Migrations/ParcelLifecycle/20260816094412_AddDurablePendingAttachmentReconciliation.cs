using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Post.Core.Infrastructure.Persistence.Migrations.ParcelLifecycle
{
    /// <inheritdoc />
    public partial class AddDurablePendingAttachmentReconciliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CorrelationId",
                table: "PendingAttachments",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CreatedAt",
                table: "PendingAttachments",
                type: "datetimeoffset",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<long>(
                name: "CreatedAtUtcTicks",
                table: "PendingAttachments",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeadLetteredAt",
                table: "PendingAttachments",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "DeadLetteredAtUtcTicks",
                table: "PendingAttachments",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "EventId",
                table: "PendingAttachments",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "LastError",
                table: "PendingAttachments",
                type: "nvarchar(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "NextAttemptAtUtcTicks",
                table: "PendingAttachments",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PayloadJson",
                table: "PendingAttachments",
                type: "nvarchar(max)",
                maxLength: 32768,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "RequestHash",
                table: "PendingAttachments",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            // The original authenticated envelope did not exist in the previous schema.
            // Preserve legacy rows for manual recovery and keep them out of automatic retries.
            migrationBuilder.Sql(
                """
                UPDATE [PendingAttachments]
                SET [EventId] = NEWID(),
                    [RequestHash] = REPLACE(CONVERT(varchar(36), NEWID()), '-', ''),
                    [PayloadJson] = '{}',
                    [CorrelationId] = 'legacy-pending-attachment',
                    [CreatedAt] = [ReceivedAt],
                    [CreatedAtUtcTicks] = DATEDIFF_BIG(
                        MICROSECOND,
                        CONVERT(datetime2, '0001-01-01T00:00:00'),
                        CONVERT(datetime2, SWITCHOFFSET([ReceivedAt], '+00:00'))) * 10,
                    [DeadLetteredAt] = SYSUTCDATETIME(),
                    [DeadLetteredAtUtcTicks] = DATEDIFF_BIG(
                        MICROSECOND,
                        CONVERT(datetime2, '0001-01-01T00:00:00'),
                        SYSUTCDATETIME()) * 10,
                    [LastError] = 'Legacy pending attachment requires manual reconciliation.',
                    [NextAttemptAt] = NULL,
                    [NextAttemptAtUtcTicks] = NULL
                WHERE [EventId] = '00000000-0000-0000-0000-000000000000';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_PendingAttachments_DeadLetteredAtUtcTicks_NextAttemptAtUtcTicks_CreatedAtUtcTicks",
                table: "PendingAttachments",
                columns: new[] { "DeadLetteredAtUtcTicks", "NextAttemptAtUtcTicks", "CreatedAtUtcTicks" });

            migrationBuilder.CreateIndex(
                name: "IX_PendingAttachments_EventId",
                table: "PendingAttachments",
                column: "EventId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PendingAttachments_DeadLetteredAtUtcTicks_NextAttemptAtUtcTicks_CreatedAtUtcTicks",
                table: "PendingAttachments");

            migrationBuilder.DropIndex(
                name: "IX_PendingAttachments_EventId",
                table: "PendingAttachments");

            migrationBuilder.DropColumn(
                name: "CorrelationId",
                table: "PendingAttachments");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "PendingAttachments");

            migrationBuilder.DropColumn(
                name: "CreatedAtUtcTicks",
                table: "PendingAttachments");

            migrationBuilder.DropColumn(
                name: "DeadLetteredAt",
                table: "PendingAttachments");

            migrationBuilder.DropColumn(
                name: "DeadLetteredAtUtcTicks",
                table: "PendingAttachments");

            migrationBuilder.DropColumn(
                name: "EventId",
                table: "PendingAttachments");

            migrationBuilder.DropColumn(
                name: "LastError",
                table: "PendingAttachments");

            migrationBuilder.DropColumn(
                name: "NextAttemptAtUtcTicks",
                table: "PendingAttachments");

            migrationBuilder.DropColumn(
                name: "PayloadJson",
                table: "PendingAttachments");

            migrationBuilder.DropColumn(
                name: "RequestHash",
                table: "PendingAttachments");
        }
    }
}
