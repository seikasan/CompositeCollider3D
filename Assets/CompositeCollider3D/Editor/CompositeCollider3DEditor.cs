using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Composite = CompositeCollider3D.CompositeCollider3D;

namespace CompositeCollider3D.Editor
{
    [CustomEditor(typeof(Composite))]
    public sealed class CompositeCollider3DEditor : UnityEditor.Editor
    {
        private bool _showAdvanced;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_sources"), new GUIContent("Sources"), true);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_generationType"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_representation"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_material"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_includeLayers"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_excludeLayers"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_layerOverridePriority"));
            if (serializedObject.FindProperty("_representation").enumValueIndex ==
                (int)Composite.CollisionRepresentation.DynamicConvex)
            {
                _showAdvanced = EditorGUILayout.Foldout(_showAdvanced, "Advanced (CoACD)", true);
                if (_showAdvanced)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("_coacdThreshold"), new GUIContent("Concavity Threshold"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("_coacdSampleResolution"), new GUIContent("Sample Resolution"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("_coacdMctsIteration"), new GUIContent("MCTS Iterations"));
                    EditorGUI.indentLevel--;
                }
            }
            serializedObject.ApplyModifiedProperties();

            var composite = (Composite)target;
            if (composite.IsGeometryGenerationRunning)
            {
                EditorGUILayout.HelpBox("Generating geometry in the background. The previous Collider remains active until completion.", MessageType.Info);
                Repaint();
            }

            if (GUILayout.Button("Use Child Colliders"))
            {
                var children = composite.GetComponentsInChildren<Collider>(true);
                var excludedRoot = serializedObject.FindProperty("_generatedRoot").objectReferenceValue as GameObject;
                var sources = new List<Collider>();
                foreach (Collider child in children)
                {
                    if (child.transform != composite.transform &&
                        (excludedRoot == null || !child.transform.IsChildOf(excludedRoot.transform)))
                        sources.Add(child);
                }

                Undo.RecordObject(composite, "Use Child Colliders");
                serializedObject.Update();
                SerializedProperty entries = serializedObject.FindProperty("_sources");
                entries.arraySize = sources.Count;
                for (int i = 0; i < sources.Count; i++)
                {
                    entries.GetArrayElementAtIndex(i).objectReferenceValue = sources[i];
                }
                serializedObject.ApplyModifiedProperties();
            }

            if (GUILayout.Button("Generate Geometry"))
            {
                composite.RequestGeometryGeneration();
                EditorUtility.SetDirty(composite);
                if (composite.gameObject.scene.IsValid())
                {
                    EditorSceneManager.MarkSceneDirty(composite.gameObject.scene);
                }
            }

            using (new EditorGUI.DisabledScope(EditorApplication.isPlaying || composite.IsGeometryGenerationRunning || composite.GetGeneratedMeshes().Length == 0))
            {
                if (GUILayout.Button("Save Generated Meshes..."))
                {
                    SaveGeneratedMeshes(composite);
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Generated Info", EditorStyles.boldLabel);
            Mesh[] meshes = composite.GetGeneratedMeshes();
            var generatedRoot = serializedObject.FindProperty("_generatedRoot").objectReferenceValue as GameObject;
            string status = composite.IsGeometryGenerationRunning ? "Generating" :
                generatedRoot == null || meshes.Length == 0 ? "No result" :
                composite.IsGeneratedGeometryCurrent ? "Current" : "Out of date";
            EditorGUILayout.LabelField("Status", status);
            if (generatedRoot != null && meshes.Length > 0 && meshes[0] != null)
            {
                MeshCollider[] colliders = generatedRoot.GetComponentsInChildren<MeshCollider>(true);
                int convexParts = 0;
                foreach (MeshCollider collider in colliders)
                    if (collider.convex) convexParts++;
                EditorGUILayout.LabelField("Result Triangles", (meshes[0].GetIndexCount(0) / 3).ToString());
                EditorGUILayout.LabelField("Colliders", colliders.Length.ToString());
                EditorGUILayout.LabelField("Convex Parts", convexParts.ToString());
            }
            long total = serializedObject.FindProperty("_lastTotalMilliseconds").longValue;
            if (total > 0)
            {
                EditorGUILayout.LabelField("Last Total", total + " ms");
                EditorGUILayout.LabelField("Capture", serializedObject.FindProperty("_lastCaptureMilliseconds").longValue + " ms");
                EditorGUILayout.LabelField("Boolean", serializedObject.FindProperty("_lastBooleanMilliseconds").longValue + " ms");
                EditorGUILayout.LabelField("CoACD", serializedObject.FindProperty("_lastCoacdMilliseconds").longValue + " ms");
                EditorGUILayout.LabelField("Mesh / Collider Apply", serializedObject.FindProperty("_lastApplyMilliseconds").longValue + " ms");
            }
        }

        private static void SaveGeneratedMeshes(Composite composite)
        {
            string path = EditorUtility.SaveFilePanelInProject(
                "Save CompositeCollider3D Meshes", $"{composite.name} Colliders", "asset",
                "Choose an Assets folder for the generated meshes.");
            if (string.IsNullOrEmpty(path)) return;
            path = AssetDatabase.GenerateUniqueAssetPath(path);

            Mesh[] originals = composite.GetGeneratedMeshes();
            var saved = new Mesh[originals.Length];
            try
            {
                for (int i = 0; i < originals.Length; i++)
                {
                    saved[i] = Instantiate(originals[i]);
                    saved[i].name =
                        i == 0
                        ? "Composite3D Result"
                        : "Convex " + (i - 1);
                    if (i == 0)
                    {
                        AssetDatabase.CreateAsset(saved[i], path);
                    }
                    else
                    {
                        AssetDatabase.AddObjectToAsset(saved[i], saved[0]);
                    }
                }
                AssetDatabase.SaveAssets();
                composite.UseSavedMeshes(saved);
                EditorUtility.SetDirty(composite);
                if (composite.gameObject.scene.IsValid())
                {
                    EditorSceneManager.MarkSceneDirty(composite.gameObject.scene);
                }
                Debug.Log($"CompositeCollider3D meshes saved to {path}", composite);
            }
            catch (Exception e)
            {
                if (AssetDatabase.LoadAssetAtPath<Mesh>(path) != null)
                {
                    AssetDatabase.DeleteAsset(path);
                }
                else
                {
                    foreach (Mesh mesh in saved)
                    {
                        if (mesh != null)
                        {
                            DestroyImmediate(mesh);
                        }
                    }
                }
                Debug.LogError($"Could not save CompositeCollider3D meshes: {e}", composite);
            }
        }
    }

}
