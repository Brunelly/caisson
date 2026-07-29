using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Caisson.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDesiredStateRevisionMetadata : Migration
    {
        /// <summary>
        /// Sentinel default for <c>desired_state_json</c> on any pre-#63 row already in the table: an
        /// empty-but-valid JSON object, never an empty string (which is not valid <c>jsonb</c>). Every
        /// row inserted by the ingestion pipeline from here on always supplies a real payload — this
        /// default only exists to make the column additive against a table that may already hold dev
        /// rows from story #62.
        /// </summary>
        private const string PreStory63PayloadSentinel = "{}";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "author_email",
                table: "desired_state_version",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "author_name",
                table: "desired_state_version",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "author_when_utc",
                table: "desired_state_version",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "desired_state_json",
                table: "desired_state_version",
                type: "jsonb",
                maxLength: 2097152,
                nullable: false,
                defaultValue: PreStory63PayloadSentinel);

            migrationBuilder.AddColumn<string>(
                name: "ingested_by",
                table: "desired_state_version",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "pre-story-63");

            migrationBuilder.AddColumn<int>(
                name: "schema_version",
                table: "desired_state_version",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateIndex(
                name: "ix_desired_state_version_rack_slug_commit_sha",
                table: "desired_state_version",
                columns: new[] { "rack_slug", "commit_sha" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_desired_state_version_rack_slug_commit_sha",
                table: "desired_state_version");

            migrationBuilder.DropColumn(
                name: "author_email",
                table: "desired_state_version");

            migrationBuilder.DropColumn(
                name: "author_name",
                table: "desired_state_version");

            migrationBuilder.DropColumn(
                name: "author_when_utc",
                table: "desired_state_version");

            migrationBuilder.DropColumn(
                name: "desired_state_json",
                table: "desired_state_version");

            migrationBuilder.DropColumn(
                name: "ingested_by",
                table: "desired_state_version");

            migrationBuilder.DropColumn(
                name: "schema_version",
                table: "desired_state_version");
        }
    }
}
