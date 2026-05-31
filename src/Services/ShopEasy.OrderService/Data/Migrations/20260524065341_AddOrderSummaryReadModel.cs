using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopEasy.OrderService.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderSummaryReadModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OrderSummaries",
                columns: table => new
                {
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    UserEmail = table.Column<string>(type: "text", nullable: false),
                    StatusName = table.Column<string>(type: "text", nullable: false),
                    StatusCode = table.Column<int>(type: "integer", nullable: false),
                    TotalAmount = table.Column<decimal>(type: "numeric", nullable: false),
                    AmountDisplay = table.Column<string>(type: "text", nullable: false),
                    ItemCount = table.Column<int>(type: "integer", nullable: false),
                    ItemsSummary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    PaymentStatus = table.Column<string>(type: "text", nullable: true),
                    FailureReason = table.Column<string>(type: "text", nullable: true),
                    PlacedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderSummaries", x => x.OrderId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OrderSummaries_PlacedAt",
                table: "OrderSummaries",
                column: "PlacedAt");

            migrationBuilder.CreateIndex(
                name: "IX_OrderSummaries_StatusCode",
                table: "OrderSummaries",
                column: "StatusCode");

            migrationBuilder.CreateIndex(
                name: "IX_OrderSummaries_UserId",
                table: "OrderSummaries",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OrderSummaries");
        }
    }
}
