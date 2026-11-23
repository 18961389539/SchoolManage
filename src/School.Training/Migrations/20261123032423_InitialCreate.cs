using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace School.Training.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Audits",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    At = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UserName = table.Column<string>(type: "TEXT", nullable: false),
                    Action = table.Column<string>(type: "TEXT", nullable: false),
                    Target = table.Column<string>(type: "TEXT", nullable: false),
                    Detail = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Audits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Cabinets",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Host = table.Column<string>(type: "TEXT", nullable: false),
                    Port = table.Column<int>(type: "INTEGER", nullable: false),
                    JumpHost = table.Column<string>(type: "TEXT", nullable: false),
                    JumpPort = table.Column<int>(type: "INTEGER", nullable: false),
                    JumpUserName = table.Column<string>(type: "TEXT", nullable: false),
                    JumpPasswordProtected = table.Column<string>(type: "TEXT", nullable: false),
                    Remark = table.Column<string>(type: "TEXT", nullable: false),
                    IsSimulated = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Cabinets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CoreCourseSeries",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CoreCourseSeries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DeviceTypes",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Newline = table.Column<string>(type: "TEXT", nullable: false),
                    Backspace = table.Column<string>(type: "TEXT", nullable: false),
                    Remark = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceTypes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ExamPapers",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    Remark = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExamPapers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ExamQuestionBanks",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExamQuestionBanks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ImportBatches",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    RowsJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Committed = table.Column<bool>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    RemoveInvalidRows = table.Column<bool>(type: "INTEGER", nullable: false),
                    TotalCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ProcessedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ImportedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    RemovedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    FailureMessage = table.Column<string>(type: "TEXT", nullable: false),
                    QueuedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportBatches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Licenses",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    ImportedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Licenses", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Nodes",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Host = table.Column<string>(type: "TEXT", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", nullable: false),
                    Port = table.Column<int>(type: "INTEGER", nullable: false),
                    UserName = table.Column<string>(type: "TEXT", nullable: false),
                    PasswordProtected = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Remark = table.Column<string>(type: "TEXT", nullable: false),
                    TotalCpu = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalMemoryMb = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalDiskGb = table.Column<int>(type: "INTEGER", nullable: false),
                    IpAddress = table.Column<string>(type: "TEXT", nullable: false),
                    SubnetMask = table.Column<string>(type: "TEXT", nullable: false),
                    Gateway = table.Column<string>(type: "TEXT", nullable: false),
                    SimulatedClockOffsetSeconds = table.Column<long>(type: "INTEGER", nullable: false),
                    AccountQuota = table.Column<int>(type: "INTEGER", nullable: false),
                    VirtualMachineQuota = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Nodes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Professions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Code = table.Column<string>(type: "TEXT", nullable: false),
                    Remark = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Professions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    UserName = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", nullable: false),
                    Role = table.Column<string>(type: "TEXT", nullable: false),
                    SecurityStamp = table.Column<string>(type: "TEXT", nullable: false),
                    MustChangePassword = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Courses",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Remark = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PublishedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    SeriesId = table.Column<string>(type: "TEXT", nullable: true),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Courses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Courses_CoreCourseSeries_SeriesId",
                        column: x => x.SeriesId,
                        principalTable: "CoreCourseSeries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ExamQuestions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    BankId = table.Column<string>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    Stem = table.Column<string>(type: "TEXT", nullable: false),
                    OptionsJson = table.Column<string>(type: "TEXT", nullable: false),
                    AnswerJson = table.Column<string>(type: "TEXT", nullable: false),
                    Analysis = table.Column<string>(type: "TEXT", nullable: false),
                    PracticalNodeId = table.Column<string>(type: "TEXT", nullable: false),
                    PracticalRubricJson = table.Column<string>(type: "TEXT", nullable: false),
                    PracticalResources = table.Column<string>(type: "TEXT", nullable: false),
                    Score = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExamQuestions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExamQuestions_ExamQuestionBanks_BankId",
                        column: x => x.BankId,
                        principalTable: "ExamQuestionBanks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CaseTopologyTemplates",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    NodeId = table.Column<string>(type: "TEXT", nullable: false),
                    BlueprintJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaseTopologyTemplates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CaseTopologyTemplates_Nodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "Nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Devices",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    CabinetId = table.Column<string>(type: "TEXT", nullable: false),
                    DeviceTypeId = table.Column<string>(type: "TEXT", nullable: false),
                    NodeId = table.Column<string>(type: "TEXT", nullable: true),
                    Host = table.Column<string>(type: "TEXT", nullable: false),
                    Port = table.Column<int>(type: "INTEGER", nullable: false),
                    ConnectionType = table.Column<string>(type: "TEXT", nullable: false),
                    SerialPortNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    WebPort = table.Column<int>(type: "INTEGER", nullable: false),
                    WebProtocol = table.Column<string>(type: "TEXT", nullable: false),
                    UserName = table.Column<string>(type: "TEXT", nullable: false),
                    PasswordProtected = table.Column<string>(type: "TEXT", nullable: false),
                    Remark = table.Column<string>(type: "TEXT", nullable: false),
                    ConfigurationJson = table.Column<string>(type: "TEXT", nullable: false),
                    SavedConfigurationJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Devices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Devices_Cabinets_CabinetId",
                        column: x => x.CabinetId,
                        principalTable: "Cabinets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Devices_DeviceTypes_DeviceTypeId",
                        column: x => x.DeviceTypeId,
                        principalTable: "DeviceTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Devices_Nodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "Nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "JobPositions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    DurationMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    StartupNodeId = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobPositions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobPositions_Nodes_StartupNodeId",
                        column: x => x.StartupNodeId,
                        principalTable: "Nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "VirtualMachines",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Uuid = table.Column<string>(type: "TEXT", nullable: false),
                    IpAddress = table.Column<string>(type: "TEXT", nullable: false),
                    NodeId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    CpuCount = table.Column<int>(type: "INTEGER", nullable: false),
                    MemoryMb = table.Column<int>(type: "INTEGER", nullable: false),
                    DiskGb = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Remark = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VirtualMachines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VirtualMachines_Nodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "Nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Classes",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    ProfessionId = table.Column<string>(type: "TEXT", nullable: false),
                    Remark = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Classes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Classes_Professions_ProfessionId",
                        column: x => x.ProfessionId,
                        principalTable: "Professions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "NodeAccountAssignments",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    NodeId = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    AssignedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NodeAccountAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NodeAccountAssignments_Nodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "Nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_NodeAccountAssignments_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TrainingExams",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    PaperId = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    StartAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DurationMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrainingExams", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TrainingExams_ExamPapers_PaperId",
                        column: x => x.PaperId,
                        principalTable: "ExamPapers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TrainingExams_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Classrooms",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    CourseId = table.Column<string>(type: "TEXT", nullable: false),
                    TeacherUserId = table.Column<string>(type: "TEXT", nullable: false),
                    OpenAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CloseAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Classrooms", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Classrooms_Courses_CourseId",
                        column: x => x.CourseId,
                        principalTable: "Courses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Classrooms_Users_TeacherUserId",
                        column: x => x.TeacherUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CourseChapters",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    CourseId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CourseChapters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CourseChapters_Courses_CourseId",
                        column: x => x.CourseId,
                        principalTable: "Courses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ExamPaperQuestions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    PaperId = table.Column<string>(type: "TEXT", nullable: false),
                    QuestionId = table.Column<string>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    Stem = table.Column<string>(type: "TEXT", nullable: false),
                    OptionsJson = table.Column<string>(type: "TEXT", nullable: false),
                    AnswerJson = table.Column<string>(type: "TEXT", nullable: false),
                    Analysis = table.Column<string>(type: "TEXT", nullable: false),
                    PracticalNodeId = table.Column<string>(type: "TEXT", nullable: false),
                    PracticalRubricJson = table.Column<string>(type: "TEXT", nullable: false),
                    PracticalResources = table.Column<string>(type: "TEXT", nullable: false),
                    Score = table.Column<int>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExamPaperQuestions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExamPaperQuestions_ExamPapers_PaperId",
                        column: x => x.PaperId,
                        principalTable: "ExamPapers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ExamPaperQuestions_ExamQuestions_QuestionId",
                        column: x => x.QuestionId,
                        principalTable: "ExamQuestions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "JobSkills",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    PositionId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    SubName = table.Column<string>(type: "TEXT", nullable: false),
                    Ability = table.Column<string>(type: "TEXT", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobSkills", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobSkills_JobPositions_PositionId",
                        column: x => x.PositionId,
                        principalTable: "JobPositions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "JobAttempts",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    PositionId = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    StudentName = table.Column<string>(type: "TEXT", nullable: false),
                    StudentUserName = table.Column<string>(type: "TEXT", nullable: false),
                    StudentNumber = table.Column<string>(type: "TEXT", nullable: false),
                    ClassName = table.Column<string>(type: "TEXT", nullable: false),
                    VirtualMachineId = table.Column<string>(type: "TEXT", nullable: true),
                    EnvironmentJson = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Deadline = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SubmittedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AutoSubmitted = table.Column<bool>(type: "INTEGER", nullable: false),
                    ObjectiveScore = table.Column<int>(type: "INTEGER", nullable: false),
                    PracticalScore = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalScore = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxScore = table.Column<int>(type: "INTEGER", nullable: false),
                    GradedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobAttempts_JobPositions_PositionId",
                        column: x => x.PositionId,
                        principalTable: "JobPositions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_JobAttempts_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_JobAttempts_VirtualMachines_VirtualMachineId",
                        column: x => x.VirtualMachineId,
                        principalTable: "VirtualMachines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "People",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Number = table.Column<string>(type: "TEXT", nullable: false),
                    ClassId = table.Column<string>(type: "TEXT", nullable: true),
                    Gender = table.Column<string>(type: "TEXT", nullable: false),
                    Phone = table.Column<string>(type: "TEXT", nullable: false),
                    Email = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_People", x => x.Id);
                    table.ForeignKey(
                        name: "FK_People_Classes_ClassId",
                        column: x => x.ClassId,
                        principalTable: "Classes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_People_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ExamAttempts",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ExamId = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    StudentName = table.Column<string>(type: "TEXT", nullable: false),
                    StudentUserName = table.Column<string>(type: "TEXT", nullable: false),
                    StudentNumber = table.Column<string>(type: "TEXT", nullable: false),
                    ClassName = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Deadline = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SubmittedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AutoSubmitted = table.Column<bool>(type: "INTEGER", nullable: false),
                    ObjectiveScore = table.Column<int>(type: "INTEGER", nullable: false),
                    PracticalScore = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalScore = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxScore = table.Column<int>(type: "INTEGER", nullable: false),
                    GradedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    GradedByUserId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExamAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExamAttempts_TrainingExams_ExamId",
                        column: x => x.ExamId,
                        principalTable: "TrainingExams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ExamAttempts_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ExamStudents",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ExamId = table.Column<string>(type: "TEXT", nullable: false),
                    StudentUserId = table.Column<string>(type: "TEXT", nullable: false),
                    StudentPersonId = table.Column<string>(type: "TEXT", nullable: false),
                    ClassId = table.Column<string>(type: "TEXT", nullable: true),
                    ClassName = table.Column<string>(type: "TEXT", nullable: false),
                    DeploymentStatus = table.Column<string>(type: "TEXT", nullable: false),
                    DeploymentMessage = table.Column<string>(type: "TEXT", nullable: false),
                    DeployedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AddedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExamStudents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExamStudents_Classes_ClassId",
                        column: x => x.ClassId,
                        principalTable: "Classes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ExamStudents_TrainingExams_ExamId",
                        column: x => x.ExamId,
                        principalTable: "TrainingExams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ExamStudents_Users_StudentUserId",
                        column: x => x.StudentUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClassroomGroups",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ClassroomId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Remark = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassroomGroups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClassroomGroups_Classrooms_ClassroomId",
                        column: x => x.ClassroomId,
                        principalTable: "Classrooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClassroomStudents",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ClassroomId = table.Column<string>(type: "TEXT", nullable: false),
                    StudentUserId = table.Column<string>(type: "TEXT", nullable: false),
                    StudentPersonId = table.Column<string>(type: "TEXT", nullable: false),
                    ClassId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassroomStudents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClassroomStudents_Classrooms_ClassroomId",
                        column: x => x.ClassroomId,
                        principalTable: "Classrooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ClassroomStudents_Users_StudentUserId",
                        column: x => x.StudentUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CourseSections",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ChapterId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    DurationMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CourseSections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CourseSections_CourseChapters_ChapterId",
                        column: x => x.ChapterId,
                        principalTable: "CourseChapters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "JobQuestions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    PositionId = table.Column<string>(type: "TEXT", nullable: false),
                    SkillId = table.Column<string>(type: "TEXT", nullable: true),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    Stem = table.Column<string>(type: "TEXT", nullable: false),
                    OptionsJson = table.Column<string>(type: "TEXT", nullable: false),
                    AnswerJson = table.Column<string>(type: "TEXT", nullable: false),
                    Analysis = table.Column<string>(type: "TEXT", nullable: false),
                    RubricJson = table.Column<string>(type: "TEXT", nullable: false),
                    PracticalRubricJson = table.Column<string>(type: "TEXT", nullable: false),
                    Score = table.Column<int>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobQuestions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobQuestions_JobPositions_PositionId",
                        column: x => x.PositionId,
                        principalTable: "JobPositions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_JobQuestions_JobSkills_SkillId",
                        column: x => x.SkillId,
                        principalTable: "JobSkills",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ExamAnswers",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    AttemptId = table.Column<string>(type: "TEXT", nullable: false),
                    PaperQuestionId = table.Column<string>(type: "TEXT", nullable: false),
                    SelectedJson = table.Column<string>(type: "TEXT", nullable: false),
                    TextAnswer = table.Column<string>(type: "TEXT", nullable: false),
                    ScoreAwarded = table.Column<int>(type: "INTEGER", nullable: true),
                    Comment = table.Column<string>(type: "TEXT", nullable: false),
                    PracticalDetailsJson = table.Column<string>(type: "TEXT", nullable: false),
                    AnsweredAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExamAnswers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExamAnswers_ExamAttempts_AttemptId",
                        column: x => x.AttemptId,
                        principalTable: "ExamAttempts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ExamAnswers_ExamPaperQuestions_PaperQuestionId",
                        column: x => x.PaperQuestionId,
                        principalTable: "ExamPaperQuestions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ExamPracticalEnvironments",
                columns: table => new
                {
                    EnrollmentId = table.Column<string>(type: "TEXT", nullable: false),
                    PaperQuestionId = table.Column<string>(type: "TEXT", nullable: false),
                    BaselineJson = table.Column<string>(type: "TEXT", nullable: false),
                    ConfigurationJson = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    HasWork = table.Column<bool>(type: "INTEGER", nullable: false),
                    DeployedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RestartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    VmUuid = table.Column<string>(type: "TEXT", nullable: true),
                    DeploymentStatus = table.Column<string>(type: "TEXT", nullable: false),
                    DeploymentMessage = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExamPracticalEnvironments", x => new { x.EnrollmentId, x.PaperQuestionId });
                    table.ForeignKey(
                        name: "FK_ExamPracticalEnvironments_ExamPaperQuestions_PaperQuestionId",
                        column: x => x.PaperQuestionId,
                        principalTable: "ExamPaperQuestions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ExamPracticalEnvironments_ExamStudents_EnrollmentId",
                        column: x => x.EnrollmentId,
                        principalTable: "ExamStudents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClassroomGroupMembers",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    GroupId = table.Column<string>(type: "TEXT", nullable: false),
                    StudentUserId = table.Column<string>(type: "TEXT", nullable: false),
                    AddedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassroomGroupMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClassroomGroupMembers_ClassroomGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "ClassroomGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ClassroomGroupMembers_Users_StudentUserId",
                        column: x => x.StudentUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CourseResources",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    SectionId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    DurationMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: false),
                    Topology = table.Column<string>(type: "TEXT", nullable: false),
                    TopologyBlueprintJson = table.Column<string>(type: "TEXT", nullable: false),
                    CaseRubricJson = table.Column<string>(type: "TEXT", nullable: false),
                    StartupNodeId = table.Column<string>(type: "TEXT", nullable: true),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CourseResources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CourseResources_CourseSections_SectionId",
                        column: x => x.SectionId,
                        principalTable: "CourseSections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CourseResources_Nodes_StartupNodeId",
                        column: x => x.StartupNodeId,
                        principalTable: "Nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "JobAnswers",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    AttemptId = table.Column<string>(type: "TEXT", nullable: false),
                    QuestionId = table.Column<string>(type: "TEXT", nullable: false),
                    SelectedJson = table.Column<string>(type: "TEXT", nullable: false),
                    TextAnswer = table.Column<string>(type: "TEXT", nullable: false),
                    PracticalDetailsJson = table.Column<string>(type: "TEXT", nullable: false),
                    ScoreAwarded = table.Column<int>(type: "INTEGER", nullable: false),
                    AnsweredAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobAnswers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobAnswers_JobAttempts_AttemptId",
                        column: x => x.AttemptId,
                        principalTable: "JobAttempts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_JobAnswers_JobQuestions_QuestionId",
                        column: x => x.QuestionId,
                        principalTable: "JobQuestions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClassroomCabinetAssignments",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ClassroomId = table.Column<string>(type: "TEXT", nullable: false),
                    GroupId = table.Column<string>(type: "TEXT", nullable: false),
                    CabinetId = table.Column<string>(type: "TEXT", nullable: false),
                    ResourceId = table.Column<string>(type: "TEXT", nullable: false),
                    AssignedByUserId = table.Column<string>(type: "TEXT", nullable: false),
                    AssignedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassroomCabinetAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClassroomCabinetAssignments_Cabinets_CabinetId",
                        column: x => x.CabinetId,
                        principalTable: "Cabinets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ClassroomCabinetAssignments_ClassroomGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "ClassroomGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ClassroomCabinetAssignments_Classrooms_ClassroomId",
                        column: x => x.ClassroomId,
                        principalTable: "Classrooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ClassroomCabinetAssignments_CourseResources_ResourceId",
                        column: x => x.ResourceId,
                        principalTable: "CourseResources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CourseQuestions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ResourceId = table.Column<string>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    Stem = table.Column<string>(type: "TEXT", nullable: false),
                    OptionsJson = table.Column<string>(type: "TEXT", nullable: false),
                    AnswerJson = table.Column<string>(type: "TEXT", nullable: false),
                    Analysis = table.Column<string>(type: "TEXT", nullable: false),
                    Score = table.Column<int>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CourseQuestions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CourseQuestions_CourseResources_ResourceId",
                        column: x => x.ResourceId,
                        principalTable: "CourseResources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CourseResourceFiles",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ResourceId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    StoredName = table.Column<string>(type: "TEXT", nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    IsPinned = table.Column<bool>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CourseResourceFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CourseResourceFiles_CourseResources_ResourceId",
                        column: x => x.ResourceId,
                        principalTable: "CourseResources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LearningRecords",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ClassroomId = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    ResourceId = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    BestScore = table.Column<int>(type: "INTEGER", nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PracticeStartedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LearningRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LearningRecords_Classrooms_ClassroomId",
                        column: x => x.ClassroomId,
                        principalTable: "Classrooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LearningRecords_CourseResources_ResourceId",
                        column: x => x.ResourceId,
                        principalTable: "CourseResources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LearningRecords_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClassroomCaseSessions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ClassroomId = table.Column<string>(type: "TEXT", nullable: false),
                    LearningRecordId = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    ResourceId = table.Column<string>(type: "TEXT", nullable: false),
                    NodeId = table.Column<string>(type: "TEXT", nullable: true),
                    VirtualMachineId = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RestartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RestartCount = table.Column<int>(type: "INTEGER", nullable: false),
                    WorkId = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    EnvironmentJson = table.Column<string>(type: "TEXT", nullable: false),
                    TopologyBlueprintJson = table.Column<string>(type: "TEXT", nullable: false),
                    RubricJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassroomCaseSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClassroomCaseSessions_Classrooms_ClassroomId",
                        column: x => x.ClassroomId,
                        principalTable: "Classrooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ClassroomCaseSessions_CourseResources_ResourceId",
                        column: x => x.ResourceId,
                        principalTable: "CourseResources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ClassroomCaseSessions_LearningRecords_LearningRecordId",
                        column: x => x.LearningRecordId,
                        principalTable: "LearningRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ClassroomCaseSessions_Nodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "Nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ClassroomCaseSessions_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ClassroomCaseSessions_VirtualMachines_VirtualMachineId",
                        column: x => x.VirtualMachineId,
                        principalTable: "VirtualMachines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "PracticeAttempts",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    LearningRecordId = table.Column<string>(type: "TEXT", nullable: false),
                    AnswersJson = table.Column<string>(type: "TEXT", nullable: false),
                    Score = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxScore = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SubmittedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PracticeAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PracticeAttempts_LearningRecords_LearningRecordId",
                        column: x => x.LearningRecordId,
                        principalTable: "LearningRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CaseSessionEvidences",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", nullable: false),
                    StoredName = table.Column<string>(type: "TEXT", nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    Note = table.Column<string>(type: "TEXT", nullable: false),
                    CapturedByUserId = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaseSessionEvidences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CaseSessionEvidences_ClassroomCaseSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "ClassroomCaseSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClassroomCaseGrades",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", nullable: false),
                    IsFinal = table.Column<bool>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ScoredAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Score = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxScore = table.Column<int>(type: "INTEGER", nullable: false),
                    Percent = table.Column<int>(type: "INTEGER", nullable: false),
                    DetailsJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassroomCaseGrades", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClassroomCaseGrades_ClassroomCaseSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "ClassroomCaseSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Audits_At_Id",
                table: "Audits",
                columns: new[] { "At", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Cabinets_Name",
                table: "Cabinets",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CaseSessionEvidences_SessionId_CreatedAt",
                table: "CaseSessionEvidences",
                columns: new[] { "SessionId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CaseTopologyTemplates_NodeId_Name",
                table: "CaseTopologyTemplates",
                columns: new[] { "NodeId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Classes_ProfessionId_Name",
                table: "Classes",
                columns: new[] { "ProfessionId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClassroomCabinetAssignments_CabinetId",
                table: "ClassroomCabinetAssignments",
                column: "CabinetId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClassroomCabinetAssignments_ClassroomId",
                table: "ClassroomCabinetAssignments",
                column: "ClassroomId");

            migrationBuilder.CreateIndex(
                name: "IX_ClassroomCabinetAssignments_GroupId",
                table: "ClassroomCabinetAssignments",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_ClassroomCabinetAssignments_ResourceId",
                table: "ClassroomCabinetAssignments",
                column: "ResourceId");

            migrationBuilder.CreateIndex(
                name: "IX_ClassroomCaseGrades_SessionId_ScoredAt",
                table: "ClassroomCaseGrades",
                columns: new[] { "SessionId", "ScoredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ClassroomCaseSessions_ClassroomId_Status",
                table: "ClassroomCaseSessions",
                columns: new[] { "ClassroomId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ClassroomCaseSessions_LearningRecordId",
                table: "ClassroomCaseSessions",
                column: "LearningRecordId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClassroomCaseSessions_NodeId",
                table: "ClassroomCaseSessions",
                column: "NodeId");

            migrationBuilder.CreateIndex(
                name: "IX_ClassroomCaseSessions_ResourceId",
                table: "ClassroomCaseSessions",
                column: "ResourceId");

            migrationBuilder.CreateIndex(
                name: "IX_ClassroomCaseSessions_UserId",
                table: "ClassroomCaseSessions",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ClassroomCaseSessions_VirtualMachineId",
                table: "ClassroomCaseSessions",
                column: "VirtualMachineId");

            migrationBuilder.CreateIndex(
                name: "IX_ClassroomGroupMembers_GroupId_StudentUserId",
                table: "ClassroomGroupMembers",
                columns: new[] { "GroupId", "StudentUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClassroomGroupMembers_StudentUserId",
                table: "ClassroomGroupMembers",
                column: "StudentUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ClassroomGroups_ClassroomId_Name",
                table: "ClassroomGroups",
                columns: new[] { "ClassroomId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Classrooms_CourseId",
                table: "Classrooms",
                column: "CourseId");

            migrationBuilder.CreateIndex(
                name: "IX_Classrooms_Status",
                table: "Classrooms",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Classrooms_TeacherUserId",
                table: "Classrooms",
                column: "TeacherUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ClassroomStudents_ClassroomId_StudentUserId",
                table: "ClassroomStudents",
                columns: new[] { "ClassroomId", "StudentUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClassroomStudents_StudentUserId",
                table: "ClassroomStudents",
                column: "StudentUserId");

            migrationBuilder.CreateIndex(
                name: "IX_CoreCourseSeries_Name",
                table: "CoreCourseSeries",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CourseChapters_CourseId_SortOrder",
                table: "CourseChapters",
                columns: new[] { "CourseId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_CourseQuestions_ResourceId_SortOrder",
                table: "CourseQuestions",
                columns: new[] { "ResourceId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_CourseResourceFiles_ResourceId_IsPinned",
                table: "CourseResourceFiles",
                columns: new[] { "ResourceId", "IsPinned" });

            migrationBuilder.CreateIndex(
                name: "IX_CourseResourceFiles_ResourceId_SortOrder",
                table: "CourseResourceFiles",
                columns: new[] { "ResourceId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_CourseResources_SectionId_SortOrder",
                table: "CourseResources",
                columns: new[] { "SectionId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_CourseResources_StartupNodeId",
                table: "CourseResources",
                column: "StartupNodeId");

            migrationBuilder.CreateIndex(
                name: "IX_Courses_Name",
                table: "Courses",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Courses_SeriesId",
                table: "Courses",
                column: "SeriesId");

            migrationBuilder.CreateIndex(
                name: "IX_Courses_Status_Type",
                table: "Courses",
                columns: new[] { "Status", "Type" });

            migrationBuilder.CreateIndex(
                name: "IX_CourseSections_ChapterId_SortOrder",
                table: "CourseSections",
                columns: new[] { "ChapterId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_Devices_CabinetId",
                table: "Devices",
                column: "CabinetId");

            migrationBuilder.CreateIndex(
                name: "IX_Devices_DeviceTypeId",
                table: "Devices",
                column: "DeviceTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_Devices_NodeId",
                table: "Devices",
                column: "NodeId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceTypes_Name",
                table: "DeviceTypes",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExamAnswers_AttemptId_PaperQuestionId",
                table: "ExamAnswers",
                columns: new[] { "AttemptId", "PaperQuestionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExamAnswers_PaperQuestionId",
                table: "ExamAnswers",
                column: "PaperQuestionId");

            migrationBuilder.CreateIndex(
                name: "IX_ExamAttempts_ExamId_UserId",
                table: "ExamAttempts",
                columns: new[] { "ExamId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExamAttempts_Status",
                table: "ExamAttempts",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_ExamAttempts_UserId",
                table: "ExamAttempts",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ExamPaperQuestions_PaperId_QuestionId",
                table: "ExamPaperQuestions",
                columns: new[] { "PaperId", "QuestionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExamPaperQuestions_PaperId_SortOrder",
                table: "ExamPaperQuestions",
                columns: new[] { "PaperId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_ExamPaperQuestions_QuestionId",
                table: "ExamPaperQuestions",
                column: "QuestionId");

            migrationBuilder.CreateIndex(
                name: "IX_ExamPapers_Name",
                table: "ExamPapers",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExamPracticalEnvironments_PaperQuestionId",
                table: "ExamPracticalEnvironments",
                column: "PaperQuestionId");

            migrationBuilder.CreateIndex(
                name: "IX_ExamPracticalEnvironments_VmUuid",
                table: "ExamPracticalEnvironments",
                column: "VmUuid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExamQuestionBanks_Name",
                table: "ExamQuestionBanks",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExamQuestions_BankId_Type",
                table: "ExamQuestions",
                columns: new[] { "BankId", "Type" });

            migrationBuilder.CreateIndex(
                name: "IX_ExamQuestions_CreatedAt",
                table: "ExamQuestions",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ExamQuestions_Type",
                table: "ExamQuestions",
                column: "Type");

            migrationBuilder.CreateIndex(
                name: "IX_ExamStudents_ClassId",
                table: "ExamStudents",
                column: "ClassId");

            migrationBuilder.CreateIndex(
                name: "IX_ExamStudents_ExamId_StudentUserId",
                table: "ExamStudents",
                columns: new[] { "ExamId", "StudentUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExamStudents_StudentUserId",
                table: "ExamStudents",
                column: "StudentUserId");

            migrationBuilder.CreateIndex(
                name: "IX_JobAnswers_AttemptId_QuestionId",
                table: "JobAnswers",
                columns: new[] { "AttemptId", "QuestionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobAnswers_QuestionId",
                table: "JobAnswers",
                column: "QuestionId");

            migrationBuilder.CreateIndex(
                name: "IX_JobAttempts_PositionId_UserId",
                table: "JobAttempts",
                columns: new[] { "PositionId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobAttempts_Status",
                table: "JobAttempts",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_JobAttempts_UserId",
                table: "JobAttempts",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_JobAttempts_VirtualMachineId",
                table: "JobAttempts",
                column: "VirtualMachineId");

            migrationBuilder.CreateIndex(
                name: "IX_JobPositions_Name",
                table: "JobPositions",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobPositions_StartupNodeId",
                table: "JobPositions",
                column: "StartupNodeId");

            migrationBuilder.CreateIndex(
                name: "IX_JobQuestions_PositionId_SortOrder",
                table: "JobQuestions",
                columns: new[] { "PositionId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_JobQuestions_SkillId",
                table: "JobQuestions",
                column: "SkillId");

            migrationBuilder.CreateIndex(
                name: "IX_JobSkills_PositionId_SortOrder",
                table: "JobSkills",
                columns: new[] { "PositionId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_LearningRecords_ClassroomId_UserId_ResourceId",
                table: "LearningRecords",
                columns: new[] { "ClassroomId", "UserId", "ResourceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LearningRecords_ResourceId",
                table: "LearningRecords",
                column: "ResourceId");

            migrationBuilder.CreateIndex(
                name: "IX_LearningRecords_UserId",
                table: "LearningRecords",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_NodeAccountAssignments_NodeId_UserId",
                table: "NodeAccountAssignments",
                columns: new[] { "NodeId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NodeAccountAssignments_UserId",
                table: "NodeAccountAssignments",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Nodes_Name",
                table: "Nodes",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_People_ClassId",
                table: "People",
                column: "ClassId");

            migrationBuilder.CreateIndex(
                name: "IX_People_Kind_Number",
                table: "People",
                columns: new[] { "Kind", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_People_UserId",
                table: "People",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PracticeAttempts_LearningRecordId_SubmittedAt",
                table: "PracticeAttempts",
                columns: new[] { "LearningRecordId", "SubmittedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Professions_Code",
                table: "Professions",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Exams_CreatedByUserId",
                table: "TrainingExams",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Exams_Name",
                table: "TrainingExams",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Exams_PaperId",
                table: "TrainingExams",
                column: "PaperId");

            migrationBuilder.CreateIndex(
                name: "IX_Exams_Status_StartAt",
                table: "TrainingExams",
                columns: new[] { "Status", "StartAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Users_UserName",
                table: "Users",
                column: "UserName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VirtualMachines_IpAddress",
                table: "VirtualMachines",
                column: "IpAddress",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VirtualMachines_NodeId",
                table: "VirtualMachines",
                column: "NodeId");

            migrationBuilder.CreateIndex(
                name: "IX_VirtualMachines_Uuid",
                table: "VirtualMachines",
                column: "Uuid",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Audits");

            migrationBuilder.DropTable(
                name: "CaseSessionEvidences");

            migrationBuilder.DropTable(
                name: "CaseTopologyTemplates");

            migrationBuilder.DropTable(
                name: "ClassroomCabinetAssignments");

            migrationBuilder.DropTable(
                name: "ClassroomCaseGrades");

            migrationBuilder.DropTable(
                name: "ClassroomGroupMembers");

            migrationBuilder.DropTable(
                name: "ClassroomStudents");

            migrationBuilder.DropTable(
                name: "CourseQuestions");

            migrationBuilder.DropTable(
                name: "CourseResourceFiles");

            migrationBuilder.DropTable(
                name: "Devices");

            migrationBuilder.DropTable(
                name: "ExamAnswers");

            migrationBuilder.DropTable(
                name: "ExamPracticalEnvironments");

            migrationBuilder.DropTable(
                name: "ImportBatches");

            migrationBuilder.DropTable(
                name: "JobAnswers");

            migrationBuilder.DropTable(
                name: "Licenses");

            migrationBuilder.DropTable(
                name: "NodeAccountAssignments");

            migrationBuilder.DropTable(
                name: "People");

            migrationBuilder.DropTable(
                name: "PracticeAttempts");

            migrationBuilder.DropTable(
                name: "Settings");

            migrationBuilder.DropTable(
                name: "ClassroomCaseSessions");

            migrationBuilder.DropTable(
                name: "ClassroomGroups");

            migrationBuilder.DropTable(
                name: "Cabinets");

            migrationBuilder.DropTable(
                name: "DeviceTypes");

            migrationBuilder.DropTable(
                name: "ExamAttempts");

            migrationBuilder.DropTable(
                name: "ExamPaperQuestions");

            migrationBuilder.DropTable(
                name: "ExamStudents");

            migrationBuilder.DropTable(
                name: "JobAttempts");

            migrationBuilder.DropTable(
                name: "JobQuestions");

            migrationBuilder.DropTable(
                name: "LearningRecords");

            migrationBuilder.DropTable(
                name: "ExamQuestions");

            migrationBuilder.DropTable(
                name: "Classes");

            migrationBuilder.DropTable(
                name: "TrainingExams");

            migrationBuilder.DropTable(
                name: "VirtualMachines");

            migrationBuilder.DropTable(
                name: "JobSkills");

            migrationBuilder.DropTable(
                name: "Classrooms");

            migrationBuilder.DropTable(
                name: "CourseResources");

            migrationBuilder.DropTable(
                name: "ExamQuestionBanks");

            migrationBuilder.DropTable(
                name: "Professions");

            migrationBuilder.DropTable(
                name: "ExamPapers");

            migrationBuilder.DropTable(
                name: "JobPositions");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "CourseSections");

            migrationBuilder.DropTable(
                name: "Nodes");

            migrationBuilder.DropTable(
                name: "CourseChapters");

            migrationBuilder.DropTable(
                name: "Courses");

            migrationBuilder.DropTable(
                name: "CoreCourseSeries");
        }
    }
}
