using System.Numerics;
using SmContent.Extras.Musou;
using SmFormats.Bod;
using Xunit;

namespace SmFormats.Tests.Extras.Musou;

/// <summary>The drop pod's rig read from an object file's tree (<see cref="DropPodRig"/>; docs/drop-pod-assault.md): hand-built trees only, no original bytes.</summary>
public class DropPodRigTests
{
    static BodString S(string text) => new(0, text);
    static BodField F(string name, BodValue value) => new(S(name), value);
    static BodArray Vec(float x, float y, float z) => new([new BodFloat(x), new BodFloat(y), new BodFloat(z)]);
    static BodList Kids(params BodValue[] nodes) => new(0, nodes.Select(n => new BodListItem(BodNull.Instance, BodNull.Instance, n)).ToList());
    static BodStruct Node(string name, Vector3 pos, Vector3 rot, params BodValue[] children) =>
        new(S("Node3D"), [F("Name", S(name)), F("Position", Vec(pos.X, pos.Y, pos.Z)), F("Rotation", Vec(rot.X, rot.Y, rot.Z)), F("Scale", Vec(1, 1, 1)), F("Children", Kids(children))]);
    static BodStruct Visual(string node, int block) => new(S("StaticMeshVisual"), [F("RefNode", S(node)), F("MeshName", S("DropPod")), F("MeshID", new BodInt(block))]);

    static BodStruct Pod(params BodValue[] rigChildren) => new(S("Object3D"),
    [
        F("Skeleton", new BodRef(0, new BodStruct(S("Skeleton3D"), [F("Name", S("DropPod")), F("Position", Vec(0, 0, 0)), F("Rotation", Vec(0, 0, 0)), F("Scale", Vec(1, 1, 1)),
            F("Children", Kids(Node("RIG_World", Vector3.Zero, Vector3.Zero, Node("RIG_Root", Vector3.Zero, Vector3.Zero, rigChildren))))]))),
        F("Visuals", Kids(Visual("geo_DP_Door01_LODH", 0), Visual("geo_DP_Door02_LODH", 4), new BodStruct(S("StaticMeshVisual"), [F("MeshName", S("DropPod")), F("MeshID", new BodInt(5))]))),
    ]);

    [Fact]
    public void A_door_block_is_posed_by_its_rig_node_and_the_mesh_node_under_it_and_hinges_on_the_ground_ring()
    {
        var doors = DropPodRig.From(Pod(
            Node("RIG_Door01", new Vector3(0f, -2f, 1f), Vector3.Zero, Node("geo_DP_Door01_LODH", new Vector3(0f, 0f, 0.5f), Vector3.Zero)),
            Node("RIG_Door02", new Vector3(1f, 0f, 0.2f), new Vector3(90f, 0f, 0f), Node("geo_DP_Door02_LODH", new Vector3(0f, 1f, 0f), Vector3.Zero))));
        Assert.Equal([0, 4], doors.Select(d => d.Block));
        var first = doors[0];
        Assert.Equal(new Vector3(0f, -2f, 1.5f), first.Closed.Translation);
        Assert.Equal(new Vector3(0f, -2f, 0f), first.Hinge);
        Assert.Equal(new Vector3(0f, -1f, 0f), first.Outward);
        // A turn of 90 degrees about X (applied first) stands the node's own +Y on +Z: the mesh node's offset of 1 along Y becomes 1 up.
        var second = doors[1];
        Assert.True(Vector3.Distance(new Vector3(1f, 0f, 1.2f), second.Closed.Translation) < 1e-4f, second.Closed.Translation.ToString());
        Assert.Equal(new Vector3(1f, 0f, 0f), second.Outward);
        Assert.Equal(new Vector3(1f, 0f, 0f), second.Hinge);
    }

    [Fact]
    public void Visuals_without_a_door_rig_and_unknown_nodes_give_no_door()
    {
        var none = DropPodRig.From(Pod(Node("geo_DP_Door01_LODH", Vector3.Zero, Vector3.Zero)));   // not under a RIG_Door node
        Assert.Empty(none);
        Assert.Empty(DropPodRig.From(Pod()));   // the visuals name nodes that are not there
        Assert.Empty(DropPodRig.From(new BodNull()));
    }
}
