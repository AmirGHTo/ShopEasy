using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopEasy.OrderService.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTraceContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TraceContext",
                table: "SagaStates",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TraceContext",
                table: "OutboxEntries",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TraceContext",
                table: "SagaStates");

            migrationBuilder.DropColumn(
                name: "TraceContext",
                table: "OutboxEntries");
        }
    }
}
