﻿using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Nexus.DataModel;
using Nexus.Extensibility;

namespace Nexus.Sources;

[ExtensionDescription(
    "Provides access to other Nexus databases.",
    "https://github.com/Apollo3zehn/nexus-sources-federation",
    "https://github.com/Apollo3zehn/nexus-sources-federation")]
public class Federation : IDataSource
{
    private DataSourceContext _context = default!;
    private Api.INexusClient _nexusClient = default!;
    private string _sourcePath = "/";
    private string _mountPoint = "/";
    private string _includePattern = default!;

    private static readonly JsonSerializerOptions _jsonSerializerOptions;

    static Federation()
    {
        _jsonSerializerOptions = new JsonSerializerOptions();
        _jsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public Func<HttpClient, Api.INexusClient> CreateNexusClient { get; set; }
        = httpClient => new Api.NexusClient(httpClient);

    public Task SetContextAsync(DataSourceContext context, ILogger logger, CancellationToken cancellationToken)
    {
        _context = context;

        // http client
        if (_context.ResourceLocator is null)
            throw new Exception("The resource locator must be set.");

        var httpClient = new HttpClient()
        {
            BaseAddress = _context.ResourceLocator
        };

        // token
        var token = (_context.SourceConfiguration?.GetStringValue($"access-token")) ?? throw new Exception("The access-token property is not set.");

        _nexusClient = CreateNexusClient(httpClient);
        _nexusClient.SignIn(token);

        // source-path
        var sourcePath = _context.SourceConfiguration?.GetStringValue($"source-path");

        if (sourcePath is not null)
            _sourcePath = '/' + sourcePath.Trim('/');

        // mount-point
        var mountPoint = _context.SourceConfiguration?.GetStringValue($"mount-point");

        if (mountPoint is not null)
            _mountPoint = '/' + mountPoint.Trim('/');

        // include-pattern
        var includePattern = _context.SourceConfiguration?.GetStringValue($"include-pattern");
        _includePattern = includePattern ?? "";

        return Task.CompletedTask;
    }

    public async Task<CatalogRegistration[]> GetCatalogRegistrationsAsync(string path, CancellationToken cancellationToken)
    {
        if (path == "/")
            path = _mountPoint;

        else
            path = path.TrimEnd('/');

        var catalogInfos = await _nexusClient.V1.Catalogs.GetChildCatalogInfosAsync(ToSourcePathPrefixedCatalogId(path), cancellationToken);

        return catalogInfos
            .Where(catalogInfo => Regex.IsMatch(catalogInfo.Id, _includePattern))
            .Select(catalogInfo => new CatalogRegistration(ToMountPointPrefixedCatalogId(catalogInfo.Id), catalogInfo.Title, IsTransient: true))
            .ToArray();
    }

    public async Task<ResourceCatalog> EnrichCatalogAsync(ResourceCatalog catalog, CancellationToken cancellationToken)
    {
        var newCatalog = await _nexusClient.V1.Catalogs.GetAsync(ToSourcePathPrefixedCatalogId(catalog.Id), cancellationToken);
        newCatalog = newCatalog with { Id = catalog.Id };

        // convert from Api.V1.ResourceCatalog to Nexus.DataModel.ResourceCatalog
        var jsonString = JsonSerializer.Serialize(newCatalog, _jsonSerializerOptions);
        var actualNewCatalog = JsonSerializer.Deserialize<ResourceCatalog>(jsonString, _jsonSerializerOptions)!;

        return catalog.Merge(actualNewCatalog);
    }

    public async Task<double> GetAvailabilityAsync(string catalogId, DateTime begin, DateTime end, CancellationToken cancellationToken)
    {
        var availability = await _nexusClient.V1.Catalogs
            .GetAvailabilityAsync(ToSourcePathPrefixedCatalogId(catalogId), begin, end, end - begin, cancellationToken);

        return availability.Data[0];
    }

    public async Task<(DateTime Begin, DateTime End)> GetTimeRangeAsync(string catalogId, CancellationToken cancellationToken)
    {
        var timeRange = await _nexusClient.V1.Catalogs
            .GetTimeRangeAsync(ToSourcePathPrefixedCatalogId(catalogId), cancellationToken);

        return (timeRange.Begin, timeRange.End);
    }

    public async Task ReadAsync(DateTime begin, DateTime end, ReadRequest[] requests, ReadDataHandler readData, IProgress<double> progress, CancellationToken cancellationToken)
    {
        foreach (var request in requests)
        {
            var response = await _nexusClient.V1.Data.GetStreamAsync(ToSourcePathPrefixedCatalogId(request.CatalogItem.ToPath()), begin, end, cancellationToken);
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var targetBuffer = request.Data;

            while (targetBuffer.Length > 0)
            {
                var bytesRead = await stream.ReadAsync(targetBuffer, cancellationToken);
                targetBuffer = targetBuffer[bytesRead..];
            }

            request.Status.Span.Fill(1);
        }
    }

    private string ToMountPointPrefixedCatalogId(string catalogId)
    {
        //                     absolute, relative
        return Path.Combine(_mountPoint, catalogId[_sourcePath.Length..].TrimStart('/'));
    }

    private string ToSourcePathPrefixedCatalogId(string catalogId)
    {
        //                     absolute, relative
        return Path.Combine(_sourcePath, catalogId[_mountPoint.Length..].TrimStart('/'));
    }
}
