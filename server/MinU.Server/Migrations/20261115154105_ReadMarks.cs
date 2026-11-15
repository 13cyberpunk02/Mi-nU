using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MinU.Server.Migrations
{
    /// <inheritdoc />
    public partial class ReadMarks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "LastReadMessageId",
                table: "ChatMembers",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            // Chats from before read marks count as read, or every old message would show up as unread at once.
            migrationBuilder.Sql("""
                UPDATE "ChatMembers" AS m
                SET "LastReadMessageId" = COALESCE((SELECT MAX(x."Id") FROM "Messages" AS x WHERE x."ChatId" = m."ChatId"), 0);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastReadMessageId",
                table: "ChatMembers");
        }
    }
}
