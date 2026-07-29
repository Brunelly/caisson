using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Caisson.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DesiredStateValidationErrorTimestamp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_desired_state_validation_error_ingestion_run_id",
                table: "desired_state_validation_error");

            migrationBuilder.AddColumn<DateTime>(
                name: "created_at_utc",
                table: "desired_state_validation_error",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.CreateIndex(
                name: "ix_desired_state_validation_error_run_created_id",
                table: "desired_state_validation_error",
                columns: new[] { "ingestion_run_id", "created_at_utc", "id" },
                descending: new[] { false, true, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_desired_state_validation_error_run_created_id",
                table: "desired_state_validation_error");

            migrationBuilder.DropColumn(
                name: "created_at_utc",
                table: "desired_state_validation_error");

            migrationBuilder.CreateIndex(
                name: "ix_desired_state_validation_error_ingestion_run_id",
                table: "desired_state_validation_error",
                column: "ingestion_run_id");
        }
    }
}
