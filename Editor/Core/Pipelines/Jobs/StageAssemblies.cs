﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ThunderKit.Core.Attributes;
using ThunderKit.Core.Manifests.Datums;
using ThunderKit.Core.Paths;
using UnityEditor;
using System.Threading.Tasks;
using UnityEditor.Compilation;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Networking;
using System.Text;
using System.Reflection;

namespace ThunderKit.Core.Pipelines.Jobs
{
    [PipelineSupport(typeof(Pipeline)), ManifestProcessor]
    public class StageAssemblies : PipelineJob
    {
        public static readonly HashSet<AssemblyBuilder> BuildStatus = new HashSet<AssemblyBuilder>();
        static string Combine(params string[] component) => Path.Combine(component).Replace('\\', '/');
#pragma warning disable CS0649 

        [Serializable]
        struct AsmDef
        {
            public string name;
            public bool allowUnsafeCode;
            public bool overrideReferences;
            public bool autoReferenced;
            public string[] optionalUnityReferences;
            public string[] includePlatforms;
            public string[] excludePlatforms;
            public string[] precompiledReferences;
            public string[] defineConstraints;
        }

        struct AsmBuildData : IEquatable<AsmBuildData>
        {
            public string Directory;
            public string AssemblyDefinition;
            public string Output;
            public string[] Scripts;

            public override bool Equals(object obj)
            {
                return obj is AsmBuildData data && Equals(data);
            }

            public bool Equals(AsmBuildData other)
            {
                return Directory == other.Directory &&
                       AssemblyDefinition == other.AssemblyDefinition;
            }

            public override int GetHashCode()
            {
                int hashCode = -815763736;
                hashCode = hashCode * -1521134295 + EqualityComparer<string>.Default.GetHashCode(Directory);
                hashCode = hashCode * -1521134295 + EqualityComparer<string>.Default.GetHashCode(AssemblyDefinition);
                return hashCode;
            }

            public static bool operator ==(AsmBuildData left, AsmBuildData right)
            {
                return left.Equals(right);
            }

            public static bool operator !=(AsmBuildData left, AsmBuildData right)
            {
                return !(left == right);
            }
        }
#pragma warning restore CS0649 

        public bool stageDebugDatabases;
        [PathReferenceResolver, Tooltip("Location where built assemblies will be cached before being staged")]
        public string assemblyArtifactPath = "<AssemblyStaging>";
        public BuildTarget buildTarget = BuildTarget.StandaloneWindows;
        public BuildTargetGroup buildTargetGroup = BuildTargetGroup.Standalone;


        public sealed override async Task Execute(Pipeline pipeline)
        {
            var resolvedArtifactPath = PathReference.ResolvePath(assemblyArtifactPath, pipeline, this);
            Directory.CreateDirectory(resolvedArtifactPath);

            var assemblies = CompilationPipeline.GetAssemblies();
            var definitionDatums = pipeline.Manifest.Data.OfType<AssemblyDefinitions>().ToArray();
            if (!definitionDatums.Any())
            {
                var scriptPath = UnityWebRequest.EscapeURL(AssetDatabase.GetAssetPath(MonoScript.FromScriptableObject(this)));
                pipeline.Log(LogLevel.Warning, $"No AssemblyDefinitions found, skipping");
                return;
            }

            for (int i = 0; i < definitionDatums.Length; i++)
            {
                var datum = definitionDatums[i];
                if (!datum) continue;
                var hasUnassignedDefinition = datum.definitions.Any(def => !(bool)(def));
                if (hasUnassignedDefinition)
                    pipeline.Log(LogLevel.Warning, $"AssemblyDefinitions with unassigned definition at index {i}");
            }

            var deserializedAsmDefs = definitionDatums.SelectMany(datum =>
                datum.definitions.Where(asmDefAsset => asmDefAsset)
                                .Select(asmDefAsset =>
                                        (asmDef: JsonUtility.FromJson<AsmDef>(asmDefAsset.text),
                                         asmDefAsset: asmDefAsset,
                                         datum: datum)
                ));

            var definitions = deserializedAsmDefs.Select(dataSet =>
                    (asm: assemblies.FirstOrDefault(asm => dataSet.asmDef.name == asm.name),
                     asmDefAsset: dataSet.asmDefAsset,
                     asmDef: dataSet.asmDef,
                     datum: dataSet.datum)
                ).Where(def => def.asm != null)
                .ToArray();

            MethodInfo uNetProcessMethod = UNetWeaverHelper.GetProcessMethod();

            foreach (var definition in definitions)
            {
                var assemblyName = $"{definition.asm.name}.dll";
                var targetName = Path.GetFileNameWithoutExtension(definition.asm.name);
                var assemblyOutputPath = Combine(resolvedArtifactPath, assemblyName);
                var builder = new AssemblyBuilder(assemblyOutputPath, definition.asm.sourceFiles)
                {
                    additionalReferences = definition.asm.allReferences,
                };
                builder.excludeReferences = builder.defaultReferences.Where(rf => rf.Contains(assemblyName)).ToArray();
                builder.buildTargetGroup = buildTargetGroup;

                var index = pipeline.ManifestIndex;
                void OnBuildStarted(string path) => pipeline.Log(LogLevel.Information, $"Building : {path}");
                void OnBuildFinished(string path, CompilerMessage[] messages)
                {
                    BuildStatus.Remove(builder);
                    if (messages.Any())
                        foreach (var message in messages.OrderBy(msg => msg.type))
                        {
                            var extraData = $"{message.file} ({message.line}:{message.column})";
                            switch (message.type)
                            {
                                case CompilerMessageType.Error:
                                    pipeline.Log(LogLevel.Error, message.message, extraData);
                                    break;
                                case CompilerMessageType.Warning:
                                    pipeline.Log(LogLevel.Warning, message.message, extraData);
                                    break;
                            }
                        }

                    pipeline.Log(LogLevel.Information, $"Build Completed: ``` {path} ```");

                    var prevIndex = pipeline.ManifestIndex;
                    pipeline.ManifestIndex = index;
                    var resolvedPaths = definition.datum.StagingPaths
                        .Select(p => PathReference.ResolvePath(p, pipeline, this)).ToArray();

                    foreach (var outputPath in resolvedPaths)
                    {
                        Directory.CreateDirectory(outputPath);
                        if (stageDebugDatabases)
                            CopyFiles(pipeline, resolvedArtifactPath, outputPath, $"{targetName}*.pdb", $"{targetName}*.mdb", assemblyName);
                        else
                            CopyFiles(pipeline, resolvedArtifactPath, outputPath, assemblyName);

                        TryUNetWeave(uNetProcessMethod, definition, assemblyName, outputPath);
                    }
                    pipeline.ManifestIndex = prevIndex;
                }
                builder.buildTarget = buildTarget;
                builder.buildFinished += OnBuildFinished;
                builder.buildStarted += OnBuildStarted;

                if (File.Exists(assemblyOutputPath))
                    File.Delete(assemblyOutputPath);
                BuildStatus.Add(builder);
                builder.Build();
            }

            while (EditorApplication.isCompiling)
            {
                await Task.Delay(100);
            }
        }

        void CopyFiles(Pipeline pipeline, string sourcePath, string outputPath, params string[] patterns)
        {
            var builder = new StringBuilder("Assembly Files");
            Directory.CreateDirectory(outputPath);
            var targetFiles = (from pattern in patterns
                               from file in Directory.GetFiles(sourcePath, pattern, SearchOption.AllDirectories)
                               select file).ToArray();

            builder.AppendLine();
            foreach (var source in targetFiles)
            {
                var fileName = Path.GetFileName(source);
                string destination = Combine(outputPath, fileName);
                File.Copy(source, destination, true);
                builder.AppendLine($"From: {source}");
                builder.AppendLine($"  To: {destination}");
                builder.AppendLine();
            }

            pipeline.Log(LogLevel.Information, $"staging ``` {sourcePath} ``` in ``` {outputPath} ```\r\n", builder.ToString());
        }

        private static void TryUNetWeave(MethodInfo uNetProcessMethod,
            (UnityEditor.Compilation.Assembly asm, AssemblyDefinitionAsset asmDefAsset, AsmDef asmDef, AssemblyDefinitions datum) definition,
            string assemblyName, string outputPath)
        {
            if (uNetProcessMethod != null)
            {
                var enginePath = InternalEditorUtility.GetEngineCoreModuleAssemblyPath();
                var networkingDllPath = Path.Combine(EditorApplication.applicationContentsPath, "UnityExtensions", "Unity", "Networking", "UnityEngine.Networking.dll");
                if (!File.Exists(networkingDllPath))
                    networkingDllPath = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Library", "ScriptAssemblies", "com.unity.multiplayer-hlapi.Runtime.dll");
                var assemblyToWeavePath = Path.Combine(outputPath, assemblyName);

                // Before 2019
                if (uNetProcessMethod.GetParameters().Length == 8)
                {
                    uNetProcessMethod.Invoke(null,
                    new object[]
                    {
                        enginePath,
                        networkingDllPath,
                        outputPath,
                        new[] { assemblyToWeavePath },
                        definition.asm.allReferences,
                        null,
                        (Action<string>)Debug.LogWarning,
                        (Action<string>)Debug.LogError
                    });
                }
                // After 2019
                else if (uNetProcessMethod.GetParameters().Length == 7)
                {
                    uNetProcessMethod.Invoke(null,
                    new object[]
                    {
                        enginePath,
                        networkingDllPath,
                        outputPath,
                        new[] { assemblyToWeavePath },
                        definition.asm.allReferences,
                        (Action<string>)Debug.LogWarning,
                        (Action<string>)Debug.LogError
                    });
                }
            }
        }
    }
}