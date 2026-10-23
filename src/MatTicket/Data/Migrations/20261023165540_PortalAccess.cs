using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatTicket.Data.Migrations
{
    /// <inheritdoc />
    public partial class PortalAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EntraGroupsJson",
                table: "User",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "Access",
                table: "TicketForm",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "Public");

            migrationBuilder.AddColumn<string>(
                name: "AccessGroupsJson",
                table: "TicketForm",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "AccountData",
                table: "TicketForm",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "Requesters");

            migrationBuilder.AddColumn<long>(
                name: "RequesterUserId",
                table: "Ticket",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AccessGroupsJson",
                table: "Category",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.CreateIndex(
                name: "IX_Ticket_RequesterUserId",
                table: "Ticket",
                column: "RequesterUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_Ticket_User_RequesterUserId",
                table: "Ticket",
                column: "RequesterUserId",
                principalTable: "User",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Ticket_User_RequesterUserId",
                table: "Ticket");

            migrationBuilder.DropIndex(
                name: "IX_Ticket_RequesterUserId",
                table: "Ticket");

            migrationBuilder.DropColumn(
                name: "EntraGroupsJson",
                table: "User");

            migrationBuilder.DropColumn(
                name: "Access",
                table: "TicketForm");

            migrationBuilder.DropColumn(
                name: "AccessGroupsJson",
                table: "TicketForm");

            migrationBuilder.DropColumn(
                name: "AccountData",
                table: "TicketForm");

            migrationBuilder.DropColumn(
                name: "RequesterUserId",
                table: "Ticket");

            migrationBuilder.DropColumn(
                name: "AccessGroupsJson",
                table: "Category");
        }
    }
}
