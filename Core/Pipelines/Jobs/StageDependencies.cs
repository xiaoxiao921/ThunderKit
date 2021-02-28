using System.IO;
using System.Linq;
using ThunderKit.Core.Attributes;
using ThunderKit.Core.Manifests.Datum;
using ThunderKit.Core.Paths;
using UnityEditor;

namespace ThunderKit.Core.Pipelines.Jobs
{
    [PipelineSupport(typeof(Pipeline)), RequiresManifestDatumType(typeof(ManifestIdentity))]
    public class StageDependencies : PipelineJob
    {
        [PathReferenceResolver]
        public string StagingPath;
        public Manifests.Manifest[] ExcludedManifests;

        public override void Execute(Pipeline pipeline)
        {
            var manifestIdentities = pipeline.Datums.OfType<ManifestIdentity>();
            var dependencies = manifestIdentities.SelectMany(tm => tm.Dependencies).Distinct().ToList();

            foreach (var manifest in pipeline.manifests.Except(ExcludedManifests))
                foreach (var manifestIdentity in manifest.Data.OfType<ManifestIdentity>())
                {
                    if (AssetDatabase.GetAssetPath(manifest).StartsWith("Assets")) continue;
                    var dependencyPath = Path.Combine("Packages", manifestIdentity.Name);
                    CopyFilesRecursively(dependencyPath, StagingPath.Resolve(pipeline, this));

                }
        }

        public static void CopyFilesRecursively(string source, string destination)
        {
            foreach (string dirPath in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(dirPath.Replace("Packages", destination));

            foreach (string filePath in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                if (Path.GetExtension(filePath).Equals(".meta")) continue;

                string destFileName = filePath.Replace("Packages", destination);
                Directory.CreateDirectory(Path.GetDirectoryName(destFileName));
                File.Copy(filePath, destFileName, true);
            }
        }
    }
}
