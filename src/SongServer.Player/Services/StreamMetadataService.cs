using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SongServer.Configuration;
using SongServer.Data;

namespace SongServer.Services;

public class StreamMetadataService : IHostedService, IDisposable
{
	private readonly Timer _timer;
	private readonly IDbContextFactory<LibraryContext> _contextFactory;
	private readonly StreamMetadataOptions _options;
	private readonly HttpClient _httpClient;
	private readonly ILogger _logger;
	private long _lastStart = 0;

	public StreamMetadataService(IOptions<StreamMetadataOptions> options, IDbContextFactory<LibraryContext> contextFactory, ILogger<StreamMetadataService> logger)
	{
		_timer = new(TimerElapsed, null, Timeout.Infinite, Timeout.Infinite);
		_contextFactory = contextFactory;
		_options = options.Value;
		_logger = logger;
		_httpClient = new HttpClient(new HttpClientHandler() {
			AutomaticDecompression = System.Net.DecompressionMethods.All,
		});

		_httpClient.DefaultRequestHeaders.Clear();
		_httpClient.DefaultRequestHeaders.Add("Accept", "application/json, text/plain, */*");
		_httpClient.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.5");
		_httpClient.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate, br, zstd");
		_httpClient.DefaultRequestHeaders.Add("X-hostName", "webapp.US");
		_httpClient.DefaultRequestHeaders.Add("X-Locale", "en-US");
		_httpClient.DefaultRequestHeaders.Add("Origin", _options.RefererUri.ToString());
		_httpClient.DefaultRequestHeaders.Add("Referer", _options.RefererUri.ToString());
		_httpClient.DefaultRequestHeaders.Add("Sec-Fetch-Dest", "empty");
		_httpClient.DefaultRequestHeaders.Add("Sec-Fetch-Mode", "cors");
		_httpClient.DefaultRequestHeaders.Add("Sec-Fetch-Site", "same-site");
		_httpClient.DefaultRequestHeaders.Add("Pragma", "no-cache");
		_httpClient.DefaultRequestHeaders.Add("Cache-Control", "no-cache");
		_httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:144.0) Gecko/20100101 Firefox/144.0");
	}

	public void Dispose()
	{
		_timer.Dispose();
		GC.SuppressFinalize(this);
	}

	public Task StartAsync(CancellationToken cancellationToken)
	{
		if (!_timer.Change(TimeSpan.Zero, TimeSpan.FromMinutes(1))) {
			throw new Exception("Timer startup failed.");
		}

		return Task.CompletedTask;
	}

	public Task StopAsync(CancellationToken cancellationToken)
	{
		_timer.Change(Timeout.Infinite, Timeout.Infinite);

		return Task.CompletedTask;
	}

	private async void TimerElapsed(object? state)
	{
		var resp = await _httpClient.GetAsync(_options.MetadataUri);

		if (!resp.IsSuccessStatusCode) {
			_logger.LogWarning("Stream metadata retrieval failed with non-success status: {status} ({message})", resp.StatusCode, resp.ReasonPhrase);
			return;
		}

		var content = await resp.Content.ReadAsStringAsync();
		var start   = -1L;

		if (content.Length == 0) {
			_logger.LogDebug("Stream metadata retrieval failed with zero-length content.");
			return;
		}

		try {
			using var jd = JsonDocument.Parse(content);

			var artist = jd.RootElement.GetProperty("artist").GetString();
			var album  = jd.RootElement.GetProperty("album").GetString();
			var title  = jd.RootElement.GetProperty("title").GetString();
			start  = jd.RootElement.GetProperty("startTime").GetInt64();

			if (_lastStart != start) {
				using var ctx = await _contextFactory.CreateDbContextAsync();
				ctx.StreamMetadata.Add(new StreamMetadata() { Metadata = content });
				await ctx.SaveChangesAsync();

				_logger.LogInformation("Now playing: [{title}] by {artist} ({album})", title, artist, album);
				_lastStart = start;
			}
		} catch (DbUpdateException e) when (e.InnerException is SqliteException se && se.SqliteErrorCode == 19 && se.SqliteExtendedErrorCode == 2067) {
			_logger.LogInformation("Metadata already recorded for this start time");
			if (start != -1) {
				_lastStart = start;
			}
		} catch (DbUpdateException e) when (e.InnerException is SqliteException se) {
			_logger.LogError(se, "SQLite error code {eCode}, extended error code {seCode}", se.SqliteErrorCode, se.SqliteExtendedErrorCode);
		} catch (Exception e) {
			_logger.LogWarning(e, "Failed to store stream metadata response.");
		}
	}
}
