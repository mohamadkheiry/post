using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Post.Edge.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class AddOperationRequestHash : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "RequestHash",
            table: "operation_journal",
            type: "TEXT",
            maxLength: 64,
            nullable: false,
            defaultValue: "");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "RequestHash",
            table: "operation_journal");
    }
}
