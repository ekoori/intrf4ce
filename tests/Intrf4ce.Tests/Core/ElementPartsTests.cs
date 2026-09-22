using System.Text.Json;
using Intrf4ce.Core.Model;
using Intrf4ce.Core.Narration;

namespace Intrf4ce.Tests.Core;

public class ElementPartsTests
{
    private static TableElement Table(string json) =>
        (TableElement)JsonSerializer.Deserialize<Element>(json, DeckJson.Options)!;

    [Fact]
    public void TableRowsAreNamedBySlugsOfTheFirstColumn()
    {
        var table = Table("""{"type":"table","columns":["Part","Load"],"rows":[["Knee joint",3],["ACL",1.5],["Knee joint",2],[null,0],[]]}""");

        Assert.Equal(["knee-joint", "acl", "knee-joint-2", "row4", "row5"], ElementParts.TableRows(table));
    }

    [Fact]
    public void TableRowsFollowTheKeyColumnWhenOneIsNamed()
    {
        var table = Table("""{"type":"table","columns":["Rank","City"],"key":"City","rows":[[1,"Zagreb"],[2,"Split"]]}""");

        Assert.Equal(["zagreb", "split"], ElementParts.TableRows(table));
    }

    [Fact]
    public void GeneratedGalleryIdsNeverTakeAnIdTheAgentChose()
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        GalleryItem[] items = [new() { Src = "a.jpg" }, new() { Src = "b.jpg", Id = "item1" }];

        Assert.Equal(["item1-2", "item1"], ElementParts.GalleryItems(items, used));
    }

    [Fact]
    public void FolderPhotosAreNamedAfterTheirFiles()
    {
        var used = new HashSet<string>(StringComparer.Ordinal);

        Assert.Equal("img-2031", ElementParts.Photo("IMG_2031.JPG", used));
        Assert.Equal("img-2031-2", ElementParts.Photo("img 2031.png", used));
    }

    [Fact]
    public void CuesOnTableRowsAreCheckedBeforeTheSlideIsDrawn()
    {
        var slide = new Slide
        {
            Id = "s1",
            Elements = [Table("""{"type":"table","id":"t","columns":["Part"],"rows":[["Knee joint"]]}""")],
            Narration = "[[focus t.knee-joint]] This one is fine. [[focus t.elbow]] This one is not.",
        };

        var issues = SpecValidator.CheckCueTargets(slide, CueParser.Parse(slide.Narration)).ToList();

        var issue = Assert.Single(issues);
        Assert.Contains("'t' has no part 'elbow'", issue.Message, StringComparison.Ordinal);
        Assert.Contains("knee-joint", issue.Message, StringComparison.Ordinal);
    }
}
