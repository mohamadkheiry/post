using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Post.Core.Infrastructure.Persistence.Migrations.OperationalResult
{
    /// <inheritdoc />
    public partial class EnforceOperationalResultProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "CK_OperationalResults_EdgeId_NotEmpty",
                table: "OperationalResults",
                sql: "[EdgeId] <> '00000000-0000-0000-0000-000000000000'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_OperationalResults_ExchangeCenterCode_NotEmpty",
                table: "OperationalResults",
                sql: "[ExchangeCenterCode] <> ''");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_OperationalResults_EdgeId_NotEmpty",
                table: "OperationalResults");

            migrationBuilder.DropCheckConstraint(
                name: "CK_OperationalResults_ExchangeCenterCode_NotEmpty",
                table: "OperationalResults");
        }
    }
}
