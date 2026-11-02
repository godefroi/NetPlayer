using Microsoft.EntityFrameworkCore;
using SongClassifier.Billboard;
using Spectre.Console;

namespace SongClassifier;

internal static class PlaylistEditor
{
	private const int _tableHeight = 30;

	public static async Task EditPlaylist()
	{
		using var context = new BillboardContext();

		var artists = (await context.RankedFiles
			.AsNoTracking()
			.Select(f => new { f.Artist, f.Album })
			.Distinct()
			.OrderBy(f => f.Artist)
			.ThenBy(f => f.Album)
			.ToListAsync())
			.GroupBy(n => n.Artist)
			.ToDictionary(g => g.Key, g => new ArtistNode(g.Key, g.Select(i => i.Album)));

		if (artists.Count == 0) {
			AnsiConsole.MarkupLine("[red]No albums found in RankedFiles.[/]");
			return;
		}

		AnsiConsole.MarkupLine("[grey]Use Up/Down to move, Right to expand, Left to collapse, Enter to open album, Esc to cancel.[/]");

		var windowStart = 0;
		var listState   = new List<ArtistAlbumRow>();
		var selIdx      = 0;
		var editArtist  = default(string?);
		var editAlbum   = default(string?);

		// build the initial state, which is only artists, as nothing is expanded
		foreach (var an in artists.Values) {
			listState.Add(new(an.Artist, null, false));
		}

		while (true) {
			(selIdx, windowStart, editArtist, editAlbum) = EditPlaylist(artists, listState, selIdx, windowStart);

			if (editArtist == default || editAlbum == default) {
				return;
			}

			await EditAlbum(editArtist, editAlbum);
		}
	}

	private static (int SelectedIndex, int WindowStart, string? Artist, string? Album) EditPlaylist(Dictionary<string, ArtistNode> artists, List<ArtistAlbumRow> state, int selIdx, int windowStart)
	{
		var editArtist = default(string?);
		var editAlbum  = default(string?);

		AnsiConsole.Live(BuildTree(state, selIdx, windowStart, _tableHeight)).AutoClear(true).Start(ctx => {
			while (true) {
				// ensure indices are valid
				selIdx = Math.Max(0, Math.Min(selIdx, Math.Max(0, state.Count - 1)));

				// adjust windowStart so the selected index is always visible
				if (selIdx < windowStart) {
					windowStart = selIdx;
				} else if (selIdx >= windowStart + _tableHeight) {
					windowStart = selIdx - _tableHeight + 1;
				}

				// ensure windowStart is within valid range
				if (state.Count <= _tableHeight) {
					windowStart = 0;
				} else {
					windowStart = Math.Max(0, Math.Min(windowStart, state.Count - _tableHeight));
				}

				ctx.UpdateTarget(BuildTree(state, selIdx, windowStart, _tableHeight));
				ctx.Refresh();

				switch (Console.ReadKey(true).Key) {
					case ConsoleKey.UpArrow:
						selIdx = (selIdx - 1 + state.Count) % state.Count;
						break;
					case ConsoleKey.DownArrow:
						selIdx = (selIdx + 1) % state.Count;
						break;
					case ConsoleKey.RightArrow:
						// expand artist if on artist row
						if (state[selIdx].Album is null && !state[selIdx].Expanded) {
							var artistName = state[selIdx].Artist;
							// mark expanded in state
							state[selIdx] = new(artistName, null, true);
							// find albums from artists list and insert after this row
							if (!string.IsNullOrWhiteSpace(artistName) && artists.TryGetValue(artistName, out var artistNode)) {
								for (var k = 0; k < artistNode.Albums.Count; k++) {
									state.Insert(selIdx + 1 + k, new(artistName, artistNode.Albums[k], false));
								}
							}
						}
						break;
					case ConsoleKey.LeftArrow:
						// if on artist row and expanded, collapse
						if (state[selIdx].Album is null && state[selIdx].Expanded) {
							var artistName = state[selIdx].Artist;
							state[selIdx] = new(artistName, null, false);
							// remove following album rows for same artist
							while (selIdx + 1 < state.Count && state[selIdx + 1].Album is not null && state[selIdx + 1].Artist == artistName) {
								state.RemoveAt(selIdx + 1);
							}
						} else if (state[selIdx].Album is not null) {
							// collapse parent artist and move selection to it
							var artistName = state[selIdx].Artist;
							int parentPos = -1;
							for (int p = selIdx - 1; p >= 0; p--) {
								if (state[p].Album is null && state[p].Artist == artistName) { parentPos = p; break; }
							}
							if (parentPos >= 0) {
								// collapse that artist
								state[parentPos] = new(artistName, null, false);
								// remove album rows after it
								while (parentPos + 1 < state.Count && state[parentPos + 1].Album is not null && state[parentPos + 1].Artist == artistName) {
									state.RemoveAt(parentPos + 1);
								}
								selIdx = parentPos;
							}
						}
						break;
					case ConsoleKey.Enter:
						if (state[selIdx].Album is not null) {
							editArtist = state[selIdx].Artist;
							editAlbum  = state[selIdx].Album;
						}
						return;
					case ConsoleKey.Escape:
						return;
					default:
						break;
				}
			}
		});

		return (selIdx, windowStart, editArtist, editAlbum);
	}

	private static async Task EditAlbum(string artist, string album)
	{
		using var context = new BillboardContext();

		var allCharts = context.Radio.Cast<Song>().Union(context.Hot100.Cast<Song>());
		var files = await context.RankedFiles
			.Where(f => f.Artist == artist && f.Album == album)
			.OrderBy(f => f.Track)
			.ToListAsync();

		if (files.Count == 0) {
			AnsiConsole.MarkupLine("[red]No files found for that artist/album.[/]");
			return;
		}

		var rows = files.Select(f => new PlaylistRow(
			f.Artist,
			f.Album,
			f.Track,
			f.Title,
			f.Filename,
			f.InPlaylist,
			f.Rank
		)).ToList();

		await EditAlbum(rows);
	}

	private static async Task EditAlbum(IList<PlaylistRow> rows)
	{
		using var context = new BillboardContext();

		var selIdx = 0;

		//AnsiConsole.MarkupLine("[grey]Use Up/Down to move, Space to toggle, Enter to accept changes, Esc to cancel.[/]");

		await AnsiConsole.Live(BuildTable(rows, selIdx)).AutoClear(true).StartAsync(async ctx => {
			while (true) {
				ctx.UpdateTarget(BuildTable(rows, selIdx));
				ctx.Refresh();

				switch (Console.ReadKey(true).Key) {
					case ConsoleKey.UpArrow:
						selIdx = (selIdx - 1 + rows.Count) % rows.Count;
						break;
					case ConsoleKey.DownArrow:
						selIdx = (selIdx + 1) % rows.Count;
						break;
					case ConsoleKey.Spacebar:
						// add or remove a row to the context.Playlist
						if (rows[selIdx].InPlaylist) {
							// remove existing playlist entry (if any)
							var existing = await context.Playlist.FindAsync(rows[selIdx].Filename);
							if (existing != null) {
								context.Playlist.Remove(existing);
							}
							rows[selIdx] = rows[selIdx] with { InPlaylist = false };
						} else {
							// add a new playlist entry
							context.Playlist.Add(new(rows[selIdx].Filename));
							rows[selIdx] = rows[selIdx] with { InPlaylist = true };
						}
						break;
					case ConsoleKey.Enter:
						await context.SaveChangesAsync();
						return;
					case ConsoleKey.Escape:
						return;
					default:
						// ignore other keys
						break;
				}
			}
		});
	}

	private static Table BuildTable(IList<PlaylistRow> rows, int selected)
	{
		var t = new Table();

		// Add a column showing the maximum rank (numeric) found for the song in Radio or Hot100
		t.AddColumns("✓", "Artist", "Album", "Track", "Title", "Rank");

		for (var i = 0; i < rows.Count; i++) {
			var (pre, post) = selected == i ? ("[black on yellow]", "[/]") : ("", "");

			t.AddRow(
				$"{pre}{(rows[i].InPlaylist ? "✓" : string.Empty)}{post}",
				$"{pre}{Markup.Escape(rows[i].Artist ?? string.Empty)}{post}",
				$"{pre}{Markup.Escape(rows[i].Album ?? string.Empty)}{post}",
				$"{pre}{rows[i].Track?.ToString()}{post}",
				$"{pre}{Markup.Escape(rows[i].Title ?? string.Empty)}{post}",
				$"{pre}{rows[i].MaxRank?.ToString() ?? string.Empty}{post}");
		}

		return t;
	}

	private static Table BuildTree(IList<ArtistAlbumRow> state, int selected, int windowStart, int windowSize)
	{
		var t = new Table();

		t.AddColumn("Artist / Album");

		if (state.Count == 0) {
			return t;
		}

		windowStart = Math.Max(0, Math.Min(windowStart, Math.Max(0, state.Count - 1)));

		var end = Math.Min(state.Count, windowStart + windowSize);

		for (var idx = windowStart; idx < end; idx++) {
			var (pre, post) = idx == selected ? ("[black on yellow]", "[/]") : ("", "");

			if (state[idx].Album is null) {
				t.AddRow($"{pre}{(state[idx].Expanded ? "▾" : "▸")} {Markup.Escape(state[idx].Artist ?? string.Empty)}{post}");
			} else {
				var lineChar = (idx + 1 < state.Count && state[idx + 1].Album is not null) ? '\u251c' : '\u2514';
				t.AddRow($"{pre} {lineChar}\u2500{Markup.Escape(state[idx].Album ?? string.Empty)}{post}");
			}
		}

		return t;
	}

	private record PlaylistRow(string Artist, string Album, int? Track, string Title, string Filename, bool InPlaylist, int? MaxRank);

	private record ArtistAlbumRow(string? Artist, string? Album, bool Expanded = false);

	private class ArtistNode(string artist, IEnumerable<string> albums)
	{
		public string Artist { get; init; } = artist;
		public List<string> Albums { get; init; } = [.. albums];
		public bool IsExpanded { get; set; } = false;
	}
}
