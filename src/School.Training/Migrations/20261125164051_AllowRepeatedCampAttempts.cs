using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace School.Training.Migrations
{
    /// <inheritdoc />
    public partial class AllowRepeatedCampAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_JobAttempts_PositionId_UserId",
                table: "JobAttempts");

            migrationBuilder.AddColumn<int>(
                name: "ContentVersion",
                table: "JobSkills",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "ContentVersion",
                table: "JobQuestions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "ContentVersion",
                table: "JobPositions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "ContentVersion",
                table: "JobAttempts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "StartupNodeId",
                table: "JobAttempts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE "JobAttempts"
                SET "StartupNodeId" = (
                    SELECT "StartupNodeId" FROM "JobPositions"
                    WHERE "JobPositions"."Id" = "JobAttempts"."PositionId"
                );
                """);

            migrationBuilder.CreateIndex(
                name: "IX_JobAttempts_PositionId_UserId",
                table: "JobAttempts",
                columns: new[] { "PositionId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_JobAttempts_PositionId_UserId_Active",
                table: "JobAttempts",
                columns: new[] { "PositionId", "UserId" },
                unique: true,
                filter: "\"Status\" = 'inProgress'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_JobAttempts_PositionId_UserId",
                table: "JobAttempts");

            migrationBuilder.DropIndex(
                name: "IX_JobAttempts_PositionId_UserId_Active",
                table: "JobAttempts");

            migrationBuilder.DropColumn(
                name: "ContentVersion",
                table: "JobSkills");

            migrationBuilder.DropColumn(
                name: "ContentVersion",
                table: "JobQuestions");

            migrationBuilder.DropColumn(
                name: "ContentVersion",
                table: "JobPositions");

            migrationBuilder.DropColumn(
                name: "ContentVersion",
                table: "JobAttempts");

            migrationBuilder.DropColumn(
                name: "StartupNodeId",
                table: "JobAttempts");

            migrationBuilder.CreateIndex(
                name: "IX_JobAttempts_PositionId_UserId",
                table: "JobAttempts",
                columns: new[] { "PositionId", "UserId" },
                unique: true);
        }
    }
}
