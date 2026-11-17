using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MinU.Server.Migrations
{
    /// <inheritdoc />
    public partial class GroupAvatars : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AvatarFileId",
                table: "Chats",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AvatarFileId",
                table: "Chats");
        }
    }
}
