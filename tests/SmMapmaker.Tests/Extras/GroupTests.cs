using SmMapmaker;

namespace SmMapmaker.Tests;

/// <summary>Editing groups (named sets of objects, no transform and no effect in the game) and set-parent as the grouping-by-nesting path.</summary>
public class GroupTests
{
    static MapSession Five(Lab lab)
    {
        var s = lab.Session();
        for (int i = 1; i <= 5; i++)
            Assert.True(s.Apply(Lab.Op($$$"""{"op":"place","asset":{"source":"sm1","map":"mp_fake","definition":"def_wall"},"name":"p{{{i}}}","transform":{"position":[{{{i}}},0,0]}}""")).Ok);
        return s;
    }

    static OpResult Run(MapSession s, string json) => s.Apply(Lab.Op(json));

    [Fact]
    public void CreateRenameAddRemoveUngroupDelete()
    {
        using var lab = new Lab();
        var s = Five(lab);
        var made = Run(s, """{"op":"group","ids":["o1","o2"],"name":"Walls"}""");
        Assert.True(made.Ok);
        Assert.Equal(["g1"], made.Created);
        Assert.Equal(["o1", "o2"], s.Document.GetGroup("g1").Members);
        Assert.Equal("Walls", s.Document.GetGroup("g1").Name);
        Assert.Equal("Group 2", s.Document.GetGroup(Run(s, """{"op":"group","ids":["o5"]}""").Created[0]).Name);

        Assert.True(Run(s, """{"op":"group-rename","id":"g1","name":"Wall set"}""").Ok);
        Assert.Equal("Wall set", s.Document.GetGroup("g1").Name);
        Assert.Equal("bad-op", Run(s, """{"op":"group-rename","id":"g1","name":""}""").Errors[0].Code);

        Assert.True(Run(s, """{"op":"group-add","group":"g1","ids":["o3"]}""").Ok);
        Assert.Equal(["o1", "o2", "o3"], s.Document.GetGroup("g1").Members);
        Assert.True(Run(s, """{"op":"group-add","group":"g1","ids":["o5"]}""").Ok);   // leaves g2, which stays (now empty)
        Assert.Empty(s.Document.GetGroup("g2").Members);

        Assert.True(Run(s, """{"op":"group-remove","ids":["o3"]}""").Ok);
        Assert.Null(s.Document.GroupOf("o3"));
        Assert.Equal(5, s.Document.Objects.Count);

        Assert.True(Run(s, """{"op":"ungroup","id":"g1"}""").Ok);
        Assert.Null(s.Document.FindGroup("g1"));
        Assert.Equal(5, s.Document.Objects.Count);
        Assert.True(Run(s, """{"op":"delete-group","id":"g2"}""").Ok);
        Assert.Empty(s.Document.Groups);
        Assert.Equal("unknown-group", Run(s, """{"op":"group-rename","id":"g9","name":"x"}""").Errors[0].Code);
        Assert.Equal("unknown-id", Run(s, """{"op":"group","ids":["o99"]}""").Errors[0].Code);
    }

    [Fact]
    public void GroupsNestAndExpandToTheirObjects()
    {
        using var lab = new Lab();
        var s = Five(lab);
        Run(s, """{"op":"group","ids":["o1","o2"],"name":"inner"}""");
        Assert.True(Run(s, """{"op":"group","ids":["g1","o3"],"name":"outer"}""").Ok);
        Assert.Equal(["o1", "o2", "o3"], s.Document.GroupObjects("g2"));
        Assert.Equal("g2", s.Document.GroupOf("g1")!.Id);
        Assert.True(s.Document.GroupContains("g2", "o1"));
        Assert.False(s.Document.GroupContains("g1", "o3"));

        Assert.Equal("group-cycle", Run(s, """{"op":"group-add","group":"g1","ids":["g2"]}""").Errors[0].Code);
        Assert.Equal("group-cycle", Run(s, """{"op":"group-add","group":"g1","ids":["g1"]}""").Errors[0].Code);

        // a group made from objects inside a group stays inside it
        Assert.True(Run(s, """{"op":"group","ids":["o1"],"name":"just one"}""").Ok);
        Assert.Equal("g1", s.Document.GroupOf("g3")!.Id);

        // ungrouping the middle group hands its members to the one around it
        Assert.True(Run(s, """{"op":"ungroup","id":"g1"}""").Ok);
        Assert.Equal("g2", s.Document.GroupOf("g3")!.Id);
        Assert.Equal("g2", s.Document.GroupOf("o2")!.Id);

        Assert.True(Run(s, """{"op":"delete-group","id":"g2"}""").Ok);   // removes nested groups too; objects stay
        Assert.Empty(s.Document.Groups);
        Assert.Equal(5, s.Document.Objects.Count);
    }

    [Fact]
    public void DeletingObjectsRemovesThemFromGroups()
    {
        using var lab = new Lab();
        var s = Five(lab);
        Run(s, """{"op":"group","ids":["o1","o2","o3"]}""");
        Assert.True(Run(s, """{"op":"delete","ids":["o2"]}""").Ok);
        Assert.Equal(["o1", "o3"], s.Document.GetGroup("g1").Members);
    }

    [Fact]
    public void GroupsSaveLoadAndUndoRedo()
    {
        using var lab = new Lab();
        var s = Five(lab);
        string before = s.Document.ToJson();
        Run(s, """{"op":"group","ids":["o1","o2"],"name":"inner"}""");
        Run(s, """{"op":"group","ids":["g1","o3"],"name":"outer"}""");
        var path = Path.Combine(lab.Dir, "g.smmap"); s.Save(path);
        var loaded = MapSession.Load(path, lab.Library);
        Assert.Equal(s.Document.ToJson(), loaded.Document.ToJson());
        Assert.Equal(["o1", "o2", "o3"], loaded.Document.GroupObjects("g2"));

        Assert.True(s.Undo().Ok); Assert.True(s.Undo().Ok);
        Assert.Empty(s.Document.Groups);
        Assert.Equal(before, s.Document.ToJson());
        Assert.True(s.Redo().Ok); Assert.True(s.Redo().Ok);
        Assert.Equal(2, s.Document.Groups.Count);
    }

    [Fact]
    public void OldFilesWithoutGroupsOpenAndWriteNoGroupsKey()
    {
        using var lab = new Lab();
        var s = Five(lab);
        var json = s.Document.ToJson();
        Assert.DoesNotContain("groups", json);
        var doc = MapDocument.Parse(json);
        Assert.Empty(doc.Groups);
        Assert.Equal(json, doc.ToJson());
    }

    [Fact]
    public void FilesWithStrayGroupEntriesAreRepairedNotRefused()
    {
        using var lab = new Lab();
        var s = Five(lab);
        var node = System.Text.Json.Nodes.JsonNode.Parse(s.Document.ToJson())!.AsObject();
        node["groups"] = System.Text.Json.Nodes.JsonNode.Parse("""
            [{"id":"g1","name":"a","members":["o1","o99","g2","o2"]},{"id":"g2","name":"b","members":["o2","g1","o3"]}]
            """);
        var doc = MapDocument.Parse(node.ToJsonString());
        Assert.Equal(["o1", "o2"], doc.GetGroup("g1").Members);        // o99 is gone; g2 inside g1 while g1 is inside g2 would be a cycle, so the first is dropped
        Assert.Equal(["g1", "o3"], doc.GetGroup("g2").Members);        // o2 already belongs to g1
        node["groups"] = System.Text.Json.Nodes.JsonNode.Parse("""[{"id":"x","name":"a","members":[]}]""");
        Assert.Equal("bad-file", Assert.Throws<MapException>(() => MapDocument.Parse(node.ToJsonString())).Code);
    }

    [Fact]
    public void ReparentingAnyObjectUnderAnyOtherIsSavedAndUndoable()
    {
        using var lab = new Lab();
        var s = Five(lab);
        Assert.True(Run(s, """{"op":"place","piece":{"kind":"kill-zone"},"name":"zone"}""").Ok);   // a different kind of object as the parent
        var world = s.Document.World("o2");
        Assert.True(Run(s, """{"op":"set-parent","id":"o2","parent":"o6"}""").Ok);
        Assert.Equal("o6", s.Document.Get("o2").Parent);
        Assert.True(TransformMath.Close(world, s.Document.World("o2"), 1e-4f));
        var path = Path.Combine(lab.Dir, "p.smmap"); s.Save(path);
        Assert.Equal("o6", MapSession.Load(path, lab.Library).Document.Get("o2").Parent);
        Assert.True(s.Undo().Ok);
        Assert.Null(s.Document.Get("o2").Parent);
    }
}
