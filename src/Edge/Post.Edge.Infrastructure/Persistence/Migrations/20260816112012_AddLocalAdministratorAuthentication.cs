using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Post.Edge.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class AddLocalAdministratorAuthentication : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
                name: "local_administrator_sessions",
                columns: table => new
                {
                    TokenHash = table.Column<string>(type: "TEXT", nullable: false),
                    UserName = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_local_administrator_sessions", x => x.TokenHash);
                });

        migrationBuilder.CreateTable(
                name: "local_administrators",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserName = table.Column<string>(type: "TEXT", nullable: false),
                    NormalizedUserName = table.Column<string>(type: "TEXT", nullable: false),
                    SecretAlgorithm = table.Column<string>(type: "TEXT", nullable: false),
                    SecretIterations = table.Column<int>(type: "INTEGER", nullable: false),
                    SecretSalt = table.Column<string>(type: "TEXT", nullable: false),
                    SecretHash = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_local_administrators", x => x.Id);
                });

        migrationBuilder.CreateIndex(
                name: "IX_local_administrator_sessions_ExpiresAt",
                table: "local_administrator_sessions",
                column: "ExpiresAt");

        migrationBuilder.CreateIndex(
                name: "IX_local_administrators_NormalizedUserName",
                table: "local_administrators",
                column: "NormalizedUserName",
                unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
                name: "local_administrator_sessions");

        migrationBuilder.DropTable(
                name: "local_administrators");
    }
}
