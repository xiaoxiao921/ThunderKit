﻿using PassivePicasso.ThunderKit.Core.Data;
using PassivePicasso.ThunderKit.Core.Editor;
using PassivePicasso.ThunderKit.Core.Manifests;
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using static System.IO.Path;

namespace PassivePicasso.ThunderKit.Core.Pipelines
{
    public class PathReference : ComposableObject
    {
        [MenuItem(Constants.ThunderKitContextRoot + nameof(PathReference), false, priority = Constants.ThunderKitMenuPriority)]
        public static void CreateOutput() => ScriptableHelper.SelectNewAsset<PathReference>();

        private static Regex referenceIdentifier = new Regex("\\%(.*?)\\%");
        public static string ResolvePath(string input, Manifest manifest, Pipeline pipeline)
        {
            var pathReferences = FindObjectsOfType<PathReference>()
                                 .Union(Resources.FindObjectsOfTypeAll<PathReference>())
                                 .ToDictionary(k => k.name);

            Match match = referenceIdentifier.Match(input);
            while (match != null && !string.IsNullOrEmpty(match.Value))
            {
                string matchValue = match.Value.Trim('%');
                string replacement = string.Empty;
                switch (matchValue)
                {
                    case "GamePath":
                        replacement = ThunderKitSettings.GetOrCreateSettings().GamePath;
                        break;
                    case "GameExecutable":
                        replacement = Combine(ThunderKitSettings.GetOrCreateSettings().GamePath, ThunderKitSettings.GetOrCreateSettings().GameExecutable);
                        break;
                    case "PWD":
                        replacement = Directory.GetCurrentDirectory();
                        break;
                    default:
                        replacement = pathReferences[matchValue].GetPath(manifest, pipeline);
                        break;
                }
                input = input.Replace(match.Value, replacement);
                match = match.NextMatch();
            }

            return input;
        }


        public override Type ElementType => typeof(PathComponent);

        public override bool SupportsType(Type type) => ElementType.IsAssignableFrom(type);

        public string GetPath(Manifests.Manifest manifest, Pipeline pipeline)
        {
            return Data.OfType<PathComponent>().Select(d => d.GetPath(this, manifest, pipeline)).Aggregate(Combine);
        }

        public override string ElementTemplate => $@"
using PassivePicasso.ThunderKit.Core.Pipelines;
using PassivePicasso.ThunderKit.Core.Manifests;

namespace {{0}}
{{{{
    public class {{1}} : PathComponent
    {{{{
        public override string GetPath({nameof(PathReference)} output, Manifest manifest, Pipeline pipeline)
        {{{{
            return base.GetPath(output, manifest, pipeline);
        }}}}
    }}}}
}}}}
";
    }
}