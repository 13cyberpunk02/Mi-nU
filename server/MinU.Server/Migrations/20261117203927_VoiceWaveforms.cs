using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MinU.Server.Migrations
{
    /// <inheritdoc />
    public partial class VoiceWaveforms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Waveform",
                table: "Files",
                type: "character varying(88)",
                maxLength: 88,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Waveform",
                table: "Files");
        }
    }
}
