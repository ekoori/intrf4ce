using System.Text.Json;
using Intrf4ce.Core.Model;

namespace Intrf4ce.Tests.Core;

public class SpecValidatorTests
{
    private static Slide Knee(params Callout[] callouts) => new()
    {
        Id = "knee-slide",
        Elements = [new ImageElement { Id = "knee", Src = "knee.png", Callouts = callouts }],
    };

    [Fact]
    public void AValidSlideHasNoIssues()
    {
        var slide = Knee(new Callout { Id = "acl", At = [0.5, 0.4], Label = "ACL" }) with
        {
            Narration = "The ACL. [[focus knee.acl]] Here.",
        };

        Assert.Empty(SpecValidator.Validate(slide));
    }

    [Fact]
    public void CoordinatesOutsideZeroToOneNameThePathAndTheRule()
    {
        var issues = SpecValidator.Validate(Knee(new Callout { Id = "acl", At = [470, 440], Label = "ACL" }));

        var issue = Assert.Single(issues);
        Assert.Equal(IssueSeverity.Error, issue.Severity);
        Assert.Equal("elements[0].callouts[0].at", issue.Path);
        Assert.Contains("0..1", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACalloutNeedsExactlyOneOfAtOrBox()
    {
        var neither = SpecValidator.Validate(Knee(new Callout { Id = "a", Label = "A" }));
        var both = SpecValidator.Validate(Knee(new Callout { Id = "a", Label = "A", At = [0.1, 0.1], Box = [0.1, 0.1, 0.2, 0.2] }));

        Assert.Contains(neither, i => i.Message.Contains("exactly one", StringComparison.Ordinal));
        Assert.Contains(both, i => i.Message.Contains("exactly one", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnknownLayoutListsTheKnownOnes()
    {
        var issues = SpecValidator.Validate(new Slide { Id = "s", Layout = "columns" });

        Assert.Contains(issues, i => i.Path == "layout" && i.Message.Contains("split", StringComparison.Ordinal));
    }

    [Fact]
    public void ACueAtAMissingPartIsAWarningThatListsTheParts()
    {
        var slide = Knee(new Callout { Id = "acl", At = [0.5, 0.4], Label = "ACL" }) with
        {
            Narration = "[[focus knee.pcl]] The PCL.",
        };

        var issue = Assert.Single(SpecValidator.Validate(slide));
        Assert.Equal(IssueSeverity.Warning, issue.Severity);
        Assert.Contains("parts: acl", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ABrokenCueIsAnError()
    {
        var slide = Knee() with { Narration = "Look [[zoom knee 0.1 0.1]] here." };

        Assert.Contains(SpecValidator.Validate(slide), i => i.Severity == IssueSeverity.Error && i.Path == "narration");
    }

    [Fact]
    public void ChartFieldsMustExistInTheData()
    {
        using var data = JsonDocument.Parse("""[{"month":"Jan","sales":4}]""");
        var slide = new Slide
        {
            Id = "s",
            Elements = [new ChartElement { Id = "c", Kind = "bar", Data = data.RootElement.Clone(), X = "month", Y = "revenue" }],
        };

        var issue = Assert.Single(SpecValidator.Validate(slide));
        Assert.Equal("elements[0].y", issue.Path);
        Assert.Contains("month, sales", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingIdsAreGeneratedFromTypeAndTitle()
    {
        var slide = new Slide
        {
            Title = "Ligaments of the Knee",
            Elements = [new MarkdownElement { Text = "a" }, new MarkdownElement { Text = "b" }, new ImageElement { Id = "markdown", Src = "x" }],
        };

        var filled = SpecValidator.WithIds(slide, id => id == "ligaments-of-the-knee");

        Assert.Equal("ligaments-of-the-knee-2", filled.Id);
        Assert.Equal(["markdown2", "markdown3", "markdown"], filled.Elements.Select(e => e.Id));
        Assert.Empty(SpecValidator.Validate(filled));
    }

    [Fact]
    public void DuplicateElementIdsAreRejected()
    {
        var slide = new Slide
        {
            Id = "s",
            Elements = [new MarkdownElement { Id = "a", Text = "x" }, new MarkdownElement { Id = "a", Text = "y" }],
        };

        Assert.Contains(SpecValidator.Validate(slide), i => i.Path == "elements[1].id");
    }

    [Fact]
    public void QuizAnswersMustIndexOptions()
    {
        var slide = new Slide
        {
            Id = "s",
            Elements = [new QuizElement { Id = "q", Question = "?", Options = ["a", "b"], Answer = [2] }],
        };

        Assert.Contains(SpecValidator.Validate(slide), i => i.Path == "elements[0].answer");
    }
}
