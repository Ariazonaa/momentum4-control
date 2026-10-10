using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Momentum4.Protocol.Dongle;

namespace Momentum4.Core.Dongle;

public sealed partial class DongleFirmwareService : IDisposable
{
    public const int MaximumMetadataSize = 256 * 1024;
    public const int MaximumPackages = 32;
    public static TimeSpan PreparationTimeout { get; } = TimeSpan.FromSeconds(90);
    private const string ApiBase = "https://api.s-consumer-cloud.com/firmware/api/v2/";
    private const string ReleasesUrl = ApiBase + "availableSystemReleases/700434?os_type=android";
    private readonly IDongleLeaseProvider leases;
    private readonly TimeProvider clock;
    private readonly HttpClient http;
    private readonly object issuer = new();

    public DongleFirmwareService(IDongleLeaseProvider leases, TimeProvider? timeProvider = null, HttpMessageHandler? httpMessageHandler = null)
    {
        this.leases = leases; clock = timeProvider ?? TimeProvider.System;
        http = new HttpClient(httpMessageHandler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.None, ConnectTimeout = TimeSpan.FromSeconds(60), MaxResponseHeadersLength = 64 }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<DongleUpdatePlan> CheckUpdateAsync(DongleTarget target, CancellationToken cancellationToken = default)
    {
        using var preparation = new DongleBudget(clock, PreparationTimeout, cancellationToken);
        try { return await CheckUpdateCoreAsync(target, preparation, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        { throw new DongleException("Firmware preparation deadline exceeded", exception); }
    }

    private async Task<DongleUpdatePlan> CheckUpdateCoreAsync(DongleTarget target, DongleBudget preparation, CancellationToken cancellationToken)
    {
        preparation.Check();
        UpgradeVersion current;
        using (var budget = preparation.Slice(30))
        {
            await using var lease = await leases.AcquireAsync(target, budget.Remaining, budget.Token).ConfigureAwait(false);
            if (lease.Target != target) throw new DongleException("BTD 700 lease identity mismatch");
            await using var transport = new DongleUpgradeSession(lease, target, budget);
            await transport.ConnectAsync().ConfigureAwait(false); current = await transport.GetVersionAsync().ConfigureAwait(false);
        }
        var releases = await JsonResponseAsync(ReleasesUrl, preparation, cancellationToken).ConfigureAwait(false);
        if (releases.ValueKind != JsonValueKind.Array || releases.GetArrayLength() is < 1 or > MaximumPackages) throw new DongleException("Invalid official firmware release list");
        var versions = releases.EnumerateArray().Select(MetadataVersion).ToArray();
        if (versions.Distinct().Count() != versions.Length) throw new DongleException("Duplicate official firmware releases");
        var latest = versions.Max();
        if (latest <= current.Firmware) return new DongleUpdatePlan(issuer, target, current, latest, null, null);
        var manifest = await JsonResponseAsync(ApiBase + $"systemRelease/700434/{latest}?os_type=android", preparation, cancellationToken).ConfigureAwait(false);
        if (manifest.ValueKind != JsonValueKind.Object || StringProperty(manifest, "systemRelease") != latest.ToString()) throw new DongleException("Official firmware manifest version mismatch");
        if (!manifest.TryGetProperty("packages", out var packages) || packages.ValueKind != JsonValueKind.Array || packages.GetArrayLength() is < 1 or > MaximumPackages || packages.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.Object)) throw new DongleException("Invalid official firmware package list");
        var candidates = packages.EnumerateArray().Where(item => StringProperty(item, "type") == "FW_FULL" && StringProperty(item, "identifier") == "0x0000").ToArray();
        if (candidates.Length != 1) throw new DongleException("Expected exactly one official full BTD 700 firmware package");
        var package = candidates[0];
        if (!package.TryGetProperty("version", out var version) || MetadataVersion(version) != latest) throw new DongleException("Official package version mismatch");
        if (!package.TryGetProperty("size", out var sizeElement)) throw new DongleException("Invalid official package size");
        int size;
        if (sizeElement.ValueKind == JsonValueKind.Number && sizeElement.TryGetInt32(out var number)) size = number;
        else if (sizeElement.ValueKind == JsonValueKind.String && Regex.IsMatch(sizeElement.GetString()!, "\\A[0-9]{1,8}\\z", RegexOptions.CultureInvariant) && int.TryParse(sizeElement.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out number)) size = number;
        else throw new DongleException("Invalid official package size");
        if (size is < 1 or > FirmwareImage.MaximumSize) throw new DongleException("Official package exceeds firmware size limit");
        var url = StringProperty(package, "url") ?? throw new DongleException("Invalid firmware URL");
        ValidateImageUrl(url, latest);
        var data = await FetchAsync(url, size, preparation, cancellationToken).ConfigureAwait(false);
        if (data.Length != size) throw new DongleException("Official firmware package size mismatch");
        var image = FirmwareImage.Parse(data);
        if (image.Version != latest) throw new DongleException("Image version does not match official release");
        image.RequireCompatible(current);
        return new DongleUpdatePlan(issuer, target, current, latest, image, url);
    }

    internal static void ValidateImageUrl(string url, FirmwareVersion version)
    {
        var prefix = $"https://firmware.s-consumer-cloud.com/files/btd700/{version}/";
        if (url.Length > 2048 || !url.StartsWith(prefix, StringComparison.Ordinal) || !Regex.IsMatch(url[prefix.Length..], "\\A[A-Za-z0-9][A-Za-z0-9_-]*\\.bin\\z", RegexOptions.CultureInvariant)) throw new DongleException("Firmware URL is outside the official BTD 700 repository");
    }

    private async Task<byte[]> FetchAsync(string url, int limit, DongleBudget preparation, CancellationToken cancellationToken)
    {
        var officialApi = Regex.IsMatch(url, "\\A" + Regex.Escape(ApiBase) + "(?:availableSystemReleases/700434|systemRelease/700434/[0-9]+\\.[0-9]+\\.[0-9]+)\\?os_type=android\\z", RegexOptions.CultureInvariant);
        var officialImage = Regex.IsMatch(url, "\\Ahttps://firmware\\.s-consumer-cloud\\.com/files/btd700/[0-9]+\\.[0-9]+\\.[0-9]+/[A-Za-z0-9][A-Za-z0-9_-]*\\.bin\\z", RegexOptions.CultureInvariant);
        if (!officialApi && !officialImage) throw new DongleException("Refusing nonofficial firmware request");
        using var budget = preparation.Slice(60);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url));
            request.Headers.AcceptEncoding.ParseAdd("identity");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token).ConfigureAwait(false);
            budget.Check();
            if (response.StatusCode != HttpStatusCode.OK || response.RequestMessage?.RequestUri is { } actual && actual != request.RequestUri) throw new DongleException("Official firmware server returned non-200 response; redirects are refused");
            if (response.Content.Headers.ContentEncoding.Any(item => item != "identity")) throw new DongleException("Encoded firmware responses are unsupported");
            long? declared = null;
            if (response.Content.Headers.TryGetValues("Content-Length", out var lengths))
            {
                var values = lengths.ToArray();
                if (values.Length != 1 || !Regex.IsMatch(values[0], "\\A[0-9]{1,10}\\z", RegexOptions.CultureInvariant) || !long.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length > limit) throw new DongleException("Official firmware response exceeds size limit");
                declared = length;
            }
            await using var source = await response.Content.ReadAsStreamAsync(budget.Token).ConfigureAwait(false);
            using var result = new MemoryStream(); var buffer = new byte[Math.Min(65536, limit + 1)];
            while (true)
            {
                var count = await source.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, limit + 1 - (int)result.Length)), budget.Token).ConfigureAwait(false);
                budget.Check(); if (count == 0) break;
                result.Write(buffer, 0, count);
                if (result.Length > limit) throw new DongleException("Official firmware response exceeds size limit");
            }
            if (declared is not null && result.Length != declared) throw new DongleException("Truncated official firmware response");
            return result.ToArray();
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested) { throw new DongleException("Firmware download deadline exceeded", exception); }
        catch (HttpRequestException exception) { throw new DongleException("Official firmware download failed", exception); }
    }

    private async Task<JsonElement> JsonResponseAsync(string url, DongleBudget preparation, CancellationToken cancellationToken)
    {
        var bytes = await FetchAsync(url, MaximumMetadataSize, preparation, cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            UniqueObjects(document.RootElement);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True || !root.TryGetProperty("data", out var data)) throw new DongleException("Official firmware metadata reports failure");
            return data.Clone();
        }
        catch (JsonException exception) { throw new DongleException("Invalid official firmware metadata", exception); }
    }

    private static void UniqueObjects(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            { if (!names.Add(property.Name)) throw new DongleException("Duplicate key in official firmware metadata"); UniqueObjects(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) UniqueObjects(item);
    }
    private static string? StringProperty(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static FirmwareVersion MetadataVersion(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String) throw new DongleException("Invalid firmware version in metadata");
        var text = element.GetString()!;
        if (!Regex.IsMatch(text, "\\A(?:0|[1-9][0-9]{0,4})\\.(?:0|[1-9][0-9]{0,2})\\.(?:0|[1-9][0-9]?)\\z", RegexOptions.CultureInvariant)) throw new DongleException("Invalid firmware version in metadata");
        try { return FirmwareVersion.Parse(text); }
        catch (FormatException exception) { throw new DongleException("Invalid firmware version in metadata", exception); }
    }
    public void Dispose() => http.Dispose();
}
