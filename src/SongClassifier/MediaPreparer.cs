using System.Buffers;
using System.Collections.Frozen;
using System.Diagnostics;
using System.IO.Compression;
using FFMpegCore;
using FFMpegCore.Pipes;
using Humanizer;
using Humanizer.Bytes;
using Spectre.Console;

namespace SongClassifier;

public static class MediaPreparer
{
	private readonly static FrozenSet<string> _extensions = new string[] {
			".mp3",
			".flac",
			".opus",
			".m4a"
		}.ToFrozenSet();

	private readonly static FrozenDictionary<string, string> _mimeTypes = new Dictionary<string, string>() {
		{ ".mp3", "audio/mpeg" },
		{ ".flac", "audio/flac" },
		{ ".opus", "audio/opus" },
		{ ".m4a", "audio/mp4" }
	}.ToFrozenDictionary();

	private readonly static SearchValues<char> _invalidChars = SearchValues.Create(Path.GetInvalidFileNameChars());

	public static async Task PrepareMedia(string fileName, string outputPath, CancellationToken cancellationToken = default)
	{
		using var fs      = File.OpenRead(fileName);
		using var archive = new ZipArchive(fs);

		var encodingInfos = new List<EncodingInfo>();
		var audioEntries = archive.Entries
			.Where(e => _extensions.Contains(Path.GetExtension(e.FullName)))
			.OrderBy(e => e.Name)
			.ToList();

		if (audioEntries.Count == 0) {
			throw new InvalidOperationException("No audio entries found (missing an extension?)");
		}

		var directories = audioEntries
			.Select(e => Path.GetDirectoryName(e.FullName))
			.Where(d => !string.IsNullOrWhiteSpace(d))
			.Distinct()
			.ToList();

		var albumName = directories.Count switch {
			0 => throw new InvalidOperationException("Unable to determine album name from archive (no directories)."),
			1 => directories[0],
			>1 => throw new InvalidOperationException("Unable to determine album name from archive (multiple directories)."),
			_ => throw new UnreachableException(),
		};

		// Create a table for displaying file analysis results
		var fileTable = new Table().Width(100);
		fileTable.AddColumn("File");
		fileTable.AddColumn("Size");
		fileTable.AddColumn("Codec");
		fileTable.AddColumn("Bitrate");
		fileTable.AddColumn("Duration");

		// Add rows with just filenames initially
		foreach (var entry in audioEntries) {
			fileTable.AddRow(entry.Name, new ByteSize(entry.Length).Humanize(), "", "", "");
		}

		var progressBar = new BarChart()
			.Width(100)
			.WithMaxValue(audioEntries.Count)
			.HideValues()
			.AddItem("Analysis", 0);

		var layoutTable = new Table().HideHeaders().NoBorder();
		layoutTable.AddColumn(string.Empty);
		layoutTable.AddRow(progressBar);
		layoutTable.AddRow(fileTable);

		await AnsiConsole.Live(layoutTable).StartAsync(async ctx => {
			for (var i = 0; i < audioEntries.Count; i++) {
				progressBar.Data[0] = new BarChartItem("Analysis", i + 1);
				var entry = audioEntries[i];
				var analysis = await Analyze(entry, cancellationToken);

				// Update table row with analysis results
				var codec = analysis.PrimaryAudioStream?.CodecName ?? "Unknown";
				var bitrate = analysis.PrimaryAudioStream?.BitRate ?? 0;
				var duration = analysis.PrimaryAudioStream?.Duration ?? TimeSpan.Zero;

				fileTable.UpdateCell(i, 2, codec);
				fileTable.UpdateCell(i, 3, FormatBitrate(bitrate));
				fileTable.UpdateCell(i, 4, FormatDuration(duration));

				encodingInfos.Add(new EncodingInfo(codec, bitrate));

				ctx.Refresh();
			}
		});

		AnsiConsole.WriteLine();

		var transcode = AnsiConsole.Prompt(new TextPrompt<bool>("Transcode?")
			.AddChoice(true)
			.AddChoice(false)
			.DefaultValue(false)
			.WithConverter(c => c ? "y" : "n"));

		for (var i = 0; i < audioEntries.Count; i++) {
			AnsiConsole.MarkupLineInterpolated($"Processing [green]{audioEntries[i].Name}[/]");
			await ProcessFile(audioEntries[i], outputPath, transcode);
		}

		AnsiConsole.WriteLine();
	}

	private static string FormatBitrate(long bitrate)
	{
		if (bitrate == 0) return "-";
		return $"{bitrate / 1_000}kbps";
	}

	private static string FormatDuration(TimeSpan duration)
	{
		if (duration == TimeSpan.Zero) return "-";
		return duration.ToString(@"hh\:mm\:ss");
	}

	private static async Task<IMediaAnalysis> Analyze(ZipArchiveEntry entry, CancellationToken cancellationToken = default)
	{
		using var entryStream = entry.Open();

		return await FFProbe.AnalyseAsync(entryStream, cancellationToken: cancellationToken);
	}

	private static async Task<bool> Convert(ZipArchiveEntry entry, string outputFilename)
	{
		using var entryStream = entry.Open();

		return await FFMpegArguments
			.FromPipeInput(new StreamPipeSource(entryStream))
			.OutputToFile(outputFilename, true, options => options
				.WithAudioCodec("libopus")
				.ForceFormat("opus")
				.WithAudioBitrate(160))
			//.NotifyOnProgress(dur => Console.WriteLine(dur))
			.ProcessAsynchronously(true);
	}

	private static async Task ProcessFile(ZipArchiveEntry entry, string outputPath, bool transcode)
	{
		var tfn = Path.GetTempFileName();
		var tag = default(TagLib.Tag);

		try {
			entry.ExtractToFile(tfn, true);

			using (var file = TagLib.File.Create(tfn, _mimeTypes[Path.GetExtension(entry.Name)], TagLib.ReadStyle.Average)) {
				tag = file.Tag;
			}

			var artist = SanitizePath(tag.FirstPerformer ?? tag.FirstAlbumArtist ?? "Unknown Artist");
			var album  = SanitizePath(tag.Album);
			var outDir = Path.Join(outputPath, artist, album);
			var outFn  = Path.Join(outDir, transcode ? Path.GetFileNameWithoutExtension(entry.Name) + ".opus" : entry.Name);

			if (!Directory.Exists(outDir)) {
				Directory.CreateDirectory(outDir);
			}

			if (transcode) {
				// transcode
				await Convert(entry, outFn);
			} else {
				// move
				File.Move(tfn, outFn);
			}
		} finally {
			try {
				if (File.Exists(tfn)) {
					File.Delete(tfn);
				}
			} catch {
				// not much we can do...
			}
		}
	}

	private static string SanitizePath(string path)
	{
		if (string.IsNullOrEmpty(path) || !MemoryExtensions.ContainsAny(path, _invalidChars)) {
			return path;
		}

		Span<char> result = stackalloc char[path.Length];

		path.AsSpan().CopyTo(result);

		for (int i = 0; i < result.Length; i++) {
			if (_invalidChars.Contains(result[i])) {
				result[i] = '_';
			}
		}

		return new string(result);
	}

	private record EncodingInfo(string Codec, long Rate);
}
