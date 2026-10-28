using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopEasy.OrderService.Data.Migrations
{
    /// <inheritdoc />
    public partial class OrdersUpdatedAtIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Orders_UpdatedAt",
                table: "Orders",
                column: "UpdatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Orders_UpdatedAt",
                table: "Orders");
        }
    }
}
