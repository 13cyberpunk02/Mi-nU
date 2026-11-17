using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MinU.Server.Migrations
{
    /// <inheritdoc />
    public partial class VoiceMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DurationSeconds",
                table: "Files",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DurationSeconds",
                table: "Files");
        }
    }
}
