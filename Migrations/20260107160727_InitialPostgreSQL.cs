using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RamStockAlerts.Migrations
{
    /// <inheritdoc />
    public partial class InitialPostgreSQL : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EventStoreEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AggregateId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    EventType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Payload = table.Column<string>(type: "TEXT", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventStoreEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SignalLifecycles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SignalId = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    OccurredAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SpreadAtEvent = table.Column<decimal>(type: "TEXT", nullable: true),
                    PrintsPerSecond = table.Column<decimal>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SignalLifecycles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TradeSignals",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Ticker = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    Entry = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false),
                    Stop = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false),
                    Target = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false),
                    Score = table.Column<decimal>(type: "TEXT", precision: 5, scale: 2, nullable: false),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExecutionPrice = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: true),
                    ExecutionTime = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ExitPrice = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: true),
                    PnL = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    RejectionReason = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TradeSignals", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EventStoreEntries_AggregateId",
                table: "EventStoreEntries",
                column: "AggregateId");

            migrationBuilder.CreateIndex(
                name: "IX_EventStoreEntries_CorrelationId",
                table: "EventStoreEntries",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_EventStoreEntries_EventType",
                table: "EventStoreEntries",
                column: "EventType");

            migrationBuilder.CreateIndex(
                name: "IX_EventStoreEntries_Timestamp",
                table: "EventStoreEntries",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_SignalLifecycles_OccurredAt",
                table: "SignalLifecycles",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_SignalLifecycles_SignalId",
                table: "SignalLifecycles",
                column: "SignalId");

            migrationBuilder.CreateIndex(
                name: "IX_TradeSignals_Ticker",
                table: "TradeSignals",
                column: "Ticker");

            migrationBuilder.CreateIndex(
                name: "IX_TradeSignals_Timestamp",
                table: "TradeSignals",
                column: "Timestamp");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EventStoreEntries");

            migrationBuilder.DropTable(
                name: "SignalLifecycles");

            migrationBuilder.DropTable(
                name: "TradeSignals");
        }
    }
}
