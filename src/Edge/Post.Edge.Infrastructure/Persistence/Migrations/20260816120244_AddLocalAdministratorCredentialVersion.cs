using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Post.Edge.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class AddLocalAdministratorCredentialVersion : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "CredentialVersion",
            table: "local_administrators",
            type: "TEXT",
            nullable: false,
            defaultValue: new Guid("7b849004-9444-4c51-953d-1a0ef2202da4"));

        migrationBuilder.AddColumn<Guid>(
            name: "CredentialVersion",
            table: "local_administrator_sessions",
            type: "TEXT",
            nullable: false,
            defaultValue: Guid.Empty);

        // Upgrading the credential version deliberately invalidates every pre-versioned session.
        migrationBuilder.Sql("DELETE FROM local_administrator_sessions;");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "CredentialVersion",
            table: "local_administrators");

        migrationBuilder.DropColumn(
            name: "CredentialVersion",
            table: "local_administrator_sessions");
    }
}
