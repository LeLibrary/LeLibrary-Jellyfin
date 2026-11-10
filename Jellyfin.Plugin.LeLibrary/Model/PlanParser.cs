using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.LeLibrary.Model;

/// <summary>
/// The result of reading a plan: the plan itself plus anything the reader had to
/// ignore. Warnings are logged, never fatal.
/// </summary>
/// <param name="Plan">The parsed plan.</param>
/// <param name="Warnings">Human-readable notes.</param>
public sealed record PlanParseResult(JellyfinPlan Plan, IReadOnlyList<string> Warnings);

/// <summary>A plan document LeLibrary could not read at all.</summary>
public sealed class PlanFormatException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="PlanFormatException"/> class.</summary>
    /// <param name="message">What is wrong.</param>
    /// <param name="inner">The parser error, if any.</param>
    public PlanFormatException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Reads the plan JSON. Forward compatible on purpose: a newer schema still
/// parses on the fields this version knows, unknown fields are ignored, and an
/// item that cannot be used is dropped with a warning rather than failing the
/// whole sync.
/// </summary>
public static class PlanParser
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>Parses a plan body.</summary>
    /// <param name="json">The response body.</param>
    /// <returns>The plan and any warnings.</returns>
    public static PlanParseResult Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new PlanFormatException("the LeLibrary response was empty");
        }

        PlanDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<PlanDto>(json, Options);
        }
        catch (JsonException err)
        {
            throw new PlanFormatException("the LeLibrary response was not valid plan JSON", err);
        }

        if (dto is null)
        {
            throw new PlanFormatException("the LeLibrary response was not a plan");
        }

        var warnings = new List<string>();
        if (dto.SchemaVersion > JellyfinPlan.CurrentSchemaVersion)
        {
            warnings.Add($"plan schema {dto.SchemaVersion} is newer than this plugin ({JellyfinPlan.CurrentSchemaVersion}); using the fields it knows");
        }

        if (!string.IsNullOrWhiteSpace(dto.Error))
        {
            throw new PlanFormatException($"LeLibrary answered with an error: {dto.Error}");
        }

        var items = new List<PlanItem>();
        foreach (var item in dto.Items ?? new List<PlanItem>())
        {
            if (string.IsNullOrWhiteSpace(item.Key) || string.IsNullOrWhiteSpace(item.Path))
            {
                warnings.Add($"dropped a plan item without a key or path ({item.Title ?? item.SeriesTitle ?? "unknown"})");
                continue;
            }

            items.Add(item);
        }

        var libraries = dto.Libraries ?? new List<PlanLibrary>();
        var plan = new JellyfinPlan
        {
            SchemaVersion = dto.SchemaVersion,
            Provider = dto.Provider ?? string.Empty,
            Libraries = libraries,
            Items = items,
        };
        return new PlanParseResult(plan, warnings);
    }

    private sealed class PlanDto
    {
        public int SchemaVersion { get; set; }

        public string? Provider { get; set; }

        public string? Error { get; set; }

        public List<PlanLibrary>? Libraries { get; set; }

        public List<PlanItem>? Items { get; set; }
    }
}
