using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Metrica.Activities.Migrations
{
    /// <inheritdoc />
    public partial class InitialActivitiesSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "activities");

            migrationBuilder.CreateTable(
                name: "activities",
                schema: "activities",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    external_id = table.Column<string>(type: "text", nullable: false),
                    sport = table.Column<string>(type: "text", nullable: false),
                    start_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    elapsed_seconds = table.Column<double>(type: "double precision", nullable: false),
                    moving_seconds = table.Column<double>(type: "double precision", nullable: false),
                    distance_meters = table.Column<double>(type: "double precision", nullable: false),
                    elevation_gain_meters = table.Column<double>(type: "double precision", nullable: true),
                    avg_heart_rate = table.Column<double>(type: "double precision", nullable: true),
                    max_heart_rate = table.Column<double>(type: "double precision", nullable: true),
                    avg_power_watts = table.Column<double>(type: "double precision", nullable: true),
                    avg_cadence = table.Column<double>(type: "double precision", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_activities", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "activity_stream",
                schema: "activities",
                columns: table => new
                {
                    activity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    latitude = table.Column<double>(type: "double precision", nullable: true),
                    longitude = table.Column<double>(type: "double precision", nullable: true),
                    altitude_meters = table.Column<double>(type: "double precision", nullable: true),
                    heart_rate = table.Column<double>(type: "double precision", nullable: true),
                    power_watts = table.Column<double>(type: "double precision", nullable: true),
                    cadence = table.Column<double>(type: "double precision", nullable: true),
                    speed_mps = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                });

            migrationBuilder.CreateTable(
                name: "inbox",
                schema: "activities",
                columns: table => new
                {
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    consumer = table.Column<string>(type: "text", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inbox", x => new { x.consumer, x.message_id });
                });

            migrationBuilder.CreateTable(
                name: "activity_laps",
                schema: "activities",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    activity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    start_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    elapsed_seconds = table.Column<double>(type: "double precision", nullable: false),
                    distance_meters = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_activity_laps", x => x.id);
                    table.ForeignKey(
                        name: "fk_activity_laps_activities_activity_id",
                        column: x => x.activity_id,
                        principalSchema: "activities",
                        principalTable: "activities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_activities_user_start",
                schema: "activities",
                table: "activities",
                columns: new[] { "user_id", "start_time" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ux_activities_user_source_external",
                schema: "activities",
                table: "activities",
                columns: new[] { "user_id", "source", "external_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_activity_laps_activity_id",
                schema: "activities",
                table: "activity_laps",
                column: "activity_id");

            migrationBuilder.CreateIndex(
                name: "ix_activity_stream_activity_time",
                schema: "activities",
                table: "activity_stream",
                columns: new[] { "activity_id", "time" });

            // Passo DDL pianificato (T11, DEC-003/DEC-014), non generato da EF: activity_stream diventa una hypertable
            // TimescaleDB partizionata sulla colonna "time". Funziona perche' lo script di init concede al ruolo USAGE su
            // "public" (dove risiede l'estensione) e ne imposta il search_path. Va DOPO la creazione di tabella e indici;
            // se la migrazione viene rigenerata, questo passo va riaggiunto a mano.
            migrationBuilder.Sql("SELECT create_hypertable('activities.activity_stream', 'time');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "activity_laps",
                schema: "activities");

            migrationBuilder.DropTable(
                name: "activity_stream",
                schema: "activities");

            migrationBuilder.DropTable(
                name: "inbox",
                schema: "activities");

            migrationBuilder.DropTable(
                name: "activities",
                schema: "activities");
        }
    }
}
