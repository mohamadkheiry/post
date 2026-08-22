using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Post.Edge.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class AddExternalOperationSagas : Migration
{
    private static readonly string[] SagaIndexColumns = ["Finalized", "UpdatedAt"];

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "external_operation_sagas",
            columns: table => new
            {
                OperationId = table.Column<Guid>(type: "TEXT", nullable: false),
                OperationType = table.Column<string>(type: "TEXT", nullable: false),
                RequestHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                IntentJson = table.Column<string>(type: "TEXT", nullable: false),
                ReceiptJson = table.Column<string>(type: "TEXT", nullable: true),
                Finalized = table.Column<bool>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_external_operation_sagas", x => new { x.OperationId, x.OperationType });
            });

        migrationBuilder.CreateIndex(
            name: "IX_external_operation_sagas_Finalized_UpdatedAt",
            table: "external_operation_sagas",
            columns: SagaIndexColumns);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "external_operation_sagas");
    }
}
