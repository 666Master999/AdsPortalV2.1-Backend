using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AdsPortalV2.Migrations
{
    /// <inheritdoc />
    public partial class AddAdSoftDeleteAndModerationStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "Ads",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ModerationStatus",
                table: "Ads",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "Ads");

            migrationBuilder.DropColumn(
                name: "ModerationStatus",
                table: "Ads");
        }
    }
}
