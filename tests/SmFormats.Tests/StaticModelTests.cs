using System.Numerics;
using SmContent;
using SmFormats.Bod;
using SmFormats.Dcm;
using SmFormats.Obpk;

namespace SmFormats.Tests;

public class StaticModelTests
{
    [Fact]
    public void Resolves_mesh_materials_and_diffuse_textures_within_one_archive()
    {
        var mesh = SyntheticDcmWriter.Build([new SyntheticDcmWriter.Block(
            [new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 1, 0)], [0, 1, 2, 0, 2, 3],
            [new("stone", 0, 3, 0, 3), new("metal", 3, 3, 0, 4)])]);
        byte[] fakeDds = [0x44, 0x44, 0x53, 0x20, 1, 2, 3];
        var archive = SyntheticObpkWriter.Build(
        [
            new("dcm", "statue", mesh),
            new("bmat", "stone", SyntheticBodWriter.Material("stone", "diff_norm", [("DiffuseMap", "stone_dif")])),
            new("bmat", "metal", SyntheticBodWriter.Material("metal", "diff_norm", [("NormalMap", "metal_nrm")])),
            new("dds", "stone_dif", fakeDds),
        ]);
        string path = Path.Combine(Path.GetTempPath(), "sm-staticmodel-" + Guid.NewGuid().ToString("N") + ".oppc");
        try
        {
            File.WriteAllBytes(path, archive);
            var notes = new List<string>();
            var model = StaticModel.Load(path, "statue", notes);

            Assert.Equal(2, model.Surfaces.Count);
            Assert.Equal("stone_dif", model.Surfaces[0].DiffuseName);
            Assert.Equal(fakeDds, model.Surfaces[0].DiffuseDds);
            Assert.Null(model.Surfaces[1].DiffuseDds);
            Assert.Contains(notes, n => n.Contains("'metal' has no DiffuseMap"));
            Assert.Equal(new Vector3(1, 1, 0), model.Mesh.Blocks[0].Positions[2]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Missing_mesh_is_reported()
    {
        var archive = SyntheticObpkWriter.Build([new("bmat", "stone", SyntheticBodWriter.Material("stone", "x", []))]);
        string path = Path.Combine(Path.GetTempPath(), "sm-staticmodel-" + Guid.NewGuid().ToString("N") + ".oppc");
        try
        {
            File.WriteAllBytes(path, archive);
            Assert.Throws<FileNotFoundException>(() => StaticModel.Load(path, "statue"));
        }
        finally { File.Delete(path); }
    }
}
