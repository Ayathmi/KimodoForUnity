// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace KimodoForUnity.Tests
{
    /// <summary>Format, isolation and actual Animator playback regression tests.</summary>
    public sealed class KimodoTests
    {
        /// <summary>Units, handedness and child translations survive parsing.</summary>
        [Test]
        public void BvhPreservesChildTranslationAndScale()
        {
            var bvh = KimodoBvh.Parse("HIERARCHY ROOT Root { OFFSET 0 0 0 CHANNELS 0 JOINT Hips { OFFSET 0 100 0 CHANNELS 3 Xposition Yposition Zposition } } MOTION Frames: 1 Frame Time: 0.033333 100 20 300");
            bvh.LocalPose(0, 1, out var position, out var rotation);
            Assert.That(position.x, Is.EqualTo(-1).Within(0.0001));
            Assert.That(position.y, Is.EqualTo(1.2f).Within(0.0001));
            Assert.That(position.z, Is.EqualTo(3).Within(0.0001));
            Assert.That(Quaternion.Angle(rotation, Quaternion.identity), Is.LessThan(0.01f));
        }

        /// <summary>Intrinsic Euler channels are applied in their declared sequence.</summary>
        [Test]
        public void BvhRotationOrderIsNotAssumed()
        {
            var bvh = KimodoBvh.Parse("HIERARCHY ROOT Root { OFFSET 0 0 0 CHANNELS 2 Xrotation Zrotation } MOTION Frames: 1 Frame Time: 0.1 90 90");
            bvh.LocalPose(0, 0, out _, out var rotation);
            Assert.That(Vector3.Distance(rotation * Vector3.up, Vector3.right), Is.LessThan(0.0001f));
        }

        /// <summary>Malformed and hostile motion data is rejected before object creation.</summary>
        [TestCase("NaN")]
        [TestCase("Infinity")]
        [TestCase("1e38")]
        public void BvhRejectsNonFiniteValues(string number)
        {
            Assert.Throws<InvalidDataException>(() => KimodoBvh.Parse("HIERARCHY ROOT Root { OFFSET 0 0 0 CHANNELS 1 Xposition } MOTION Frames: 1 Frame Time: 0.1 " + number));
        }

        /// <summary>Both truncated and extra motion columns are rejected.</summary>
        [TestCase("")]
        [TestCase("1 2")]
        public void BvhRejectsWrongFrameTable(string table)
        {
            Assert.Throws<InvalidDataException>(() => KimodoBvh.Parse("HIERARCHY ROOT Root { OFFSET 0 0 0 CHANNELS 1 Xposition } MOTION Frames: 1 Frame Time: 0.1 " + table));
        }

        /// <summary>Duplicate names cannot produce ambiguous Unity bindings.</summary>
        [Test]
        public void BvhRejectsDuplicateNames()
        {
            Assert.Throws<InvalidDataException>(() => KimodoBvh.Parse("HIERARCHY ROOT Root { OFFSET 0 0 0 CHANNELS 0 JOINT Root { OFFSET 0 1 0 CHANNELS 1 Xrotation } } MOTION Frames: 1 Frame Time: 0.1 0"));
        }

        /// <summary>End Sites and scientific notation are accepted.</summary>
        [Test]
        public void BvhAcceptsEndSiteAndCultureIndependentNumbers()
        {
            var original = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                var bvh = KimodoBvh.Parse("HIERARCHY ROOT Root { OFFSET 1e-3 0 0 CHANNELS 1 Zrotation End Site { OFFSET 0 1 0 } } MOTION Frames: 1 Frame Time: 0.033333 0.5");
                Assert.That(bvh.Frames, Is.EqualTo(1));
                Assert.That(bvh.FrameTime, Is.EqualTo(0.033333f));
            }
            finally { CultureInfo.CurrentCulture = original; }
        }

        /// <summary>Known T-pose fixture produces a valid Humanoid Avatar.</summary>
        [Test]
        public void SyntheticSomaRigBuildsValidHumanAvatar()
        {
            using (var rig = new KimodoSourceRig(KimodoBvh.Parse(KimodoTestFixture.Motion())))
            {
                Assert.IsTrue(rig.Avatar.isValid);
                Assert.IsTrue(rig.Avatar.isHuman);
            }
        }

        /// <summary>Unity resolves phalanges rather than SOMA's extra metacarpals.</summary>
        [Test]
        public void SomaFingersResolveOnActualAnimator()
        {
            using (var rig = new KimodoSourceRig(KimodoBvh.Parse(KimodoTestFixture.Motion())))
            {
                var animator = rig.Root.GetComponent<Animator>();
                Assert.That(animator.GetBoneTransform(HumanBodyBones.LeftIndexProximal).name, Is.EqualTo("LeftHandIndex2"));
                Assert.That(animator.GetBoneTransform(HumanBodyBones.LeftIndexDistal).name, Is.EqualTo("LeftHandIndex4"));
                Assert.That(animator.GetBoneTransform(HumanBodyBones.RightThumbProximal).name, Is.EqualTo("RightHandThumb1"));
                Assert.That(animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg).name, Is.EqualTo("LeftLeg"));
                Assert.That(animator.GetBoneTransform(HumanBodyBones.LeftLowerLeg).name, Is.EqualTo("LeftShin"));
            }
        }

        /// <summary>Kimodo pelvis channels already include neutral height.</summary>
        [Test]
        public void SomaPelvisHeightIsNotAddedTwice()
        {
            using (var rig = new KimodoSourceRig(KimodoBvh.Parse(KimodoTestFixture.Motion())))
            {
                rig.Apply(0);
                var animator = rig.Root.GetComponent<Animator>();
                Assert.That(animator.GetBoneTransform(HumanBodyBones.Hips).position.y, Is.EqualTo(1f).Within(0.0001f));
                rig.Apply(1);
                Assert.That(animator.GetBoneTransform(HumanBodyBones.Hips).position.y, Is.EqualTo(1.2f).Within(0.0001f));
            }
        }

        /// <summary>Muscle curves really drive a separate Animator through Playables.</summary>
        [Test]
        public void BakedClipAnimatesIndependentHumanoid()
        {
            var motion = KimodoBvh.Parse(KimodoTestFixture.Motion());
            var clip = KimodoHumanoid.Bake(motion, false, false);
            using (var rig = new KimodoSourceRig(motion))
            {
                var graph = PlayableGraph.Create("Kimodo playback verification");
                try
                {
                    var animator = rig.Root.GetComponent<Animator>();
                    animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                    var playable = AnimationClipPlayable.Create(graph, clip);
                    var output = AnimationPlayableOutput.Create(graph, "Pose", animator);
                    output.SetSourcePlayable(playable);
                    graph.Play();
                    playable.SetTime(0);
                    graph.Evaluate(0);
                    var arm = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
                    var hips = animator.GetBoneTransform(HumanBodyBones.Hips);
                    var hand = animator.GetBoneTransform(HumanBodyBones.LeftHand);
                    var before = hand.position - hips.position;
                    var rotation = arm.localRotation;
                    playable.SetTime(motion.Duration * 0.99f);
                    graph.Evaluate(0);
                    Assert.That(Vector3.Distance(before, hand.position - hips.position), Is.GreaterThan(0.1f),
                        "The hand must move relative to the pelvis, not merely follow body translation.");
                    Assert.That(Quaternion.Angle(rotation, arm.localRotation), Is.GreaterThan(30f));
                    Assert.IsTrue(clip.humanMotion);
                    Assert.That(clip.frameRate, Is.EqualTo(30f).Within(0.01f));
                    Assert.That(clip.length, Is.EqualTo(motion.Duration).Within(0.0001f));
                }
                finally
                {
                    graph.Destroy();
                    UnityEngine.Object.DestroyImmediate(clip);
                }
            }
        }

        /// <summary>In-place removes planar motion but retains body vertical displacement.</summary>
        [Test]
        public void InPlaceDoesNotFlattenVerticalMovement()
        {
            var motion = KimodoBvh.Parse(KimodoTestFixture.Motion());
            var clip = KimodoHumanoid.Bake(motion, true, false);
            try
            {
                var x = AnimationUtility.GetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), "RootT.x"));
                var y = AnimationUtility.GetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), "RootT.y"));
                Assert.That(x.Evaluate(0), Is.EqualTo(x.Evaluate(motion.Duration)).Within(0.0001));
                Assert.That(Mathf.Abs(y.Evaluate(0) - y.Evaluate(motion.Duration)), Is.GreaterThan(0.05f));
            }
            finally { UnityEngine.Object.DestroyImmediate(clip); }
        }

        /// <summary>Process arguments cannot turn prompt punctuation into shell execution.</summary>
        [TestCase("", "\"\"")]
        [TestCase("hello world", "\"hello world\"")]
        [TestCase("D:\\a b\\", "\"D:\\a b\\\\\"")]
        [TestCase("a\"b", "\"a\\\"b\"")]
        public void WindowsArgumentQuoting(string value, string expected)
            => Assert.That(KimodoProcess.Quote(value), Is.EqualTo(expected));

        /// <summary>Runtime and probe execution have enforced offline settings.</summary>
        [Test]
        public void ProcessEnvironmentCannotFetchModels()
        {
            var config = new KimodoConfiguration { pythonPath = typeof(object).Assembly.Location, assetsRoot = Path.GetTempPath() };
            var info = KimodoProcess.StartInfo(config, new[] { "-B", "-c", "print(1)" }, Path.GetTempPath());
            Assert.IsFalse(info.UseShellExecute);
            Assert.That(info.EnvironmentVariables["HF_HUB_OFFLINE"], Is.EqualTo("1"));
            Assert.That(info.EnvironmentVariables["TRANSFORMERS_OFFLINE"], Is.EqualTo("1"));
            Assert.That(info.EnvironmentVariables["TEXT_ENCODER_DEVICE"], Is.EqualTo("cuda"));
            Assert.That(info.EnvironmentVariables["TEXT_ENCODER_MODE"], Is.EqualTo("api"));
            Assert.That(info.EnvironmentVariables["GRADIO_SERVER_NAME"], Is.EqualTo("127.0.0.1"));
            Assert.IsNull(info.EnvironmentVariables["HF_TOKEN"]);
        }

        /// <summary>Directory prefixes cannot escape output containment.</summary>
        [Test]
        public void PathBoundariesRejectTraversalAndPrefixConfusion()
        {
            var root = Path.Combine(Path.GetTempPath(), "Kimodo-Test");
            Assert.IsFalse(KimodoPaths.Within(root + "-other", root));
            Assert.Throws<ArgumentException>(() => KimodoPaths.Child(root, "..", "escape.json"));
            Assert.Throws<ArgumentException>(() => KimodoPaths.ValidateRoot(Application.dataPath));
        }

        /// <summary>Only an explicit completed probe enables generation.</summary>
        [Test]
        public void MissingDependenciesCannotEnableGeneration()
        {
            Assert.IsFalse(KimodoDependencyCheck.CanGenerate(null, new List<KimodoCheckItem>()));
            Assert.IsFalse(KimodoSession.IsInspected(new KimodoConfiguration()));
        }

        /// <summary>Model limits are validated before any process or folder is created.</summary>
        [TestCase(-1f)]
        [TestCase(10.1f)]
        [TestCase(float.NaN)]
        public void InvalidDurationIsRejected(float duration)
            => Assert.Throws<ArgumentException>(() => KimodoSession.ValidateRequest("A person walks.", duration, 100));
    }

    /// <summary>Original procedural test skeleton; no third-party avatar assets are distributed.</summary>
    public static class KimodoTestFixture
    {
        /// <summary>Returns two known motion frames on a simple standard T-pose.</summary>
        public static string Motion()
        {
            var names = new List<string>();
            var writer = new StringBuilder("HIERARCHY\nROOT Root {\nOFFSET 0 0 0\nCHANNELS 6 Xposition Yposition Zposition Zrotation Yrotation Xrotation\n");
            names.Add("Root");
            Add(writer, names, "Hips", "0 100 0", () =>
            {
                Add(writer, names, "Spine1", "0 10 0", () =>
                    Add(writer, names, "Spine2", "0 10 0", () =>
                        Add(writer, names, "Chest", "0 10 0", () =>
                        {
                            Add(writer, names, "Neck1", "0 15 0", () => Add(writer, names, "Head", "0 10 0", null));
                            foreach (var side in new[] { "Left", "Right" })
                            {
                                var sign = side == "Left" ? 1 : -1;
                                Add(writer, names, side + "Shoulder", sign * 8 + " 10 0", () =>
                                    Add(writer, names, side + "Arm", sign * 10 + " 0 0", () =>
                                        Add(writer, names, side + "ForeArm", sign * 25 + " 0 0", () =>
                                            Add(writer, names, side + "Hand", sign * 22 + " 0 0", () =>
                                            {
                                                Add(writer, names, side + "HandThumb1", sign * 2 + " -1 3", () =>
                                                    Add(writer, names, side + "HandThumb2", sign * 3 + " 0 1", () =>
                                                        Add(writer, names, side + "HandThumb3", sign * 2 + " 0 0", null)));
                                                Add(writer, names, side + "HandIndex1", sign * 2 + " 0 2", () =>
                                                    Add(writer, names, side + "HandIndex2", sign * 5 + " 0 0", () =>
                                                        Add(writer, names, side + "HandIndex3", sign * 3 + " 0 0", () =>
                                                            Add(writer, names, side + "HandIndex4", sign * 2 + " 0 0", null))));
                                            }))));
                            }
                        })));
                foreach (var side in new[] { "Left", "Right" })
                {
                    var sign = side == "Left" ? 1 : -1;
                    Add(writer, names, side + "Leg", sign * 10 + " -5 0", () =>
                        Add(writer, names, side + "Shin", "0 -40 0", () =>
                            Add(writer, names, side + "Foot", "0 -40 0", () =>
                                Add(writer, names, side + "ToeBase", "0 -5 12", null))));
                }
            }, true);
            writer.Append("}\nMOTION\nFrames: 2\nFrame Time: 0.0333333333\n");
            for (var frame = 0; frame < 2; frame++)
            {
                foreach (var name in names)
                {
                    if (name == "Root") writer.Append("0 0 0 0 0 0 ");
                    else if (name == "Hips") writer.Append(frame == 0 ? "0 100 0 0 0 0 " : "100 120 100 0 0 0 ");
                    else writer.Append(name == "LeftArm" && frame == 1 ? "60 0 0 " : "0 0 0 ");
                }
                writer.Append('\n');
            }
            return writer.ToString();
        }

        private static void Add(StringBuilder writer, List<string> names, string name, string offset, Action children, bool translation = false)
        {
            names.Add(name);
            writer.Append("JOINT ").Append(name).Append(" {\nOFFSET ").Append(offset).Append('\n');
            writer.Append(translation ? "CHANNELS 6 Xposition Yposition Zposition Zrotation Yrotation Xrotation\n" : "CHANNELS 3 Zrotation Yrotation Xrotation\n");
            children?.Invoke();
            writer.Append("}\n");
        }
    }
}
