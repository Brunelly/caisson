using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Caisson.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDesiredStateVersionCandidateFingerprint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "candidate_fingerprint",
                table: "desired_state_version",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_desired_state_version_rack_slug_candidate_fingerprint",
                table: "desired_state_version",
                columns: new[] { "rack_slug", "candidate_fingerprint" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_desired_state_version_rack_slug_candidate_fingerprint",
                table: "desired_state_version");

            migrationBuilder.DropColumn(
                name: "candidate_fingerprint",
                table: "desired_state_version");
        }
    }
}
