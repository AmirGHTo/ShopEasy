using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShopEasy.InventoryService.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddStockLedgerUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_StockTransactions_OrderId_ProductId_Reason",
                table: "StockTransactions",
                columns: new[] { "OrderId", "ProductId", "Reason" },
                unique: true,
                filter: "\"OrderId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StockTransactions_OrderId_ProductId_Reason",
                table: "StockTransactions");
        }
    }
}
