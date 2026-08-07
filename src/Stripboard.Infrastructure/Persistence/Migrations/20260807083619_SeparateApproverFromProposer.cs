using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stripboard.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SeparateApproverFromProposer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "approved_at",
                table: "schedule_versions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "approved_by",
                table: "schedule_versions",
                type: "character varying(150)",
                maxLength: 150,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "approved_at",
                table: "schedule_versions");

            migrationBuilder.DropColumn(
                name: "approved_by",
                table: "schedule_versions");
        }
    }
}
