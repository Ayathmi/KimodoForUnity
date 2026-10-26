// SPDX-License-Identifier: Apache-2.0
using System;
using UnityEditor;
using UnityEngine;

namespace KimodoForUnity
{
    /// <summary>Isolated preview scene; never samples or edits the selected project object.</summary>
    public sealed class KimodoPreview : IDisposable
    {
        private PreviewRenderUtility render;
        private KimodoSourceRig source;
        private HumanPoseHandler sourcePose;
        private HumanPoseHandler targetPose;
        private GameObject target;
        private Material material;
        private Transform[] jointMarkers;
        private Transform[] boneMarkers;
        private KimodoBvh motion;
        private HumanPose pose;

        /// <summary>Loads motion and optionally clones a valid target Humanoid.</summary>
        public void Load(KimodoBvh data, GameObject targetPrefab)
        {
            Dispose();
            motion = data;
            try
            {
                render = new PreviewRenderUtility();
                render.camera.fieldOfView = 32;
                render.camera.nearClipPlane = 0.01f;
                render.camera.farClipPlane = 100;
                render.camera.clearFlags = CameraClearFlags.Color;
                render.camera.backgroundColor = new Color(0.12f, 0.14f, 0.17f);
                render.lights[0].intensity = 1.4f;
                render.lights[0].transform.rotation = Quaternion.Euler(40, 30, 0);
                render.lights[1].intensity = 0.6f;
                render.ambientColor = new Color(0.5f, 0.5f, 0.5f);
                source = new KimodoSourceRig(data);
                render.AddSingleGO(source.Root);
                if (targetPrefab != null)
                {
                    target = UnityEngine.Object.Instantiate(targetPrefab);
                    target.hideFlags = HideFlags.HideAndDontSave;
                    target.transform.position = Vector3.zero;
                    target.transform.rotation = Quaternion.identity;
                    var animator = target.GetComponentInChildren<Animator>();
                    if (animator == null || animator.avatar == null || !animator.avatar.isValid || !animator.avatar.isHuman)
                        throw new InvalidOperationException("Target must have an Animator with a valid Humanoid Avatar.");
                    animator.runtimeAnimatorController = null;
                    animator.applyRootMotion = false;
                    foreach (var behaviour in target.GetComponentsInChildren<MonoBehaviour>())
                        behaviour.enabled = false;
                    render.AddSingleGO(target);
                    sourcePose = new HumanPoseHandler(source.Avatar, source.Root.transform);
                    targetPose = new HumanPoseHandler(animator.avatar, animator.transform);
                    pose = new HumanPose { muscles = new float[HumanTrait.MuscleCount] };
                }
                else
                {
                    var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Standard");
                    material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave, color = new Color(0.2f, 0.8f, 0.7f) };
                    if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", new Color(0.2f, 0.8f, 0.7f));
                    jointMarkers = new Transform[data.Joints.Length];
                    boneMarkers = new Transform[data.Joints.Length];
                    for (var i = 0; i < jointMarkers.Length; i++)
                    {
                        if (data.Joints[i].Parent < 0) continue;
                        jointMarkers[i] = Primitive(PrimitiveType.Sphere);
                        jointMarkers[i].localScale = Vector3.one * 0.026f;
                        if (data.Joints[data.Joints[i].Parent].Parent >= 0) boneMarkers[i] = Primitive(PrimitiveType.Cylinder);
                    }
                }
            }
            catch { Dispose(); throw; }
        }

        /// <summary>Renders one frame in the private preview scene.</summary>
        public void Draw(Rect rect, int frame, float yaw)
        {
            if (render == null || rect.width < 2 || rect.height < 2) return;
            source.Apply(Mathf.Clamp(frame, 0, motion.Frames - 1));
            if (targetPose != null)
            {
                sourcePose.GetHumanPose(ref pose);
                targetPose.SetHumanPose(ref pose);
            }
            else
            {
                for (var i = 0; i < jointMarkers.Length; i++)
                {
                    if (jointMarkers[i] == null) continue;
                    jointMarkers[i].position = source.Bones[i].position;
                    var parent = motion.Joints[i].Parent;
                    if (boneMarkers[i] == null) continue;
                    var start = source.Bones[parent].position;
                    var end = source.Bones[i].position;
                    var delta = end - start;
                    boneMarkers[i].position = (start + end) * 0.5f;
                    boneMarkers[i].rotation = delta.sqrMagnitude < 0.000001f ? Quaternion.identity : Quaternion.FromToRotation(Vector3.up, delta);
                    boneMarkers[i].localScale = new Vector3(0.012f, delta.magnitude * 0.5f, 0.012f);
                }
            }
            var bounds = new Bounds(source.Bones[Math.Min(1, source.Bones.Length - 1)].position, Vector3.one * 0.01f);
            if (target != null)
            {
                var renderers = target.GetComponentsInChildren<Renderer>();
                if (renderers.Length > 0)
                {
                    bounds = renderers[0].bounds;
                    foreach (var renderer in renderers) bounds.Encapsulate(renderer.bounds);
                }
            }
            else
                for (var i = 0; i < source.Bones.Length; i++)
                    if (motion.Joints[i].Parent >= 0) bounds.Encapsulate(source.Bones[i].position);
            var rotation = Quaternion.Euler(8, yaw, 0);
            var distance = Mathf.Max(2f, bounds.size.magnitude * 1.5f);
            render.camera.transform.position = bounds.center + rotation * new Vector3(0, 0, distance);
            render.camera.transform.LookAt(bounds.center);
            render.BeginPreview(rect, GUIStyle.none);
            render.Render(allowScriptableRenderPipeline: true);
            GUI.DrawTexture(rect, render.EndPreview(), ScaleMode.StretchToFill, false);
        }

        private Transform Primitive(PrimitiveType kind)
        {
            var item = GameObject.CreatePrimitive(kind);
            item.hideFlags = HideFlags.HideAndDontSave;
            item.transform.SetParent(source.Root.transform, false);
            UnityEngine.Object.DestroyImmediate(item.GetComponent<Collider>());
            item.GetComponent<Renderer>().sharedMaterial = material;
            return item.transform;
        }

        /// <summary>Releases all hidden scene objects and native handlers.</summary>
        public void Dispose()
        {
            sourcePose?.Dispose();
            targetPose?.Dispose();
            sourcePose = null;
            targetPose = null;
            source?.Dispose();
            source = null;
            if (target != null) UnityEngine.Object.DestroyImmediate(target);
            target = null;
            render?.Cleanup();
            render = null;
            if (material != null) UnityEngine.Object.DestroyImmediate(material);
            material = null;
        }
    }
}
