using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReMind.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDataPointsLocationSpatialIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Split from InitialSchema: SQL Server rejects CREATE SPATIAL INDEX inside the
            // default EF transaction. This migration's only schema change is the guarded
            // create below, executed with suppressTransaction so a failure before the history
            // row is recorded can be replayed. Do not add CREATE TABLE or other unguarded DDL.
            migrationBuilder.Sql(
                """
                IF NOT EXISTS (
                    SELECT 1
                    FROM sys.spatial_indexes
                    WHERE name = N'IX_DataPoints_Location')
                BEGIN
                    CREATE SPATIAL INDEX IX_DataPoints_Location
                    ON dbo.DataPoints (Location)
                    USING GEOGRAPHY_AUTO_GRID;
                END
                """,
                suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // DROP of a spatial index is kept outside the default EF transaction as well.
            migrationBuilder.Sql(
                """
                IF EXISTS (
                    SELECT 1
                    FROM sys.spatial_indexes
                    WHERE name = N'IX_DataPoints_Location')
                BEGIN
                    DROP INDEX IX_DataPoints_Location ON dbo.DataPoints;
                END
                """,
                suppressTransaction: true);
        }
    }
}
