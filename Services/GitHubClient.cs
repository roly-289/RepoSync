using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace RepoSync;

public sealed record GitHubRepo(string Name, string CloneUrl, bool Private);

/// <summary>Lists repositories through the GitHub REST API.</summary>
public static class GitHubClient
{
    static readonly HttpClient Http = new();

    static GitHubClient() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("RepoSync");

    /// <summary>
    /// With a token: every repo the token can see, including private ones (the profile is not used).
    /// Without one: the public repos of <paramref name="user"/>.
    /// </summary>
    public static async Task<List<GitHubRepo>> ListReposAsync(string user, string token)
    {
        var baseUrl = token != ""
            ? "https://api.github.com/user/repos?per_page=100"
            : $"https://api.github.com/users/{Uri.EscapeDataString(user)}/repos?per_page=100";

        var repos = new List<GitHubRepo>();
        for (int page = 1; ; page++)
        {
            using var doc = JsonDocument.Parse(await GetAsync($"{baseUrl}&page={page}", token));
            var items = doc.RootElement.EnumerateArray().ToList();
            foreach (var r in items.Where(x => token != "" || user == "" ||
                         string.Equals(x.GetProperty("owner").GetProperty("login").GetString(), user, StringComparison.OrdinalIgnoreCase)))
                repos.Add(new(r.GetProperty("name").GetString()!, r.GetProperty("clone_url").GetString()!, r.GetProperty("private").GetBoolean()));
            if (items.Count < 100) break;
        }
        return repos;
    }

    /// <summary>"token user: name", or why the token was rejected. Used to diagnose missing private repos.</summary>
    public static async Task<string> DescribeTokenAsync(string token)
    {
        try
        {
            using var doc = JsonDocument.Parse(await GetAsync("https://api.github.com/user", token));
            return "token user: " + doc.RootElement.GetProperty("login").GetString();
        }
        catch (Exception ex) { return "TOKEN REJECTED: " + ex.Message; }
    }

    static async Task<string> GetAsync(string url, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (token != "") request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await Http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}
