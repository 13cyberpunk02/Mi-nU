using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MinU.Server.Migrations
{
    /// <inheritdoc />
    public partial class CallRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CallSeconds",
                table: "Messages",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CallVideo",
                table: "Messages",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "Messages",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CallSeconds",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "CallVideo",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "Messages");
        }
    }
}
