using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SongServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialMigration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sources",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    path = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sources", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "state",
                columns: table => new
                {
                    key = table.Column<string>(type: "TEXT", nullable: false),
                    value = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_state", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "stream_metadata",
                columns: table => new
                {
                    rowid = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    metadata = table.Column<string>(type: "TEXT", nullable: false),
                    artist = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "json_extract(metadata, '$.artist')", stored: false),
                    album = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "json_extract(metadata, '$.album')", stored: false),
                    title = table.Column<string>(type: "TEXT", nullable: true, computedColumnSql: "json_extract(metadata, '$.title')", stored: false),
                    start_time = table.Column<long>(type: "INTEGER", nullable: false, computedColumnSql: "json_extract(metadata, '$.startTime')", stored: false),
                    end_time = table.Column<long>(type: "INTEGER", nullable: true, computedColumnSql: "json_extract(metadata, '$.endTime')", stored: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stream_metadata", x => x.rowid);
                });

            migrationBuilder.CreateTable(
                name: "songs",
                columns: table => new
                {
                    path = table.Column<string>(type: "TEXT", nullable: false),
                    source_id = table.Column<long>(type: "INTEGER", nullable: false),
                    title = table.Column<string>(type: "TEXT", nullable: false),
                    track_number = table.Column<int>(type: "INTEGER", nullable: true),
                    track_total = table.Column<int>(type: "INTEGER", nullable: true),
                    disc_number = table.Column<int>(type: "INTEGER", nullable: true),
                    disc_total = table.Column<int>(type: "INTEGER", nullable: true),
                    duration = table.Column<TimeSpan>(type: "TEXT", nullable: false),
                    last_modified = table.Column<DateTime>(type: "TEXT", nullable: false),
                    file_size = table.Column<long>(type: "INTEGER", nullable: false),
                    hash = table.Column<string>(type: "TEXT", nullable: false),
                    album = table.Column<string>(type: "TEXT", nullable: true),
                    album_sort = table.Column<string>(type: "TEXT", nullable: true),
                    album_artists = table.Column<string>(type: "TEXT", nullable: true),
                    album_artists_sort = table.Column<string>(type: "TEXT", nullable: true),
                    performers = table.Column<string>(type: "TEXT", nullable: true),
                    performers_sort = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_songs", x => new { x.source_id, x.path });
                    table.ForeignKey(
                        name: "FK_songs_sources_source_id",
                        column: x => x.source_id,
                        principalTable: "sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "playlist",
                columns: table => new
                {
                    source_id = table.Column<long>(type: "INTEGER", nullable: false),
                    relative_path = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_playlist", x => new { x.source_id, x.relative_path });
                    table.ForeignKey(
                        name: "FK_playlist_songs_source_id_relative_path",
                        columns: x => new { x.source_id, x.relative_path },
                        principalTable: "songs",
                        principalColumns: new[] { "source_id", "path" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_playlist_sources_source_id",
                        column: x => x.source_id,
                        principalTable: "sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_stream_metadata_artist_album_title",
                table: "stream_metadata",
                columns: new[] { "artist", "album", "title" });

            migrationBuilder.CreateIndex(
                name: "IX_stream_metadata_start_time",
                table: "stream_metadata",
                column: "start_time",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "playlist");

            migrationBuilder.DropTable(
                name: "state");

            migrationBuilder.DropTable(
                name: "stream_metadata");

            migrationBuilder.DropTable(
                name: "songs");

            migrationBuilder.DropTable(
                name: "sources");
        }
    }
}
