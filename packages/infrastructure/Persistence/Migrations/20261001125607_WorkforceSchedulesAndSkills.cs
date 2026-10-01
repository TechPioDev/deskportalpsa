using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Desk.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WorkforceSchedulesAndSkills : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsSchedulable",
                table: "app_users",
                type: "boolean",
                nullable: false,
                defaultValue: true); // existing staff stay offered for planned work; the EF bool default would have switched everyone off

            migrationBuilder.CreateTable(
                name: "skills",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    MspOrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_skills", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "work_schedules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AppUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    TimeZone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    MspOrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_work_schedules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_work_schedules_app_users_AppUserId",
                        column: x => x.AppUserId,
                        principalTable: "app_users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "staff_skills",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AppUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SkillId = table.Column<Guid>(type: "uuid", nullable: false),
                    Level = table.Column<int>(type: "integer", nullable: false),
                    AssignedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    MspOrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_staff_skills", x => x.Id);
                    table.ForeignKey(
                        name: "FK_staff_skills_app_users_AppUserId",
                        column: x => x.AppUserId,
                        principalTable: "app_users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_staff_skills_skills_SkillId",
                        column: x => x.SkillId,
                        principalTable: "skills",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "work_schedule_days",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkScheduleId = table.Column<Guid>(type: "uuid", nullable: false),
                    Day = table.Column<int>(type: "integer", nullable: false),
                    Start = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    End = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    MspOrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_work_schedule_days", x => x.Id);
                    table.ForeignKey(
                        name: "FK_work_schedule_days_work_schedules_WorkScheduleId",
                        column: x => x.WorkScheduleId,
                        principalTable: "work_schedules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "work_schedule_breaks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkScheduleDayId = table.Column<Guid>(type: "uuid", nullable: false),
                    Start = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    End = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    MspOrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_work_schedule_breaks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_work_schedule_breaks_work_schedule_days_WorkScheduleDayId",
                        column: x => x.WorkScheduleDayId,
                        principalTable: "work_schedule_days",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_skills_MspOrganizationId_NormalizedName",
                table: "skills",
                columns: new[] { "MspOrganizationId", "NormalizedName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_staff_skills_AppUserId_SkillId",
                table: "staff_skills",
                columns: new[] { "AppUserId", "SkillId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_staff_skills_SkillId",
                table: "staff_skills",
                column: "SkillId");

            migrationBuilder.CreateIndex(
                name: "IX_work_schedule_breaks_WorkScheduleDayId",
                table: "work_schedule_breaks",
                column: "WorkScheduleDayId");

            migrationBuilder.CreateIndex(
                name: "IX_work_schedule_days_WorkScheduleId_Day",
                table: "work_schedule_days",
                columns: new[] { "WorkScheduleId", "Day" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_work_schedules_AppUserId_EffectiveFrom",
                table: "work_schedules",
                columns: new[] { "AppUserId", "EffectiveFrom" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_work_schedules_MspOrganizationId",
                table: "work_schedules",
                column: "MspOrganizationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "staff_skills");

            migrationBuilder.DropTable(
                name: "work_schedule_breaks");

            migrationBuilder.DropTable(
                name: "skills");

            migrationBuilder.DropTable(
                name: "work_schedule_days");

            migrationBuilder.DropTable(
                name: "work_schedules");

            migrationBuilder.DropColumn(
                name: "IsSchedulable",
                table: "app_users");
        }
    }
}
