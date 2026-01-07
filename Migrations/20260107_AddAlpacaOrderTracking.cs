using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RamStockAlerts.Migrations
{
    /// <inheritdoc />
    public partial class AddAlpacaOrderTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OrderId",
                table: "TradeSignals",
                type: "TEXT",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "OrderPlacedAt",
                table: "TradeSignals",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AutoTradingAttempted",
                table: "TradeSignals",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "AutoTradingSkipReason",
                table: "TradeSignals",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TradeSignals_OrderId",
                table: "TradeSignals",
                column: "OrderId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TradeSignals_OrderId",
                table: "TradeSignals");

            migrationBuilder.DropColumn(
                name: "OrderId",
                table: "TradeSignals");

            migrationBuilder.DropColumn(
                name: "OrderPlacedAt",
                table: "TradeSignals");

            migrationBuilder.DropColumn(
                name: "AutoTradingAttempted",
                table: "TradeSignals");

            migrationBuilder.DropColumn(
                name: "AutoTradingSkipReason",
                table: "TradeSignals");
        }
    }
}
