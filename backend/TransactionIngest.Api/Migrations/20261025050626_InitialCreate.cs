using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TransactionIngest.Api.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Transactions",
                columns: table => new
                {
                    SourceSystem = table.Column<string>(type: "TEXT", maxLength: 19, nullable: false),
                    TransactionId = table.Column<int>(type: "INTEGER", nullable: false),
                    CardNumber = table.Column<string>(type: "TEXT", maxLength: 19, nullable: false),
                    LocationCode = table.Column<string>(type: "TEXT", maxLength: 19, nullable: false),
                    ProductName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Amount = table.Column<long>(type: "INTEGER", nullable: false),
                    TransactionTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Transactions", x => new { x.SourceSystem, x.TransactionId });
                    table.CheckConstraint("CK_Transactions_Amount_Cents", "typeof(\"Amount\") = 'integer' AND \"Amount\" BETWEEN -999999999999999999 AND 999999999999999999");
                    table.CheckConstraint("CK_Transactions_CardNumber_Length", "length(\"CardNumber\") <= 19");
                    table.CheckConstraint("CK_Transactions_LocationCode_Length", "length(\"LocationCode\") BETWEEN 1 AND 19");
                    table.CheckConstraint("CK_Transactions_ProductName_Length", "length(\"ProductName\") BETWEEN 1 AND 100");
                    table.CheckConstraint("CK_Transactions_SourceSystem_Length", "length(\"SourceSystem\") BETWEEN 1 AND 19");
                    table.CheckConstraint("CK_Transactions_Status", "\"Status\" IN ('Active', 'Revoked')");
                });

            migrationBuilder.CreateTable(
                name: "TransactionAudits",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceSystem = table.Column<string>(type: "TEXT", maxLength: 19, nullable: false),
                    TransactionId = table.Column<int>(type: "INTEGER", nullable: false),
                    Action = table.Column<string>(type: "TEXT", nullable: false),
                    FieldName = table.Column<string>(type: "TEXT", nullable: true),
                    OldValue = table.Column<string>(type: "TEXT", nullable: true),
                    NewValue = table.Column<string>(type: "TEXT", nullable: true),
                    ChangedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TransactionAudits", x => x.Id);
                    table.CheckConstraint("CK_TransactionAudits_Action", "\"Action\" IN ('Created', 'Updated', 'Revoked', 'Reactivated')");
                    table.ForeignKey(
                        name: "FK_TransactionAudits_Transactions_SourceSystem_TransactionId",
                        columns: x => new { x.SourceSystem, x.TransactionId },
                        principalTable: "Transactions",
                        principalColumns: new[] { "SourceSystem", "TransactionId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TransactionAudits_SourceSystem_TransactionId_ChangedAtUtc",
                table: "TransactionAudits",
                columns: new[] { "SourceSystem", "TransactionId", "ChangedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TransactionAudits");

            migrationBuilder.DropTable(
                name: "Transactions");
        }
    }
}
