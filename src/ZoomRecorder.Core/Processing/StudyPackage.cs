using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace ZoomRecorder.Core.Processing;

public sealed record TimestampReference(long StartMilliseconds, long EndMilliseconds);

public sealed record NoteSection(
    [property: JsonRequired] string Heading,
    [property: JsonRequired] string Body,
    [property: JsonRequired] IReadOnlyList<TimestampReference> TimestampReferences);

public sealed record KeyTerm(
    [property: JsonRequired] string Term,
    [property: JsonRequired] string Definition,
    [property: JsonRequired] IReadOnlyList<TimestampReference> TimestampReferences);

public sealed record StudyAssignment(
    [property: JsonRequired] string Description,
    [property: JsonRequired] string DueDateText,
    DateOnly? NormalizedDueDate,
    double Confidence,
    [property: JsonRequired] TimestampReference SourceTimestamp);

public sealed record ReviewQuestion(
    [property: JsonRequired] string Question,
    [property: JsonRequired] string SuggestedAnswer,
    [property: JsonRequired] string SupportingSection);

public sealed record StudyGuideContribution(
    [property: JsonRequired] string Topic,
    [property: JsonRequired] IReadOnlyList<string> Contributions);

public sealed record StudyPackage(
    int SchemaVersion,
    [property: JsonRequired] string LectureTitle,
    DateOnly LectureDate,
    [property: JsonRequired] string ShortSummary,
    [property: JsonRequired] IReadOnlyList<NoteSection> NoteSections,
    [property: JsonRequired] IReadOnlyList<KeyTerm> KeyTerms,
    [property: JsonRequired] IReadOnlyList<StudyAssignment> Assignments,
    [property: JsonRequired] IReadOnlyList<ReviewQuestion> ReviewQuestions,
    [property: JsonRequired] IReadOnlyList<StudyGuideContribution> StudyGuideContributions);

public sealed record ClassStudyGuide(
    int SchemaVersion,
    [property: JsonRequired] IReadOnlyList<StudyGuideContribution> Topics);

public static class StudyPackageValidator
{
    public const int SupportedSchemaVersion = 1;

    public static void Validate(StudyPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (package.SchemaVersion != SupportedSchemaVersion)
        {
            Reject($"Unsupported study package schema version {package.SchemaVersion}.");
        }

        Required(package.LectureTitle, nameof(package.LectureTitle));
        if (package.LectureDate == default)
        {
            Reject($"{nameof(package.LectureDate)} is required.");
        }

        Required(package.ShortSummary, nameof(package.ShortSummary));
        RequiredCollection(package.NoteSections, nameof(package.NoteSections));
        RequiredCollection(package.KeyTerms, nameof(package.KeyTerms));
        RequiredCollection(package.Assignments, nameof(package.Assignments));
        RequiredCollection(package.ReviewQuestions, nameof(package.ReviewQuestions));
        RequiredCollection(package.StudyGuideContributions, nameof(package.StudyGuideContributions));

        foreach (var section in package.NoteSections)
        {
            NotNull(section, nameof(package.NoteSections));
            Required(section.Heading, nameof(section.Heading));
            Required(section.Body, nameof(section.Body));
            ValidateReferences(section.TimestampReferences, nameof(section.TimestampReferences));
        }

        foreach (var keyTerm in package.KeyTerms)
        {
            NotNull(keyTerm, nameof(package.KeyTerms));
            Required(keyTerm.Term, nameof(keyTerm.Term));
            Required(keyTerm.Definition, nameof(keyTerm.Definition));
            ValidateReferences(keyTerm.TimestampReferences, nameof(keyTerm.TimestampReferences));
        }

        foreach (var assignment in package.Assignments)
        {
            NotNull(assignment, nameof(package.Assignments));
            Required(assignment.Description, nameof(assignment.Description));
            Required(assignment.DueDateText, nameof(assignment.DueDateText));
            if (!double.IsFinite(assignment.Confidence) || assignment.Confidence is < 0 or > 1)
            {
                Reject($"{nameof(assignment.Confidence)} must be between 0 and 1.");
            }

            ValidateReference(assignment.SourceTimestamp, nameof(assignment.SourceTimestamp));
        }

        foreach (var question in package.ReviewQuestions)
        {
            NotNull(question, nameof(package.ReviewQuestions));
            Required(question.Question, nameof(question.Question));
            Required(question.SuggestedAnswer, nameof(question.SuggestedAnswer));
            Required(question.SupportingSection, nameof(question.SupportingSection));
        }

        foreach (var contribution in package.StudyGuideContributions)
        {
            NotNull(contribution, nameof(package.StudyGuideContributions));
            Required(contribution.Topic, nameof(contribution.Topic));
            RequiredCollection(contribution.Contributions, nameof(contribution.Contributions));
            foreach (var text in contribution.Contributions)
            {
                Required(text, nameof(contribution.Contributions));
            }
        }
    }

    private static void ValidateReferences(IReadOnlyList<TimestampReference>? references, string name)
    {
        RequiredCollection(references, name);
        foreach (var reference in references)
        {
            ValidateReference(reference, name);
        }
    }

    private static void ValidateReference(TimestampReference? reference, string name)
    {
        NotNull(reference, name);
        if (reference.StartMilliseconds < 0 || reference.EndMilliseconds < reference.StartMilliseconds)
        {
            Reject($"{name} contains an invalid timestamp range.");
        }
    }

    private static void Required(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Reject($"{name} is required.");
        }
    }

    private static void RequiredCollection<T>([NotNull] IReadOnlyList<T>? collection, string name)
    {
        if (collection is null)
        {
            Reject($"{name} is required.");
        }
    }

    private static void NotNull<T>([NotNull] T? value, string name) where T : class
    {
        if (value is null)
        {
            Reject($"{name} cannot contain null values.");
        }
    }

    [DoesNotReturn]
    private static void Reject(string message) => throw new StudyPackageValidationException(message);
}

public sealed class StudyPackageValidationException(string message) : ArgumentException(message);
