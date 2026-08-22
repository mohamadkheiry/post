using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Post.Core.Infrastructure.Persistence.Migrations.OperationalResult
{
    /// <inheritdoc />
    public partial class AddOperationalResultProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                IF EXISTS (SELECT 1 FROM [OperationalResults])
                    THROW 51000, 'OperationalResults contains legacy rows without trustworthy Edge provenance. Backfill them through an approved migration before retrying.', 1;
                """);

            migrationBuilder.AddColumn<Guid>(
                name: "EdgeId",
                table: "OperationalResults",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "ExchangeCenterCode",
                table: "OperationalResults",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_OperationalResults_EdgeId",
                table: "OperationalResults",
                column: "EdgeId");

            migrationBuilder.CreateIndex(
                name: "IX_OperationalResults_ExchangeCenterCode",
                table: "OperationalResults",
                column: "ExchangeCenterCode");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OperationalResults_EdgeId",
                table: "OperationalResults");

            migrationBuilder.DropIndex(
                name: "IX_OperationalResults_ExchangeCenterCode",
                table: "OperationalResults");

            migrationBuilder.DropColumn(
                name: "EdgeId",
                table: "OperationalResults");

            migrationBuilder.DropColumn(
                name: "ExchangeCenterCode",
                table: "OperationalResults");
        }
    }
}
