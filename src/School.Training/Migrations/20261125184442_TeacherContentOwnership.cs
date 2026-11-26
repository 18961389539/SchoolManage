using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace School.Training.Migrations
{
    /// <inheritdoc />
    public partial class TeacherContentOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OwnerUserId",
                table: "ExamQuestionBanks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Visibility",
                table: "ExamQuestionBanks",
                type: "TEXT",
                nullable: false,
                defaultValue: "published");

            migrationBuilder.AddColumn<string>(
                name: "OwnerUserId",
                table: "ExamPapers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Visibility",
                table: "ExamPapers",
                type: "TEXT",
                nullable: false,
                defaultValue: "published");

            migrationBuilder.AddColumn<string>(
                name: "OwnerUserId",
                table: "Courses",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Visibility",
                table: "Courses",
                type: "TEXT",
                nullable: false,
                defaultValue: "published");

            migrationBuilder.CreateIndex(
                name: "IX_ExamQuestionBanks_OwnerUserId",
                table: "ExamQuestionBanks",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ExamQuestionBanks_Visibility",
                table: "ExamQuestionBanks",
                column: "Visibility");

            migrationBuilder.CreateIndex(
                name: "IX_ExamPapers_OwnerUserId",
                table: "ExamPapers",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ExamPapers_Visibility",
                table: "ExamPapers",
                column: "Visibility");

            migrationBuilder.CreateIndex(
                name: "IX_Courses_OwnerUserId",
                table: "Courses",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Courses_Visibility",
                table: "Courses",
                column: "Visibility");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ExamQuestionBanks_OwnerUserId",
                table: "ExamQuestionBanks");

            migrationBuilder.DropIndex(
                name: "IX_ExamQuestionBanks_Visibility",
                table: "ExamQuestionBanks");

            migrationBuilder.DropIndex(
                name: "IX_ExamPapers_OwnerUserId",
                table: "ExamPapers");

            migrationBuilder.DropIndex(
                name: "IX_ExamPapers_Visibility",
                table: "ExamPapers");

            migrationBuilder.DropIndex(
                name: "IX_Courses_OwnerUserId",
                table: "Courses");

            migrationBuilder.DropIndex(
                name: "IX_Courses_Visibility",
                table: "Courses");

            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                table: "ExamQuestionBanks");

            migrationBuilder.DropColumn(
                name: "Visibility",
                table: "ExamQuestionBanks");

            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                table: "ExamPapers");

            migrationBuilder.DropColumn(
                name: "Visibility",
                table: "ExamPapers");

            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                table: "Courses");

            migrationBuilder.DropColumn(
                name: "Visibility",
                table: "Courses");
        }
    }
}
