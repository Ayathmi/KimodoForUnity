// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

namespace KimodoForUnity
{
    /// <summary>One BVH joint, including channels on non-root joints.</summary>
    public sealed class KimodoBvhJoint
    {
        /// <summary>Unique hierarchy name.</summary>
        public string Name;
        /// <summary>Parent index, or minus one.</summary>
        public int Parent;
        /// <summary>Rest offset in source centimetres.</summary>
        public Vector3 Offset;
        /// <summary>Declared channel order.</summary>
        public string[] Channels;
        /// <summary>Offset into one motion frame.</summary>
        public int ChannelStart;
    }

    /// <summary>Parsed motion preserving the source sampling interval.</summary>
    public sealed class KimodoBvh
    {
        /// <summary>Ordered skeleton.</summary>
        public KimodoBvhJoint[] Joints;
        /// <summary>Flat channel values.</summary>
        public float[] Samples;
        /// <summary>Declared frame count.</summary>
        public int Frames;
        /// <summary>Seconds per sample.</summary>
        public float FrameTime;
        /// <summary>Channels per frame.</summary>
        public int ChannelCount;
        /// <summary>Time of the final sample.</summary>
        public float Duration => (Frames - 1) * FrameTime;

        /// <summary>Reads a bounded text motion file.</summary>
        public static KimodoBvh Read(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("BVH not found.", path);
            if (new FileInfo(path).Length > 32 * 1024 * 1024)
                throw new InvalidDataException("BVH exceeds the 32 MiB import limit.");
            return Parse(File.ReadAllText(path));
        }

        /// <summary>Parses a BVH hierarchy and exact frame/channel table.</summary>
        public static KimodoBvh Parse(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length > 32 * 1024 * 1024)
                throw new InvalidDataException("Empty or oversized BVH.");
            return new Parser(text).Read();
        }

        /// <summary>Samples a joint in Unity metres with the right-to-left handed basis change.</summary>
        public void LocalPose(int frame, int joint, out Vector3 position, out Quaternion rotation)
        {
            if (frame < 0 || frame >= Frames) throw new ArgumentOutOfRangeException(nameof(frame));
            var item = Joints[joint];
            position = item.Offset;
            var sourceRotation = Quaternion.identity;
            for (var i = 0; i < item.Channels.Length; i++)
            {
                var value = Samples[frame * ChannelCount + item.ChannelStart + i];
                switch (item.Channels[i])
                {
                    case "Xposition": position.x += value; break;
                    case "Yposition": position.y += value; break;
                    case "Zposition": position.z += value; break;
                    case "Xrotation": sourceRotation *= Quaternion.AngleAxis(value, Vector3.right); break;
                    case "Yrotation": sourceRotation *= Quaternion.AngleAxis(value, Vector3.up); break;
                    case "Zrotation": sourceRotation *= Quaternion.AngleAxis(value, Vector3.forward); break;
                }
            }
            position = new Vector3(-position.x, position.y, position.z) * 0.01f;
            var basis = Matrix4x4.Scale(new Vector3(-1, 1, 1));
            rotation = (basis * Matrix4x4.Rotate(sourceRotation) * basis).rotation.normalized;
        }

        private sealed class Parser
        {
            private readonly MatchCollection tokens;
            private readonly List<KimodoBvhJoint> joints = new List<KimodoBvhJoint>();
            private readonly HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
            private int cursor;
            private int channels;

            public Parser(string text) { tokens = Regex.Matches(text, @"[{}]|[^\s{}]+"); }

            public KimodoBvh Read()
            {
                Expect("HIERARCHY");
                Expect("ROOT");
                Joint(-1, 0);
                Expect("MOTION");
                Expect("Frames:");
                var frames = Integer(1, 18000);
                Expect("Frame");
                Expect("Time:");
                var interval = Number();
                if (interval < 0.001f || interval > 1f)
                    throw new InvalidDataException("Frame Time must be between 0.001 and 1 second.");
                var count = checked(frames * channels);
                if (channels == 0 || count > 8000000 || tokens.Count - cursor != count)
                    throw new InvalidDataException("Frame/channel table size is invalid.");
                var samples = new float[count];
                for (var i = 0; i < count; i++) samples[i] = Number();
                return new KimodoBvh { Joints = joints.ToArray(), Frames = frames, FrameTime = interval, ChannelCount = channels, Samples = samples };
            }

            private void Joint(int parent, int depth)
            {
                if (depth > 64 || joints.Count >= 256) throw new InvalidDataException("Skeleton exceeds import limits.");
                var name = Next();
                if (name.IndexOfAny(new[] { '/', '\\', ':', '{', '}' }) >= 0 || !names.Add(name))
                    throw new InvalidDataException("Invalid or duplicate joint name: " + name);
                var joint = new KimodoBvhJoint { Name = name, Parent = parent };
                var index = joints.Count;
                joints.Add(joint);
                Expect("{");
                Expect("OFFSET");
                joint.Offset = new Vector3(Number(), Number(), Number());
                Expect("CHANNELS");
                var size = Integer(0, 6);
                joint.ChannelStart = channels;
                joint.Channels = new string[size];
                var unique = new HashSet<string>();
                for (var i = 0; i < size; i++)
                {
                    var channel = Next();
                    if (channel != "Xposition" && channel != "Yposition" && channel != "Zposition"
                        && channel != "Xrotation" && channel != "Yrotation" && channel != "Zrotation")
                        throw new InvalidDataException("Unknown BVH channel: " + channel);
                    if (!unique.Add(channel)) throw new InvalidDataException("Duplicate channel.");
                    joint.Channels[i] = channel;
                }
                channels += size;
                while (Peek() != "}")
                {
                    var word = Next();
                    if (word == "JOINT") Joint(index, depth + 1);
                    else if (word == "End")
                    {
                        Expect("Site"); Expect("{"); Expect("OFFSET");
                        Number(); Number(); Number(); Expect("}");
                    }
                    else throw new InvalidDataException("Unexpected hierarchy token: " + word);
                }
                Expect("}");
            }

            private string Peek()
            {
                if (cursor >= tokens.Count) throw new InvalidDataException("Unexpected end of BVH.");
                return tokens[cursor].Value;
            }
            private string Next() { var value = Peek(); cursor++; return value; }
            private void Expect(string expected)
            {
                if (Next() != expected) throw new InvalidDataException("Expected " + expected + ".");
            }
            private int Integer(int min, int max)
            {
                if (!int.TryParse(Next(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
                    throw new InvalidDataException("Integer outside import limits.");
                return value;
            }
            private float Number()
            {
                if (!float.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                    || float.IsNaN(value) || float.IsInfinity(value) || Math.Abs(value) > 10000000)
                    throw new InvalidDataException("Non-finite or excessive BVH number.");
                return value;
            }
        }
    }
}
