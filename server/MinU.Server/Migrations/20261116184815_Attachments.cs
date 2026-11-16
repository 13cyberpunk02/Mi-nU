using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MinU.Server.Migrations
{
    /// <inheritdoc />
    public partial class Attachments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Height",
                table: "Files",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "MessageId",
                table: "Files",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Width",
                table: "Files",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Files_MessageId",
                table: "Files",
                column: "MessageId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Files_MessageId",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "Height",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "MessageId",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "Width",
                table: "Files");
        }
    }
}
