using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopEasy.OrderService.Data.Migrations
{
    // Stores the correlation id of the request that placed an order on its outbox entry and on its
    // saga, so the background workers that publish later (outbox delivery, saga orchestrator,
    // timeout service) can put it back on the messages they send. Nullable and additive: rows
    // written before this migration simply have no id.
    /// <inheritdoc />
    public partial class AddCorrelationIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CorrelationId",
                table: "SagaStates",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CorrelationId",
                table: "OutboxEntries",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CorrelationId",
                table: "SagaStates");

            migrationBuilder.DropColumn(
                name: "CorrelationId",
                table: "OutboxEntries");
        }
    }
}
