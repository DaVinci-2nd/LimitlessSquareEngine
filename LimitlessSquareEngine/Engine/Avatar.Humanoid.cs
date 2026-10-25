using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using LimitlessSquareEngine;

namespace LimitlessSquareEngine.Engine
{
    public sealed class HumanoidBoneSpec
    {
        public string Name { get; init; } = "";

        public bool Required { get; init; }

        public string[] ParentCandidates { get; init; } = Array.Empty<string>();
    }

    internal static class HumanoidBoneSpecifications
    {
        // 全部人形骨骼规范
        public static readonly HumanoidBoneSpec[] All = BuildAll();

        public static readonly Dictionary<string, HumanoidBoneSpec> ByName = BuildByName();

        public static readonly string[] RequiredNames = All
            .Where(spec => spec.Required)
            .Select(spec => spec.Name)
            .ToArray();

        public static HumanoidBoneSpec? Find(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;

            return ByName.TryGetValue(name, out HumanoidBoneSpec? spec) ? spec : null;
        }

        static HumanoidBoneSpec[] BuildAll()
        {
            var specs = new List<HumanoidBoneSpec>(55);

            specs.Add(new HumanoidBoneSpec { Name = "hips", Required = true });

            specs.Add(new HumanoidBoneSpec
            {
                Name = "spine",
                Required = true,
                ParentCandidates = new[] { "hips" }
            });

            specs.Add(new HumanoidBoneSpec
            {
                Name = "chest",
                ParentCandidates = new[] { "spine" }
            });

            specs.Add(new HumanoidBoneSpec
            {
                Name = "upperChest",
                ParentCandidates = new[] { "chest" }
            });

            specs.Add(new HumanoidBoneSpec
            {
                Name = "neck",
                ParentCandidates = new[] { "upperChest", "chest", "spine" }
            });

            specs.Add(new HumanoidBoneSpec
            {
                Name = "head",
                Required = true,
                ParentCandidates = new[] { "neck", "upperChest", "chest", "spine" }
            });

            specs.Add(new HumanoidBoneSpec
            {
                Name = "leftEye",
                ParentCandidates = new[] { "head" }
            });

            specs.Add(new HumanoidBoneSpec
            {
                Name = "rightEye",
                ParentCandidates = new[] { "head" }
            });

            specs.Add(new HumanoidBoneSpec
            {
                Name = "jaw",
                ParentCandidates = new[] { "head" }
            });

            specs.Add(new HumanoidBoneSpec
            {
                Name = "leftUpperLeg",
                Required = true,
                ParentCandidates = new[] { "hips" }
            });

            specs.Add(new HumanoidBoneSpec
            {
                Name = "leftLowerLeg",
                Required = true,
                ParentCandidates = new[] { "leftUpperLeg" }
            });

            specs.Add(new HumanoidBoneSpec
            {
                Name = "leftFoot",
                Required = true,
                ParentCandidates = new[] { "leftLowerLeg" }
            });

            specs.Add(new HumanoidBoneSpec
            {
                Name = "leftToes",
                ParentCandidates = new[] { "leftFoot" }
            });

            specs.Add(new HumanoidBoneSpec
            {
                Name = "rightUpperLeg",
                Required = true,
                ParentCandidates = new[] { "hips" }
            });

            specs.Add(new HumanoidBoneSpec
            {
                Name = "rightLowerLeg",
                Required = true,
                ParentCandidates = new[] { "rightUpperLeg" }
            });

            specs.Add(new HumanoidBoneSpec
            {
                Name = "rightFoot",
                Required = true,
                ParentCandidates = new[] { "rightLowerLeg" }
            });

            specs.Add(new HumanoidBoneSpec
            {
                Name = "rightToes",
                ParentCandidates = new[] { "rightFoot" }
            });

            specs.Add(new HumanoidBoneSpec
            {
                Name = "leftShoulder",
                ParentCandidates = new[] { "upperChest", "chest", "spine" }
            });

            specs.Add(new HumanoidBoneSpec
            {
                Name = "leftUpperArm",
                Required = true,
                ParentCandidates = new[] { "leftShoulder", "upperChest", "chest", "spine" }
            });

            specs.Add(new HumanoidBoneSpec
            {
                Name = "leftLowerArm",
                Required = true,
                ParentCandidates = new[] { "leftUpperArm" }
            });

            specs.Add(new HumanoidBoneSpec
            {
                Name = "leftHand",
                Required = true,
                ParentCandidates = new[] { "leftLowerArm" }
            });

            specs.Add(new HumanoidBoneSpec
            {
                Name = "rightShoulder",
                ParentCandidates = new[] { "upperChest", "chest", "spine" }
            });

            specs.Add(new HumanoidBoneSpec
            {
                Name = "rightUpperArm",
                Required = true,
                ParentCandidates = new[] { "rightShoulder", "upperChest", "chest", "spine" }
            });

            specs.Add(new HumanoidBoneSpec
            {
                Name = "rightLowerArm",
                Required = true,
                ParentCandidates = new[] { "rightUpperArm" }
            });

            specs.Add(new HumanoidBoneSpec
            {
                Name = "rightHand",
                Required = true,
                ParentCandidates = new[] { "rightLowerArm" }
            });

            string[] sides = { "left", "right" };
            string[] fingerNames = { "Index", "Middle", "Ring", "Little" };

            for (int s = 0; s < sides.Length; s++)
            {
                string side = sides[s];
                string hand = side + "Hand";

                specs.Add(new HumanoidBoneSpec
                {
                    Name = side + "ThumbMetacarpal",
                    ParentCandidates = new[] { hand }
                });

                specs.Add(new HumanoidBoneSpec
                {
                    Name = side + "ThumbProximal",
                    ParentCandidates = new[] { side + "ThumbMetacarpal" }
                });

                specs.Add(new HumanoidBoneSpec
                {
                    Name = side + "ThumbDistal",
                    ParentCandidates = new[] { side + "ThumbProximal" }
                });

                for (int f = 0; f < fingerNames.Length; f++)
                {
                    string proximal = side + fingerNames[f] + "Proximal";
                    string intermediate = side + fingerNames[f] + "Intermediate";
                    string distal = side + fingerNames[f] + "Distal";

                    specs.Add(new HumanoidBoneSpec
                    {
                        Name = proximal,
                        ParentCandidates = new[] { hand }
                    });

                    specs.Add(new HumanoidBoneSpec
                    {
                        Name = intermediate,
                        ParentCandidates = new[] { proximal }
                    });

                    specs.Add(new HumanoidBoneSpec
                    {
                        Name = distal,
                        ParentCandidates = new[] { intermediate }
                    });
                }
            }

            return specs.ToArray();
        }

        static Dictionary<string, HumanoidBoneSpec> BuildByName()
        {
            var result = new Dictionary<string, HumanoidBoneSpec>(StringComparer.Ordinal);

            foreach (HumanoidBoneSpec spec in All)
                result[spec.Name] = spec;

            return result;
        }
    }

    public sealed class HumanoidBoneBinding
    {
        public string BoneName { get; set; } = "";

        public int BoneIndex { get; set; } = -1;

        public string SourceBoneName { get; set; } = "";

        public string Source { get; set; } = "";
    }

    public sealed class HumanoidRig
    {
        public string ModelKey { get; set; } = "";

        public string ProfileId { get; set; } = "";

        public bool IsValid { get; set; }

        public Dictionary<string, HumanoidBoneBinding> Bones { get; set; } = new(StringComparer.Ordinal);

        public List<string> Warnings { get; set; } = new();
    }

    public sealed class HumanoidProfile
    {
        public string ProfileId { get; set; } = "";

        public string DisplayName { get; set; } = "";

        public Dictionary<string, List<string>> Aliases { get; } = new(StringComparer.Ordinal);
    }

    public static class HumanoidProfiles
    {
        public static readonly HumanoidProfile Vrm1 = BuildVrm1();
        public static readonly HumanoidProfile Generic = BuildGeneric();
        public static readonly HumanoidProfile Mmd = BuildMmd();
        public static readonly HumanoidProfile Mixamo = BuildMixamo();
        public static readonly HumanoidProfile Unity = BuildUnity();
        public static readonly HumanoidProfile Vroid = BuildVroid();

        public static readonly HumanoidProfile[] All = { Vrm1, Generic, Mmd, Mixamo, Unity, Vroid };

        public static HumanoidProfile Resolve(string? profileId)
        {
            if (string.IsNullOrWhiteSpace(profileId))
                return Generic;

            for (int i = 0; i < All.Length; i++)
            {
                if (string.Equals(All[i].ProfileId, profileId, StringComparison.OrdinalIgnoreCase))
                    return All[i];
            }

            return Generic;
        }

        static HumanoidProfile BuildVrm1()
        {
            return new HumanoidProfile
            {
                ProfileId = "vrm1",
                DisplayName = "VRM 1.0"
            };
        }

        static HumanoidProfile BuildGeneric()
        {
            return new HumanoidProfile
            {
                ProfileId = "generic",
                DisplayName = "Generic"
            };
        }

        static HumanoidProfile BuildMmd()
        {
            HumanoidProfile profile = new HumanoidProfile
            {
                ProfileId = "mmd",
                DisplayName = "MikuMikuDance / PMX"
            };

            profile.Aliases["hips"] = new List<string> { "センター", "センタ", "腰" };
            profile.Aliases["spine"] = new List<string> { "上半身", "上半身1" };
            profile.Aliases["chest"] = new List<string> { "上半身2" };
            profile.Aliases["upperChest"] = new List<string> { "上半身3" };
            profile.Aliases["neck"] = new List<string> { "首" };
            profile.Aliases["head"] = new List<string> { "頭" };
            profile.Aliases["leftEye"] = new List<string> { "左目" };
            profile.Aliases["rightEye"] = new List<string> { "右目" };
            profile.Aliases["jaw"] = new List<string> { "あご", "顎" };

            string[] sides = { "left", "right" };
            string[] sidePrefixes = { "左", "右" };
            string[] fingerNames = { "Index", "Middle", "Ring", "Little" };
            string[] fingerPrefixes = { "人指", "中指", "薬指", "小指" };

            for (int s = 0; s < sides.Length; s++)
            {
                string side = sides[s];
                string prefix = sidePrefixes[s];

                profile.Aliases[side + "Shoulder"] = new List<string> { prefix + "肩" };
                profile.Aliases[side + "UpperArm"] = new List<string> { prefix + "腕" };
                profile.Aliases[side + "LowerArm"] = new List<string> { prefix + "ひじ", prefix + "肘" };
                profile.Aliases[side + "Hand"] = new List<string> { prefix + "手首", prefix + "手" };
                profile.Aliases[side + "UpperLeg"] = new List<string> { prefix + "足" };
                profile.Aliases[side + "LowerLeg"] = new List<string> { prefix + "ひざ", prefix + "膝" };
                profile.Aliases[side + "Foot"] = new List<string> { prefix + "足首" };
                profile.Aliases[side + "Toes"] = new List<string> { prefix + "つま先", prefix + "足先EX" };

                profile.Aliases[side + "ThumbMetacarpal"] = new List<string>
                {
                    prefix + "親指０",
                    prefix + "親指0",
                    prefix + "親指１",
                    prefix + "親指1"
                };

                profile.Aliases[side + "ThumbProximal"] = new List<string>
                {
                    prefix + "親指１",
                    prefix + "親指1",
                    prefix + "親指２",
                    prefix + "親指2"
                };

                profile.Aliases[side + "ThumbDistal"] = new List<string>
                {
                    prefix + "親指２",
                    prefix + "親指2"
                };

                for (int f = 0; f < fingerNames.Length; f++)
                {
                    string fingerPrefix = fingerPrefixes[f];

                    profile.Aliases[side + fingerNames[f] + "Proximal"] =
                        new List<string> { prefix + fingerPrefix + "１" };

                    profile.Aliases[side + fingerNames[f] + "Intermediate"] =
                        new List<string> { prefix + fingerPrefix + "２" };

                    profile.Aliases[side + fingerNames[f] + "Distal"] =
                        new List<string> { prefix + fingerPrefix + "３" };
                }
            }

            return profile;
        }

        static HumanoidProfile BuildMixamo()
        {
            HumanoidProfile profile = new HumanoidProfile
            {
                ProfileId = "mixamo",
                DisplayName = "Mixamo"
            };

            profile.Aliases["hips"] = new List<string> { "Hips" };
            profile.Aliases["spine"] = new List<string> { "Spine" };
            profile.Aliases["chest"] = new List<string> { "Spine1" };
            profile.Aliases["upperChest"] = new List<string> { "Spine2" };
            profile.Aliases["neck"] = new List<string> { "Neck" };
            profile.Aliases["head"] = new List<string> { "Head" };
            profile.Aliases["jaw"] = new List<string> { "Jaw" };

            string[] sides = { "left", "right" };
            string[] sideCapitals = { "Left", "Right" };
            string[] fingerNames = { "Index", "Middle", "Ring", "Little" };
            string[] mixamoFingerNames = { "Index", "Middle", "Ring", "Pinky" };

            for (int s = 0; s < sides.Length; s++)
            {
                string side = sides[s];
                string prefix = sideCapitals[s];

                profile.Aliases[side + "Eye"] = new List<string> { prefix + "Eye" };
                profile.Aliases[side + "Shoulder"] = new List<string> { prefix + "Shoulder" };
                profile.Aliases[side + "UpperArm"] = new List<string> { prefix + "Arm" };
                profile.Aliases[side + "LowerArm"] = new List<string> { prefix + "ForeArm" };
                profile.Aliases[side + "Hand"] = new List<string> { prefix + "Hand" };
                profile.Aliases[side + "UpperLeg"] = new List<string> { prefix + "UpLeg" };
                profile.Aliases[side + "LowerLeg"] = new List<string> { prefix + "Leg" };
                profile.Aliases[side + "Foot"] = new List<string> { prefix + "Foot" };
                profile.Aliases[side + "Toes"] = new List<string> { prefix + "ToeBase", prefix + "Toe_End" };

                profile.Aliases[side + "ThumbMetacarpal"] = new List<string> { prefix + "HandThumb1" };
                profile.Aliases[side + "ThumbProximal"] = new List<string> { prefix + "HandThumb2" };
                profile.Aliases[side + "ThumbDistal"] = new List<string> { prefix + "HandThumb3" };

                for (int f = 0; f < fingerNames.Length; f++)
                {
                    string mixamoFingerName = mixamoFingerNames[f];

                    profile.Aliases[side + fingerNames[f] + "Proximal"] =
                        new List<string> { prefix + "Hand" + mixamoFingerName + "1" };

                    profile.Aliases[side + fingerNames[f] + "Intermediate"] =
                        new List<string> { prefix + "Hand" + mixamoFingerName + "2" };

                    profile.Aliases[side + fingerNames[f] + "Distal"] =
                        new List<string> { prefix + "Hand" + mixamoFingerName + "3" };
                }
            }

            return profile;
        }

        static HumanoidProfile BuildUnity()
        {
            HumanoidProfile profile = new HumanoidProfile
            {
                ProfileId = "unity",
                DisplayName = "Unity Humanoid"
            };

            profile.Aliases["hips"] = new List<string> { "Hips" };
            profile.Aliases["spine"] = new List<string> { "Spine" };
            profile.Aliases["chest"] = new List<string> { "Chest" };
            profile.Aliases["upperChest"] = new List<string> { "UpperChest" };
            profile.Aliases["neck"] = new List<string> { "Neck" };
            profile.Aliases["head"] = new List<string> { "Head" };
            profile.Aliases["jaw"] = new List<string> { "Jaw" };

            string[] sides = { "left", "right" };
            string[] sideCapitals = { "Left", "Right" };
            string[] fingerNames = { "Index", "Middle", "Ring", "Little" };

            for (int s = 0; s < sides.Length; s++)
            {
                string side = sides[s];
                string prefix = sideCapitals[s];

                profile.Aliases[side + "Eye"] = new List<string> { prefix + "Eye" };
                profile.Aliases[side + "Shoulder"] = new List<string> { prefix + "Shoulder" };
                profile.Aliases[side + "UpperArm"] = new List<string> { prefix + "UpperArm" };
                profile.Aliases[side + "LowerArm"] = new List<string> { prefix + "LowerArm" };
                profile.Aliases[side + "Hand"] = new List<string> { prefix + "Hand" };
                profile.Aliases[side + "UpperLeg"] = new List<string> { prefix + "UpperLeg" };
                profile.Aliases[side + "LowerLeg"] = new List<string> { prefix + "LowerLeg" };
                profile.Aliases[side + "Foot"] = new List<string> { prefix + "Foot" };
                profile.Aliases[side + "Toes"] = new List<string> { prefix + "Toes" };

                profile.Aliases[side + "ThumbMetacarpal"] = new List<string> { prefix + "ThumbProximal" };
                profile.Aliases[side + "ThumbProximal"] = new List<string> { prefix + "ThumbIntermediate" };
                profile.Aliases[side + "ThumbDistal"] = new List<string> { prefix + "ThumbDistal" };

                for (int f = 0; f < fingerNames.Length; f++)
                {
                    profile.Aliases[side + fingerNames[f] + "Proximal"] =
                        new List<string> { prefix + fingerNames[f] + "Proximal" };

                    profile.Aliases[side + fingerNames[f] + "Intermediate"] =
                        new List<string> { prefix + fingerNames[f] + "Intermediate" };

                    profile.Aliases[side + fingerNames[f] + "Distal"] =
                        new List<string> { prefix + fingerNames[f] + "Distal" };
                }
            }

            return profile;
        }

        static HumanoidProfile BuildVroid()
        {
            HumanoidProfile profile = new HumanoidProfile
            {
                ProfileId = "vroid",
                DisplayName = "VRoid Studio"
            };

            profile.Aliases["hips"] = new List<string> { "J_Bip_C_Hips" };
            profile.Aliases["spine"] = new List<string> { "J_Bip_C_Spine" };
            profile.Aliases["chest"] = new List<string> { "J_Bip_C_Chest" };
            profile.Aliases["upperChest"] = new List<string> { "J_Bip_C_UpperChest" };
            profile.Aliases["neck"] = new List<string> { "J_Bip_C_Neck" };
            profile.Aliases["head"] = new List<string> { "J_Bip_C_Head" };
            profile.Aliases["jaw"] = new List<string> { "J_Adj_C_Jaw" };

            string[] sides = { "left", "right" };
            string[] sideMarkers = { "L", "R" };
            string[] fingerNames = { "Index", "Middle", "Ring", "Little" };

            for (int s = 0; s < sides.Length; s++)
            {
                string side = sides[s];
                string marker = sideMarkers[s];

                profile.Aliases[side + "Eye"] = new List<string> { "J_Adj_" + marker + "_FaceEye" };
                profile.Aliases[side + "Shoulder"] = new List<string> { "J_Bip_" + marker + "_Shoulder" };
                profile.Aliases[side + "UpperArm"] = new List<string> { "J_Bip_" + marker + "_UpperArm" };
                profile.Aliases[side + "LowerArm"] = new List<string> { "J_Bip_" + marker + "_LowerArm" };
                profile.Aliases[side + "Hand"] = new List<string> { "J_Bip_" + marker + "_Hand" };
                profile.Aliases[side + "UpperLeg"] = new List<string> { "J_Bip_" + marker + "_UpperLeg" };
                profile.Aliases[side + "LowerLeg"] = new List<string> { "J_Bip_" + marker + "_LowerLeg" };
                profile.Aliases[side + "Foot"] = new List<string> { "J_Bip_" + marker + "_Foot" };
                profile.Aliases[side + "Toes"] = new List<string> { "J_Bip_" + marker + "_ToeBase" };

                profile.Aliases[side + "ThumbMetacarpal"] = new List<string> { "J_Bip_" + marker + "_Thumb1" };
                profile.Aliases[side + "ThumbProximal"] = new List<string> { "J_Bip_" + marker + "_Thumb2" };
                profile.Aliases[side + "ThumbDistal"] = new List<string> { "J_Bip_" + marker + "_Thumb3" };

                for (int f = 0; f < fingerNames.Length; f++)
                {
                    profile.Aliases[side + fingerNames[f] + "Proximal"] =
                        new List<string> { "J_Bip_" + marker + "_" + fingerNames[f] + "1" };

                    profile.Aliases[side + fingerNames[f] + "Intermediate"] =
                        new List<string> { "J_Bip_" + marker + "_" + fingerNames[f] + "2" };

                    profile.Aliases[side + fingerNames[f] + "Distal"] =
                        new List<string> { "J_Bip_" + marker + "_" + fingerNames[f] + "3" };
                }
            }

            return profile;
        }
    }

    internal static class HumanoidRigBuilder
    {
        public static HumanoidRig Build(
            string modelKey,
            AvatarSkeleton skeleton,
            IEnumerable<KeyValuePair<string, int>>? authoritativeBones,
            HumanoidProfile profile)
        {
            if (skeleton == null)
                throw new ArgumentNullException(nameof(skeleton));

            if (profile == null)
                throw new ArgumentNullException(nameof(profile));

            var rig = new HumanoidRig
            {
                ModelKey = modelKey,
                ProfileId = profile.ProfileId
            };

            int boneCount = skeleton.Bones.Count;

            // 已被占用的源骨骼索引
            var boundSourceIndices = new HashSet<int>();

            string StripBonePrefix(string name)
            {
                int colonIndex = name.LastIndexOf(':');
                if (colonIndex < 0 || colonIndex >= name.Length - 1)
                    return name;

                return name[(colonIndex + 1)..];
            }

            // 权威数据优先
            if (authoritativeBones != null)
            {
                foreach (KeyValuePair<string, int> pair in authoritativeBones)
                {
                    if (HumanoidBoneSpecifications.Find(pair.Key) == null)
                        continue;

                    if (pair.Value < 0 || pair.Value >= boneCount)
                        continue;

                    if (!boundSourceIndices.Add(pair.Value))
                        continue;

                    rig.Bones[pair.Key] = new HumanoidBoneBinding
                    {
                        BoneName = pair.Key,
                        BoneIndex = pair.Value,
                        SourceBoneName = skeleton.Bones[pair.Value].Name,
                        Source = "vrm"
                    };
                }
            }

            // 按别名表补齐
            foreach (HumanoidBoneSpec spec in HumanoidBoneSpecifications.All)
            {
                if (rig.Bones.ContainsKey(spec.Name))
                    continue;

                if (!profile.Aliases.TryGetValue(spec.Name, out List<string>? aliases) || aliases.Count == 0)
                    continue;

                for (int a = 0; a < aliases.Count; a++)
                {
                    string alias = aliases[a];
                    if (string.IsNullOrWhiteSpace(alias))
                        continue;

                    int resolvedIndex = -1;

                    for (int i = 0; i < boneCount; i++)
                    {
                        if (boundSourceIndices.Contains(i))
                            continue;

                        string sourceName = skeleton.Bones[i].Name;

                        if (!string.Equals(sourceName, alias, StringComparison.Ordinal) &&
                            !string.Equals(sourceName, alias, StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(StripBonePrefix(sourceName), alias, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        resolvedIndex = i;
                        break;
                    }

                    if (resolvedIndex < 0)
                        continue;

                    boundSourceIndices.Add(resolvedIndex);
                    rig.Bones[spec.Name] = new HumanoidBoneBinding
                    {
                        BoneName = spec.Name,
                        BoneIndex = resolvedIndex,
                        SourceBoneName = skeleton.Bones[resolvedIndex].Name,
                        Source = "alias"
                    };

                    break;
                }
            }

            // 校验必需骨骼与父子层级
            foreach (HumanoidBoneSpec spec in HumanoidBoneSpecifications.All)
            {
                bool bound = rig.Bones.TryGetValue(spec.Name, out HumanoidBoneBinding? binding) &&
                             binding.BoneIndex >= 0;

                if (!bound)
                {
                    if (!spec.Required)
                        continue;

                    rig.Warnings.Add($"[X] Required humanoid bone '{spec.Name}' is not bound.");

                    rig.Bones[spec.Name] = new HumanoidBoneBinding
                    {
                        BoneName = spec.Name,
                        BoneIndex = -1,
                        SourceBoneName = "",
                        Source = "placeholder"
                    };

                    continue;
                }

                if (spec.ParentCandidates.Length == 0)
                    continue;

                // 沿源骨架父链上溯查找候选父骨
                bool parentSatisfied = false;
                int parentIndex = skeleton.Bones[binding!.BoneIndex].ParentIndex;

                while (parentIndex >= 0 && parentIndex < boneCount)
                {
                    for (int c = 0; c < spec.ParentCandidates.Length; c++)
                    {
                        if (!rig.Bones.TryGetValue(spec.ParentCandidates[c], out HumanoidBoneBinding? parentBinding))
                            continue;

                        if (parentBinding.BoneIndex != parentIndex)
                            continue;

                        parentSatisfied = true;
                        break;
                    }

                    if (parentSatisfied)
                        break;

                    parentIndex = skeleton.Bones[parentIndex].ParentIndex;
                }

                if (!parentSatisfied)
                {
                    rig.Warnings.Add(
                        $"[!] Humanoid bone '{spec.Name}' does not sit under any of "
                        + $"[{string.Join(", ", spec.ParentCandidates)}].");
                }
            }

            rig.IsValid = HumanoidBoneSpecifications.RequiredNames.All(name =>
                rig.Bones.TryGetValue(name, out HumanoidBoneBinding? requiredBinding) &&
                requiredBinding.BoneIndex >= 0);

            if (!rig.IsValid)
                Console.WriteLine($"[!] Humanoid rig for '{modelKey}' is incomplete ({profile.ProfileId} profile).");

            return rig;
        }

        static string BuildRigFilePath(string modelFilePath)
        {
            string directory = Path.GetDirectoryName(modelFilePath) ?? "";
            string name = Path.GetFileNameWithoutExtension(modelFilePath);
            return Path.Combine(directory, name + ".humanoid.json");
        }

        public static bool TryLoadRig(string modelFilePath, out HumanoidRig? rig)
        {
            rig = null;

            if (string.IsNullOrWhiteSpace(modelFilePath))
                return false;

            string fullPath = BuildRigFilePath(modelFilePath);
            if (!File.Exists(fullPath))
                return false;

            try
            {
                string json = File.ReadAllText(fullPath);

                rig = JsonSerializer.Deserialize<HumanoidRig>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (rig == null)
                    return false;

                Console.WriteLine($"[i] Loaded humanoid rig: {fullPath}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[!] Failed to load humanoid rig '{fullPath}': {ex.Message}");
                return false;
            }
        }

        public static void SaveRig(string modelFilePath, HumanoidRig rig)
        {
            if (rig == null || string.IsNullOrWhiteSpace(modelFilePath))
                return;

            try
            {
                string fullPath = BuildRigFilePath(modelFilePath);

                File.WriteAllText(fullPath, JsonSerializer.Serialize(rig, new JsonSerializerOptions
                {
                    WriteIndented = true
                }));

                Console.WriteLine($"[i] Saved humanoid rig: {fullPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[!] Failed to save humanoid rig for '{modelFilePath}': {ex.Message}");
            }
        }
    }
}
