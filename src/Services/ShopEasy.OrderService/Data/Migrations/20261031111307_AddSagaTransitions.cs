using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ShopEasy.OrderService.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSagaTransitions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "StatusChangedAt",
                table: "SagaStates",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SagaTransitions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    SagaId = table.Column<Guid>(type: "uuid", nullable: false),
                    FromStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ToStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SecondsInPreviousStatus = table.Column<double>(type: "double precision", nullable: true),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CorrelationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SagaTransitions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SagaTransitions_At",
                table: "SagaTransitions",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_SagaTransitions_OrderId_At",
                table: "SagaTransitions",
                columns: new[] { "OrderId", "At" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SagaTransitions");

            migrationBuilder.DropColumn(
                name: "StatusChangedAt",
                table: "SagaStates");
        }
    }
}
