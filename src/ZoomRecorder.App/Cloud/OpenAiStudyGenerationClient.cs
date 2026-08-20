using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ZoomRecorder.Core.Processing;

namespace ZoomRecorder.App.Cloud;

internal sealed class OpenAiStudyGenerationClient(OpenAiApiClient api) : IStudyGenerationClient
{
    private const string LectureSchemaName = "zoom_recorder_study_package_v1";
    private const string GuideSchemaName = "zoom_recorder_class_study_guide_v1";
    private readonly OpenAiApiClient api = api ?? throw new ArgumentNullException(nameof(api));

    public async Task<StudyPackage> GenerateLectureAsync(
        Transcript transcript,
        CancellationToken cancellationToken)
    {
        ValidateTranscript(transcript);
        cancellationToken.ThrowIfCancellationRequested();
        var output = await GenerateAsync(
            "Create a version 1 lecture study package from the timestamped transcript. Preserve timestamp evidence.",
            JsonSerializer.Serialize(transcript, OpenAiJson.Options),
            LectureSchemaName,
            LectureSchema(),
            cancellationToken);

        try
        {
            ValidateLectureJson(output);
            var package = JsonSerializer.Deserialize<StudyPackage>(output, OpenAiJson.Options)
                ?? throw new JsonException("The study package was empty.");
            StudyPackageValidator.Validate(package);
            return package;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or StudyPackageValidationException)
        {
            throw OpenAiErrorMapper.InvalidResponse();
        }
    }

    public async Task<ClassStudyGuide> GenerateGuideAsync(
        IReadOnlyList<StudyPackage> lectures,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lectures);
        if (lectures.Count == 0 || lectures.Any(lecture => lecture is null))
        {
            throw new ArgumentException("At least one valid lecture package is required.", nameof(lectures));
        }
        foreach (var lecture in lectures)
        {
            StudyPackageValidator.Validate(lecture);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var output = await GenerateAsync(
            "Rebuild the version 1 cumulative class study guide from the supplied lecture packages.",
            JsonSerializer.Serialize(lectures, OpenAiJson.Options),
            GuideSchemaName,
            GuideSchema(),
            cancellationToken);

        try
        {
            ValidateGuideJson(output);
            var guide = JsonSerializer.Deserialize<ClassStudyGuide>(output, OpenAiJson.Options)
                ?? throw new JsonException("The class guide was empty.");
            ValidateGuide(guide);
            return guide;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException)
        {
            throw OpenAiErrorMapper.InvalidResponse();
        }
    }

    private async Task<string> GenerateAsync(
        string instruction,
        string input,
        string schemaName,
        JsonNode schema,
        CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new
        {
            model = api.Options.StudyModel,
            input = new[]
            {
                new
                {
                    role = "developer",
                    content = new[] { new { type = "input_text", text = instruction } }
                },
                new
                {
                    role = "user",
                    content = new[] { new { type = "input_text", text = input } }
                }
            },
            text = new
            {
                format = new
                {
                    type = "json_schema",
                    name = schemaName,
                    strict = true,
                    schema
                }
            }
        }, OpenAiJson.Options);

        using var response = await api.SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, api.Endpoint("/v1/responses"))
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            },
            cancellationToken);

        try
        {
            await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken);
            foreach (var output in document.RootElement.GetProperty("output").EnumerateArray())
            {
                if (!output.TryGetProperty("content", out var items))
                {
                    continue;
                }
                foreach (var item in items.EnumerateArray())
                {
                    if (item.TryGetProperty("type", out var type) && type.GetString() == "output_text" &&
                        item.TryGetProperty("text", out var text) && text.GetString() is { } value)
                    {
                        return value;
                    }
                }
            }

            throw new JsonException("The Responses API result did not contain output text.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw OpenAiErrorMapper.NetworkUnavailable();
        }
        catch (HttpRequestException)
        {
            throw OpenAiErrorMapper.NetworkUnavailable();
        }
        catch (IOException)
        {
            throw OpenAiErrorMapper.NetworkUnavailable();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw OpenAiErrorMapper.InvalidResponse();
        }
    }

    private static void ValidateTranscript(Transcript transcript)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        if (transcript.SchemaVersion != 1 || transcript.Segments is null || transcript.Segments.Count == 0)
        {
            throw new ArgumentException("A version 1 timestamped transcript is required.", nameof(transcript));
        }
        foreach (var segment in transcript.Segments)
        {
            if (segment is null || segment.StartMilliseconds < 0 ||
                segment.EndMilliseconds < segment.StartMilliseconds || string.IsNullOrWhiteSpace(segment.Text))
            {
                throw new ArgumentException("The transcript contains an invalid segment.", nameof(transcript));
            }
        }
    }

    private static void ValidateGuide(ClassStudyGuide guide)
    {
        if (guide.SchemaVersion != StudyPackageValidator.SupportedSchemaVersion || guide.Topics is null)
        {
            throw new InvalidDataException("The class study guide has an invalid schema.");
        }
        foreach (var topic in guide.Topics)
        {
            if (topic is null || string.IsNullOrWhiteSpace(topic.Topic) || topic.Contributions is null ||
                topic.Contributions.Any(string.IsNullOrWhiteSpace))
            {
                throw new InvalidDataException("The class study guide has invalid content.");
            }
        }
    }

    private static void ValidateLectureJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = RequireObject(document.RootElement);
        RequireProperties(root,
            "schemaVersion", "lectureTitle", "lectureDate", "shortSummary", "noteSections",
            "keyTerms", "assignments", "reviewQuestions", "studyGuideContributions");

        foreach (var section in RequireArray(root, "noteSections").EnumerateArray())
        {
            var value = RequireObject(section);
            RequireProperties(value, "heading", "body", "timestampReferences");
            ValidateTimestampReferences(RequireArray(value, "timestampReferences"));
        }

        foreach (var keyTerm in RequireArray(root, "keyTerms").EnumerateArray())
        {
            var value = RequireObject(keyTerm);
            RequireProperties(value, "term", "definition", "timestampReferences");
            ValidateTimestampReferences(RequireArray(value, "timestampReferences"));
        }

        foreach (var assignment in RequireArray(root, "assignments").EnumerateArray())
        {
            var value = RequireObject(assignment);
            RequireProperties(value,
                "description", "dueDateText", "normalizedDueDate", "confidence", "sourceTimestamp");
            ValidateTimestamp(RequireObject(RequiredProperty(value, "sourceTimestamp")));
        }

        foreach (var question in RequireArray(root, "reviewQuestions").EnumerateArray())
        {
            RequireProperties(RequireObject(question), "question", "suggestedAnswer", "supportingSection");
        }

        ValidateContributions(RequireArray(root, "studyGuideContributions"));
    }

    private static void ValidateGuideJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = RequireObject(document.RootElement);
        RequireProperties(root, "schemaVersion", "topics");
        ValidateContributions(RequireArray(root, "topics"));
    }

    private static void ValidateContributions(JsonElement contributions)
    {
        foreach (var contribution in contributions.EnumerateArray())
        {
            var value = RequireObject(contribution);
            RequireProperties(value, "topic", "contributions");
            RequireArray(value, "contributions");
        }
    }

    private static void ValidateTimestampReferences(JsonElement references)
    {
        foreach (var reference in references.EnumerateArray())
        {
            ValidateTimestamp(RequireObject(reference));
        }
    }

    private static void ValidateTimestamp(JsonElement timestamp) =>
        RequireProperties(timestamp, "startMilliseconds", "endMilliseconds");

    private static JsonElement RequireObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The structured OpenAI response is missing required members.");
        }
        return value;
    }

    private static JsonElement RequireArray(JsonElement value, string name)
    {
        var property = RequiredProperty(value, name);
        if (property.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The structured OpenAI response is missing required members.");
        }
        return property;
    }

    private static JsonElement RequiredProperty(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property))
        {
            throw new InvalidDataException("The structured OpenAI response is missing required members.");
        }
        return property;
    }

    private static void RequireProperties(JsonElement value, params string[] names)
    {
        foreach (var name in names)
        {
            RequiredProperty(value, name);
        }
    }

    private static JsonNode LectureSchema() => JsonNode.Parse(
        LectureSchemaTemplate
            .Replace("\"__TIMESTAMP_SCHEMA__\"", TimestampSchema, StringComparison.Ordinal)
            .Replace("\"__CONTRIBUTION_SCHEMA__\"", ContributionSchema, StringComparison.Ordinal))!.DeepClone();

    private static JsonNode GuideSchema() => JsonNode.Parse(
        GuideSchemaTemplate.Replace(
            "\"__CONTRIBUTION_SCHEMA__\"",
            ContributionSchema,
            StringComparison.Ordinal))!.DeepClone();

    private const string TimestampSchema = """
        {"type":"object","additionalProperties":false,"required":["startMilliseconds","endMilliseconds"],"properties":{"startMilliseconds":{"type":"integer","minimum":0},"endMilliseconds":{"type":"integer","minimum":0}}}
        """;

    private const string ContributionSchema = """
        {"type":"object","additionalProperties":false,"required":["topic","contributions"],"properties":{"topic":{"type":"string","minLength":1},"contributions":{"type":"array","items":{"type":"string","minLength":1}}}}
        """;

    private const string LectureSchemaTemplate = """
        {
          "type":"object","additionalProperties":false,
          "required":["schemaVersion","lectureTitle","lectureDate","shortSummary","noteSections","keyTerms","assignments","reviewQuestions","studyGuideContributions"],
          "properties":{
            "schemaVersion":{"type":"integer","const":1},
            "lectureTitle":{"type":"string","minLength":1},
            "lectureDate":{"type":"string","format":"date"},
            "shortSummary":{"type":"string","minLength":1},
            "noteSections":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["heading","body","timestampReferences"],"properties":{"heading":{"type":"string","minLength":1},"body":{"type":"string","minLength":1},"timestampReferences":{"type":"array","items":"__TIMESTAMP_SCHEMA__"}}}},
            "keyTerms":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["term","definition","timestampReferences"],"properties":{"term":{"type":"string","minLength":1},"definition":{"type":"string","minLength":1},"timestampReferences":{"type":"array","items":"__TIMESTAMP_SCHEMA__"}}}},
            "assignments":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["description","dueDateText","normalizedDueDate","confidence","sourceTimestamp"],"properties":{"description":{"type":"string","minLength":1},"dueDateText":{"type":"string","minLength":1},"normalizedDueDate":{"anyOf":[{"type":"string","format":"date"},{"type":"null"}]},"confidence":{"type":"number","minimum":0,"maximum":1},"sourceTimestamp":"__TIMESTAMP_SCHEMA__"}}},
            "reviewQuestions":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["question","suggestedAnswer","supportingSection"],"properties":{"question":{"type":"string","minLength":1},"suggestedAnswer":{"type":"string","minLength":1},"supportingSection":{"type":"string","minLength":1}}}},
            "studyGuideContributions":{"type":"array","items":"__CONTRIBUTION_SCHEMA__"}
          }
        }
        """;

    private const string GuideSchemaTemplate = """
        {
          "type":"object","additionalProperties":false,
          "required":["schemaVersion","topics"],
          "properties":{"schemaVersion":{"type":"integer","const":1},"topics":{"type":"array","items":"__CONTRIBUTION_SCHEMA__"}}
        }
        """;
}
