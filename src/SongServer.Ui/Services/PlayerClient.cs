using System.Net.Http.Json;

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
}
