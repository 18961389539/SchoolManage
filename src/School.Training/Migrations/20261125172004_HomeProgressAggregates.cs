using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace School.Training.Migrations
{
    /// <inheritdoc />
    public partial class HomeProgressAggregates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClassroomProgressFreezes",
                columns: table => new
                {
                    ClassroomId = table.Column<string>(type: "TEXT", nullable: false),
                    ResourceCount = table.Column<int>(type: "INTEGER", nullable: false),
                    VerifiableCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ResourcesJson = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "[]"),
                    FrozenAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassroomProgressFreezes", x => x.ClassroomId);
                    table.ForeignKey(
                        name: "FK_ClassroomProgressFreezes_Classrooms_ClassroomId",
                        column: x => x.ClassroomId,
                        principalTable: "Classrooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClassroomStudentStats",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ClassroomId = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    LearnedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    PassedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    RecordCount = table.Column<int>(type: "INTEGER", nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    GradeCount = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceStamp = table.Column<long>(type: "INTEGER", nullable: false),
                    LastActivityAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ActiveDates = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassroomStudentStats", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClassroomStudentStats_Classrooms_ClassroomId",
                        column: x => x.ClassroomId,
                        principalTable: "Classrooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ClassroomStudentStats_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PracticeAttempts_StartedAt",
                table: "PracticeAttempts",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_PracticeAttempts_SubmittedAt",
                table: "PracticeAttempts",
                column: "SubmittedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ClassroomCaseGrades_IsFinal_ScoredAt",
                table: "ClassroomCaseGrades",
                columns: new[] { "IsFinal", "ScoredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ClassroomStudentStats_ClassroomId_UserId",
                table: "ClassroomStudentStats",
                columns: new[] { "ClassroomId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClassroomStudentStats_UserId",
                table: "ClassroomStudentStats",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClassroomProgressFreezes");

            migrationBuilder.DropTable(
                name: "ClassroomStudentStats");

            migrationBuilder.DropIndex(
                name: "IX_PracticeAttempts_StartedAt",
                table: "PracticeAttempts");

            migrationBuilder.DropIndex(
                name: "IX_PracticeAttempts_SubmittedAt",
                table: "PracticeAttempts");

            migrationBuilder.DropIndex(
                name: "IX_ClassroomCaseGrades_IsFinal_ScoredAt",
                table: "ClassroomCaseGrades");
        }
    }
}
