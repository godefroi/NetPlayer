using System.Buffers;
using System.Collections.Frozen;
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

		var files       = new List<AnalyzedFile>();
		var layoutTable = new Table().HideHeaders().NoBorder();
		layoutTable.AddColumn(string.Empty);
		layoutTable.AddRow(progressBar);
		layoutTable.AddRow(fileTable);

		await AnsiConsole.Live(layoutTable).StartAsync(async ctx => {
			for (var i = 0; i < audioEntries.Count; i++) {
				progressBar.Data[0] = new BarChartItem("Analysis", i + 1);
				var entry = audioEntries[i];
				var analysis = await Analyze(entry, cancellationToken);

				files.Add(analysis);

				// Update table row with analysis results
				fileTable.UpdateCell(i, 2, analysis.Codec);
				fileTable.UpdateCell(i, 3, FormatBitrate(analysis.BitRate));
				fileTable.UpdateCell(i, 4, FormatDuration(analysis.Duration));

				encodingInfos.Add(new EncodingInfo(analysis.Codec, analysis.BitRate));

				ctx.Refresh();
			}
		});

		AnsiConsole.WriteLine();

		var haveDiscs = files.Select(f => f.Tag.Disc).Distinct().Count() > 1;
		var transcode = AnsiConsole.Prompt(new TextPrompt<bool>("Transcode?")
			.AddChoice(true)
			.AddChoice(false)
			.DefaultValue(false)
			.WithConverter(c => c ? "y" : "n"));

		for (var i = 0; i < files.Count; i++) {
			AnsiConsole.MarkupLineInterpolated($"Processing [green]{audioEntries[i].Name}[/]");
			await ProcessFile(files[i], outputPath, haveDiscs, transcode);
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

	private static async Task<AnalyzedFile> Analyze(ZipArchiveEntry entry, CancellationToken cancellationToken = default)
	{
		using var entryStream = entry.Open();

		var dataStream = new MemoryStream((int)entry.Length);

		await entryStream.CopyToAsync(dataStream, cancellationToken);

		dataStream.Position = 0;

		var analysis = await FFProbe.AnalyseAsync(dataStream, cancellationToken: cancellationToken);
		var tag      = TagLib.File.Create(new MemoryStreamAbstraction(entry.Name, dataStream)).Tag;

		return new AnalyzedFile(entry.Name, analysis, tag, dataStream);
	}

	private static async Task<bool> Convert(Stream data, string outputFilename)
	{
		return await FFMpegArguments
			.FromPipeInput(new StreamPipeSource(data))
			.OutputToFile(outputFilename, true, options => options
				.WithAudioCodec("libopus")
				.ForceFormat("opus")
				.WithAudioBitrate(160))
			//.NotifyOnProgress(dur => Console.WriteLine(dur))
			.ProcessAsynchronously(true);
	}

	private static async Task ProcessFile(AnalyzedFile file, string outputPath, bool haveDiscs, bool transcode)
	{
		var discPath = haveDiscs ? $"Disc {file.Tag.Disc}" : null;
		var artist   = SanitizePath(file.Tag.FirstPerformer ?? file.Tag.FirstAlbumArtist ?? "Unknown Artist");
		var album    = SanitizePath(file.Tag.Album);
		var outDir   = Path.Join(outputPath, artist, album, discPath);
		var outFn    = Path.Join(outDir, transcode ? Path.GetFileNameWithoutExtension(file.Name) + ".opus" : file.Name);

		if (!Directory.Exists(outDir)) {
			Directory.CreateDirectory(outDir);
		}

		file.Data.Position = 0;

		if (transcode) {
			// transcode
			await Convert(file.Data, outFn);
		} else {
			// move
			using var fs = File.Create(outFn);
			file.Data.CopyTo(fs);
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

internal record AnalyzedFile(string Name, IMediaAnalysis Analysis, TagLib.Tag Tag, Stream Data)
{
	public string Codec => Analysis.PrimaryAudioStream?.CodecName ?? "Unknown";

	public long BitRate => Analysis.PrimaryAudioStream?.BitRate ?? 0;

	public TimeSpan Duration = Analysis.PrimaryAudioStream?.Duration ?? TimeSpan.Zero;
}

internal class MemoryStreamAbstraction(string name, MemoryStream stream) : TagLib.File.IFileAbstraction
{
	public string Name => name;

	public Stream ReadStream {
		get {
			stream.Position = 0;
			return stream;
		}
	}

	public Stream WriteStream => throw new NotImplementedException();

	public void CloseStream(Stream stream) { }
}
