using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Metrica.Ingestion.Migrations
{
    /// <inheritdoc />
    public partial class InitialIngestionSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "ingestion");

            migrationBuilder.CreateTable(
                name: "outbox",
                schema: "ingestion",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject = table.Column<string>(type: "text", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    payload = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    last_error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "uploads",
                schema: "ingestion",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    external_id = table.Column<string>(type: "text", nullable: false),
                    file_name = table.Column<string>(type: "text", nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_uploads", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_unpublished",
                schema: "ingestion",
                table: "outbox",
                column: "created_at",
                filter: "\"published_at\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_uploads_user_source_external",
                schema: "ingestion",
                table: "uploads",
                columns: new[] { "user_id", "source", "external_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outbox",
                schema: "ingestion");

            migrationBuilder.DropTable(
                name: "uploads",
                schema: "ingestion");
        }
    }
}
