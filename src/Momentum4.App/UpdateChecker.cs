// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Momentum4.App;

/// <summary>Ergebnis einer Update-Prüfung.</summary>
public sealed record UpdateInfo(bool Available, string LatestVersion, string Url);

/// <summary>
/// Optionale Update-Prüfung gegen die GitHub-Release-API (docs privacy: standardmäßig aus, opt-in in den Einstellungen).
/// Fragt nur das neueste Release ab und vergleicht die Version; lädt oder installiert nichts.
/// </summary>
public static class UpdateChecker
{
    private const string LatestReleaseApi = "https://api.github.com/repos/Ariazonaa/momentum4-control/releases/latest";

    /// <summary>Aktuelle Programmversion (Major.Minor.Build).</summary>
    public static Version CurrentVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, v.Build);
        }
    }

    /// <summary>
    /// Fragt das neueste GitHub-Release ab. Gibt <c>null</c> zurück, wenn nichts erreichbar/kein Release vorhanden ist.
    /// <see cref="UpdateInfo.Available"/> ist <c>true</c>, wenn die veröffentlichte Version neuer ist als die laufende.
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Momentum4Control", CurrentVersion.ToString()));
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var response = await http.GetAsync(LatestReleaseApi, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                // 404: es gibt noch kein Release – dann ist die laufende Version aktuell.
                return response.StatusCode == System.Net.HttpStatusCode.NotFound ? new UpdateInfo(false, string.Empty, string.Empty) : null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var release = await JsonSerializer.DeserializeAsync(stream, GithubJsonContext.Default.GithubRelease, cancellationToken);
            if (release?.TagName is not { Length: > 0 } tag || !Version.TryParse(tag.TrimStart('v', 'V'), out var latest))
            {
                return null;
            }

            latest = new Version(latest.Major, Math.Max(latest.Minor, 0), Math.Max(latest.Build, 0));
            return new UpdateInfo(latest > CurrentVersion, tag.TrimStart('v', 'V'), release.HtmlUrl ?? string.Empty);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }
}

internal sealed class GithubRelease
{
    [JsonPropertyName("tag_name")]
    public string? TagName { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(GithubRelease))]
internal sealed partial class GithubJsonContext : JsonSerializerContext;
