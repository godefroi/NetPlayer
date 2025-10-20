using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;

namespace SongServer.Services;

public class PlayerClient(HttpClient httpClient)
{
	private readonly HttpClient _httpClient = httpClient;

	public string WhatIsYourBaseAddress() => _httpClient.BaseAddress?.ToString() ?? "[null]";

	public Task<PlayerState?> GetState() => _httpClient.GetFromJsonAsync<PlayerState>("player");

	public Task<PlayerState?> Play() => _httpClient.GetFromJsonAsync<PlayerState>("player/play");

	public Task Pause() => _httpClient.GetAsync("player/pause");

	public Task Stop() => _httpClient.GetAsync("player/stop");

	public Task<PlayerState?> Advance() => _httpClient.GetFromJsonAsync<PlayerState>("player/advance");

	public async Task<List<AlbumInfo>> GetAllAlbumsAsync(CancellationToken cancellationToken = default)
	{
		var result = await _httpClient.GetFromJsonAsync<List<AlbumInfo>>("library/albums", cancellationToken).ConfigureAwait(false);
		return result ?? [];
	}

	public async Task<IEnumerable<SongMetadata>> GetAlbumAsync(string artist, string album, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(artist);
		ArgumentNullException.ThrowIfNull(album);

		var path = $"library/album/{Uri.EscapeDataString(artist)}/{Uri.EscapeDataString(album)}";
		var result = await _httpClient.GetFromJsonAsync<IEnumerable<SongMetadata>>(path, cancellationToken).ConfigureAwait(false);
		return result ?? [];
	}

	public async Task<IEnumerable<SongMetadata>> SearchLibraryAsync(string query, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(query);

		var uri = $"library/search?query={Uri.EscapeDataString(query)}";
		var result = await _httpClient.GetFromJsonAsync<IEnumerable<SongMetadata>>(uri, cancellationToken).ConfigureAwait(false);
		return result ?? [];
	}

	public async Task<OperationResult> RefreshAsync(CancellationToken cancellationToken = default)
	{
		using var res = await _httpClient.PostAsync("library/refresh", null, cancellationToken).ConfigureAwait(false);
		res.EnsureSuccessStatusCode();
		var result = await res.Content.ReadFromJsonAsync<OperationResult>(cancellationToken: cancellationToken).ConfigureAwait(false);
		return result ?? throw new InvalidOperationException("Empty response from refresh");
	}

	public async Task<LibraryStats> GetLibraryStatsAsync(CancellationToken cancellationToken = default)
	{
		var result = await _httpClient.GetFromJsonAsync<LibraryStats>("library/stats", cancellationToken).ConfigureAwait(false);
		return result ?? throw new InvalidOperationException("Empty response from library stats");
	}

	public async Task<IEnumerable<SongMetadata>> GetPlaylistAsync(CancellationToken cancellationToken = default) =>
		await _httpClient.GetFromJsonAsync<IEnumerable<SongMetadata>>("playlist/", cancellationToken).ConfigureAwait(false) ?? [];

	public async Task<OperationResult> AddToPlaylistAsync(SongMetadata request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		using var res = await _httpClient.PostAsJsonAsync("playlist", request, cancellationToken).ConfigureAwait(false);

		if (res.IsSuccessStatusCode) {
			var result = await res.Content.ReadFromJsonAsync<OperationResult>(cancellationToken: cancellationToken).ConfigureAwait(false);
			return result ?? new OperationResult("Added to playlist");
		}

		return await TryReadOperationResultOrTextAsync(res, cancellationToken).ConfigureAwait(false);
	}

	public async Task<(OperationResult? Result, ProblemDetails? Problem)> RemoveFromPlaylistAsync(string artist, string title, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(artist);
		ArgumentNullException.ThrowIfNull(title);

		var uri = $"playlist?artist={Uri.EscapeDataString(artist)}&title={Uri.EscapeDataString(title)}";
		using var res = await _httpClient.DeleteAsync(uri, cancellationToken).ConfigureAwait(false);

		if (!res.IsSuccessStatusCode) {
			return (null, (await ReadProblemDetails(res, cancellationToken)).Details);
		}

		return (await res.Content.ReadFromJsonAsync<OperationResult>(cancellationToken).ConfigureAwait(false) ?? new OperationResult("Removed from playlist"), null);
	}

	private static async Task<(HttpStatusCode StatusCode, ProblemDetails Details)> ReadProblemDetails(HttpResponseMessage res, CancellationToken cancellationToken)
	{
		var content = await res.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken: cancellationToken).ConfigureAwait(false);

		if (content == null) {
			return (res.StatusCode, new ProblemDetails() { Title = res.ReasonPhrase ?? string.Empty });
		}

		return (res.StatusCode, content);
	}

	private static async Task<OperationResult> TryReadOperationResultOrTextAsync(HttpResponseMessage res, CancellationToken cancellationToken)
	{
		try {
			var op = await res.Content.ReadFromJsonAsync<OperationResult>(cancellationToken: cancellationToken).ConfigureAwait(false);
			if (op != null) {
				return op;
			}
		} catch {
			// ignore parse errors
		}

		try {
			var text = await res.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
			if (!string.IsNullOrWhiteSpace(text)) {
				return new OperationResult(text.Trim());
			}
		} catch {
			// ignore errors
		}

		return new OperationResult($"Request failed with status {(int)res.StatusCode} {res.ReasonPhrase}");
	}
}
