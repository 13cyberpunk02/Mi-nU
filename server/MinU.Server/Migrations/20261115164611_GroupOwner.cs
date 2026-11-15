using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MinU.Server.Migrations
{
    /// <inheritdoc />
    public partial class GroupOwner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "OwnerId",
                table: "Chats",
                type: "uuid",
                nullable: true);

            // Groups from before owners: whoever wrote first most likely created it, otherwise any member.
            migrationBuilder.Sql("""
                UPDATE "Chats" AS c
                SET "OwnerId" = COALESCE(
                    (SELECT m."SenderId" FROM "Messages" AS m
                     WHERE m."ChatId" = c."Id"
                       AND EXISTS (SELECT 1 FROM "ChatMembers" AS x WHERE x."ChatId" = c."Id" AND x."UserId" = m."SenderId")
                     ORDER BY m."Id" LIMIT 1),
                    (SELECT x."UserId" FROM "ChatMembers" AS x WHERE x."ChatId" = c."Id" LIMIT 1))
                WHERE c."Kind" = 1;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OwnerId",
                table: "Chats");
        }
    }
}
