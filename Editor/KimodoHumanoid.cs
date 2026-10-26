// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace KimodoForUnity
{
    /// <summary>Temporary source rig constructed from the BVH standard T-pose.</summary>
    public sealed class KimodoSourceRig : IDisposable
    {
        /// <summary>Hidden container, never inserted into the user's saved scene.</summary>
        public GameObject Root { get; private set; }
        /// <summary>Ordered transforms matching the parsed motion.</summary>
        public Transform[] Bones { get; private set; }
        /// <summary>Built humanoid avatar.</summary>
        public Avatar Avatar { get; private set; }
        private readonly KimodoBvh motion;

        /// <summary>Builds a rest skeleton and its explicitly mapped avatar.</summary>
        public KimodoSourceRig(KimodoBvh data)
        {
            motion = data;
            Root = new GameObject("KimodoPreviewRoot") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                Bones = new Transform[data.Joints.Length];
                for (var i = 0; i < Bones.Length; i++)
                {
                    var item = data.Joints[i];
                    var bone = new GameObject(item.Name) { hideFlags = HideFlags.HideAndDontSave }.transform;
                    bone.SetParent(item.Parent < 0 ? Root.transform : Bones[item.Parent], false);
                    bone.localPosition = new Vector3(-item.Offset.x, item.Offset.y, item.Offset.z) * 0.01f;
                    Bones[i] = bone;
                }
                var human = new List<HumanBone>();
                foreach (var bone in Bones)
                {
                    var target = HumanName(bone.name);
                    if (target != null)
                        human.Add(new HumanBone { boneName = bone.name, humanName = target, limit = new HumanLimit { useDefaultValues = true } });
                }
                var skeleton = new List<SkeletonBone>
                {
                    new SkeletonBone { name = Root.name, position = Vector3.zero, rotation = Quaternion.identity, scale = Vector3.one }
                };
                foreach (var bone in Bones)
                    skeleton.Add(new SkeletonBone { name = bone.name, position = bone.localPosition, rotation = bone.localRotation, scale = bone.localScale });
                var description = new HumanDescription
                {
                    human = human.ToArray(), skeleton = skeleton.ToArray(),
                    upperArmTwist = 0.5f, lowerArmTwist = 0.5f, upperLegTwist = 0.5f, lowerLegTwist = 0.5f,
                    armStretch = 0.05f, legStretch = 0.05f, feetSpacing = 0, hasTranslationDoF = false
                };
                Avatar = AvatarBuilder.BuildHumanAvatar(Root, description);
                Avatar.name = "Kimodo source avatar";
                Avatar.hideFlags = HideFlags.HideAndDontSave;
                if (!Avatar.isValid || !Avatar.isHuman)
                    throw new InvalidOperationException("BVH is not a valid SOMA standard T-pose humanoid. Use --bvh_standard_tpose.");
                var animator = Root.AddComponent<Animator>();
                animator.avatar = Avatar;
                animator.applyRootMotion = false;
            }
            catch { Dispose(); throw; }
        }

        /// <summary>Applies one sampled pose without touching any user's scene object.</summary>
        public void Apply(int frame)
        {
            for (var i = 0; i < Bones.Length; i++)
            {
                motion.LocalPose(frame, i, out var position, out var rotation);
                    // Kimodo exports Hips channels as absolute pelvis position; its neutral offset is already included.
                    if (motion.Joints[i].Name == "Hips")
                    {
                        var joint = motion.Joints[i];
                        if (Array.IndexOf(joint.Channels, "Xposition") < 0 ||
                            Array.IndexOf(joint.Channels, "Yposition") < 0 ||
                            Array.IndexOf(joint.Channels, "Zposition") < 0)
                            throw new InvalidOperationException("SOMA Hips requires three position channels.");
                        position -= new Vector3(-joint.Offset.x, joint.Offset.y, joint.Offset.z) * 0.01f;
                    }
                Bones[i].SetLocalPositionAndRotation(position, rotation);
            }
        }

        /// <summary>Disposes temporary native Unity resources.</summary>
        public void Dispose()
        {
            if (Root != null) UnityEngine.Object.DestroyImmediate(Root);
            if (Avatar != null) UnityEngine.Object.DestroyImmediate(Avatar);
            Root = null;
            Avatar = null;
        }

        /// <summary>Maps the official SOMA joint names to Unity's Humanoid names.</summary>
        public static string HumanName(string source)
        {
            switch (source)
            {
                case "Hips": return "Hips";
                case "Spine1": return "Spine";
                case "Spine2": return "Chest";
                case "Chest": return "UpperChest";
                case "Neck1": return "Neck";
                case "Head": return "Head";
            }
            foreach (var side in new[] { "Left", "Right" })
            {
                if (!source.StartsWith(side, StringComparison.Ordinal)) continue;
                var part = source.Substring(side.Length);
                switch (part)
                {
                    case "Shoulder": return side + "Shoulder";
                    case "Arm": return side + "UpperArm";
                    case "ForeArm": return side + "LowerArm";
                    case "Hand": return side + "Hand";
                    case "Leg": return side + "UpperLeg";
                    case "Shin": return side + "LowerLeg";
                    case "Foot": return side + "Foot";
                    case "ToeBase": return side + "Toes";
                }
                foreach (var finger in new[] { "Thumb", "Index", "Middle", "Ring", "Pinky" })
                {
                    var prefix = "Hand" + finger;
                    if (!part.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    var suffix = part.Substring(prefix.Length);
                    // SOMA's non-thumb digit 1 is a metacarpal; Unity starts at the proximal phalanx.
                    var segment = finger == "Thumb"
                        ? suffix == "1" ? "Proximal" : suffix == "2" ? "Intermediate" : suffix == "3" ? "Distal" : null
                        : suffix == "2" ? "Proximal" : suffix == "3" ? "Intermediate" : suffix == "4" ? "Distal" : null;
                    if (segment != null) return side + " " + (finger == "Pinky" ? "Little" : finger) + " " + segment;
                }
            }
            return null;
        }
    }

    /// <summary>Humanoid curve baking with explicit root-motion policy.</summary>
    public static class KimodoHumanoid
    {
        /// <summary>Bakes muscles and body motion into an independent non-Legacy clip.</summary>
        public static AnimationClip Bake(KimodoBvh motion, bool inPlace, bool loop)
        {
            var clip = new AnimationClip { name = "Kimodo motion", frameRate = 1f / motion.FrameTime, legacy = false };
            try
            {
                using (var rig = new KimodoSourceRig(motion))
                using (var handler = new HumanPoseHandler(rig.Avatar, rig.Root.transform))
                {
                    var curves = new AnimationCurve[HumanTrait.MuscleCount + 7];
                    for (var i = 0; i < curves.Length; i++) curves[i] = new AnimationCurve();
                    var pose = new HumanPose { muscles = new float[HumanTrait.MuscleCount] };
                    var origin = Vector3.zero;
                    var previous = Quaternion.identity;
                    for (var frame = 0; frame < motion.Frames; frame++)
                    {
                        rig.Apply(frame);
                        handler.GetHumanPose(ref pose);
                        if (frame == 0) origin = pose.bodyPosition;
                        var position = pose.bodyPosition;
                        // Preserve vertical body movement; only planar translation becomes in-place.
                        if (inPlace) { position.x = origin.x; position.z = origin.z; }
                        var rotation = pose.bodyRotation.normalized;
                        if (frame > 0 && Quaternion.Dot(previous, rotation) < 0)
                            rotation = new Quaternion(-rotation.x, -rotation.y, -rotation.z, -rotation.w);
                        previous = rotation;
                        var time = frame * motion.FrameTime;
                        for (var muscle = 0; muscle < HumanTrait.MuscleCount; muscle++)
                        {
                            if (float.IsNaN(pose.muscles[muscle]) || float.IsInfinity(pose.muscles[muscle]))
                                throw new InvalidOperationException("Non-finite humanoid pose.");
                            curves[muscle].AddKey(time, pose.muscles[muscle]);
                        }
                        var start = HumanTrait.MuscleCount;
                        for (var axis = 0; axis < 3; axis++) curves[start + axis].AddKey(time, position[axis]);
                        for (var axis = 0; axis < 4; axis++) curves[start + 3 + axis].AddKey(time, rotation[axis]);
                    }
                    for (var i = 0; i < curves.Length; i++)
                    {
                        for (var key = 0; key < curves[i].length; key++)
                        {
                            AnimationUtility.SetKeyLeftTangentMode(curves[i], key, AnimationUtility.TangentMode.Linear);
                            AnimationUtility.SetKeyRightTangentMode(curves[i], key, AnimationUtility.TangentMode.Linear);
                        }
                        var muscleCount = HumanTrait.MuscleCount;
                        var name = i < muscleCount ? HumanTrait.MuscleName[i]
                            : i < muscleCount + 3 ? "RootT." + "xyz"[i - muscleCount]
                            : "RootQ." + "xyzw"[i - muscleCount - 3];
                        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), name), curves[i]);
                    }
                    clip.EnsureQuaternionContinuity();
                    var settings = AnimationUtility.GetAnimationClipSettings(clip);
                    settings.loopTime = loop;
                    settings.loopBlend = false;
                    settings.startTime = 0;
                    settings.stopTime = motion.Duration;
                    settings.keepOriginalPositionXZ = true;
                    settings.keepOriginalPositionY = true;
                    settings.keepOriginalOrientation = true;
                    AnimationUtility.SetAnimationClipSettings(clip, settings);
                }
                if (!clip.humanMotion) throw new InvalidOperationException("Unity did not recognize the baked clip as Humanoid.");
                return clip;
            }
            catch { UnityEngine.Object.DestroyImmediate(clip); throw; }
        }
    }
}
