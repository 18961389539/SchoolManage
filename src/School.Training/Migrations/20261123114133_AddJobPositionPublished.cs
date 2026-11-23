using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace School.Training.Migrations
{
    /// <inheritdoc />
    public partial class AddJobPositionPublished : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Published",
                table: "JobPositions",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Published",
                table: "JobPositions");
        }
    }
}
